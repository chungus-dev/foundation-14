// Scp added start - deterministic persistent shadow-atlas allocation.
using System;
using System.Numerics;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Deterministic two-dimensional buddy allocator. Width and height use independent
/// power-of-two classes, so a narrow shadow no longer consumes a square slot.
/// </summary>
internal sealed class ScpShadowAtlasBuddyAllocator
{
    public const int AtlasSize = 2048;
    public const int MinimumBlockSize = 32;

    private const int OrderCount = 7;
    private const int MaximumOrder = OrderCount - 1;
    private const int MinimumCellCount = AtlasSize / MinimumBlockSize;
    private const int MaximumLeafCount = MinimumCellCount * MinimumCellCount;
    private const int MaximumNodeCount = MaximumLeafCount * 2 - 1;
    // Seven int fields plus the byte state occupy at most 32 bytes with the
    // natural alignment used by supported runtimes. Avoid Unsafe.SizeOf here:
    // Content's runtime sandbox intentionally rejects that API.
    private const int NodeElementBytes = 32;
    private const int IntElementBytes = sizeof(int);
    private const int UlongElementBytes = sizeof(ulong);
    private const long ManagedArrayHeaderBytes = 24L;
    // Includes the managed object header, references to all backing arrays and
    // the allocator's scalar fields. Keep this deliberately conservative: the
    // CPU budget must never mistake object bookkeeping for free space.
    private const long AllocatorInstanceBytes = 128L;

    private readonly Node[] _nodes = new Node[MaximumNodeCount];
    private readonly int[] _releasedNodes = new int[MaximumNodeCount];
    private readonly int[][] _allocatedNodes = CreateAllocatedNodeMaps();
    private readonly int[][] _freeNodes = CreateAllocatedNodeMaps();
    private readonly ulong[][] _freeBits = CreateFreeBitMaps();
    private int _nextNode;
    private int _releasedNodeCount;

    /// <summary>
    /// Raw payload of every fixed backing array, including the reference slots
    /// in the three jagged-array roots. Exposed for a narrow accounting test.
    /// </summary>
    internal long BackingPayloadBytes { get; }

    /// <summary>
    /// Conservative managed CPU footprint. Computed once from the actual array
    /// lengths, so allocation, freeing and reset never add work to the hot path.
    /// </summary>
    internal long EstimatedBytes { get; }

    public ScpShadowAtlasBuddyAllocator()
    {
        BackingPayloadBytes = CalculateBackingPayloadBytes();
        EstimatedBytes = CalculateEstimatedBytes();
        Reset();
    }

    private long CalculateBackingPayloadBytes()
    {
        return GetArrayPayloadBytes(_nodes, NodeElementBytes) +
               GetArrayPayloadBytes(_releasedNodes, IntElementBytes) +
               GetJaggedArrayPayloadBytes(_allocatedNodes, IntElementBytes) +
               GetJaggedArrayPayloadBytes(_freeNodes, IntElementBytes) +
               GetJaggedArrayPayloadBytes(_freeBits, UlongElementBytes);
    }

    private long CalculateEstimatedBytes()
    {
        return AllocatorInstanceBytes +
               GetEstimatedArrayBytes(_nodes, NodeElementBytes) +
               GetEstimatedArrayBytes(_releasedNodes, IntElementBytes) +
               GetEstimatedJaggedArrayBytes(_allocatedNodes, IntElementBytes) +
               GetEstimatedJaggedArrayBytes(_freeNodes, IntElementBytes) +
               GetEstimatedJaggedArrayBytes(_freeBits, UlongElementBytes);
    }

    private static long GetArrayPayloadBytes<T>(T[] values, int elementBytes)
    {
        return (long) values.Length * elementBytes;
    }

    private static long GetJaggedArrayPayloadBytes<T>(T[][] values, int elementBytes)
    {
        var result = (long) values.Length * IntPtr.Size;
        for (var index = 0; index < values.Length; index++)
            result += GetArrayPayloadBytes(values[index], elementBytes);
        return result;
    }

    private static long GetEstimatedArrayBytes<T>(T[] values, int elementBytes)
    {
        return AlignManagedObject(
            ManagedArrayHeaderBytes + GetArrayPayloadBytes(values, elementBytes));
    }

    private static long GetEstimatedJaggedArrayBytes<T>(T[][] values, int elementBytes)
    {
        var result = AlignManagedObject(
            ManagedArrayHeaderBytes + (long) values.Length * IntPtr.Size);
        for (var index = 0; index < values.Length; index++)
            result += GetEstimatedArrayBytes(values[index], elementBytes);
        return result;
    }

    private static long AlignManagedObject(long bytes)
    {
        var alignment = IntPtr.Size;
        return (bytes + alignment - 1L) & -alignment;
    }

    public void Reset()
    {
        Array.Clear(_nodes);
        for (var index = 0; index < _allocatedNodes.Length; index++)
        {
            Array.Clear(_allocatedNodes[index]);
            Array.Clear(_freeNodes[index]);
            Array.Clear(_freeBits[index]);
        }

        _nextNode = 1;
        _releasedNodeCount = 0;
        _nodes[0] = new Node
        {
            WidthOrder = MaximumOrder,
            HeightOrder = MaximumOrder,
            Parent = -1,
            FirstChild = -1,
            SecondChild = -1,
            State = NodeState.Free,
        };
        AddFreeNode(0);
    }

    public bool TryAllocate(Vector2i requestedSize, out ScpShadowAtlasSlot slot)
    {
        if (!TryGetRequiredBlockSize(requestedSize, out var blockSize))
        {
            slot = default;
            return false;
        }

        var targetWidthOrder = GetOrder(blockSize.X);
        var targetHeightOrder = GetOrder(blockSize.Y);
        var nodeIndex = FindBestFreeNode(targetWidthOrder, targetHeightOrder);
        if (nodeIndex < 0)
        {
            slot = default;
            return false;
        }

        while (_nodes[nodeIndex].WidthOrder > targetWidthOrder ||
               _nodes[nodeIndex].HeightOrder > targetHeightOrder)
        {
            ref readonly var node = ref _nodes[nodeIndex];
            var widthExcess = node.WidthOrder - targetWidthOrder;
            var heightExcess = node.HeightOrder - targetHeightOrder;
            var splitWidth = widthExcess >= heightExcess && widthExcess > 0;
            nodeIndex = Split(nodeIndex, splitWidth);
        }

        RemoveFreeNode(nodeIndex);
        ref var allocated = ref _nodes[nodeIndex];
        allocated.State = NodeState.Allocated;
        slot = new ScpShadowAtlasSlot(
            allocated.X,
            allocated.Y,
            MinimumBlockSize << allocated.WidthOrder,
            MinimumBlockSize << allocated.HeightOrder);
        SetAllocatedNode(slot, nodeIndex + 1);
        return true;
    }

    public static bool TryGetRequiredBlockSize(Vector2i requestedSize, out Vector2i blockSize)
    {
        if (requestedSize.X <= 0 || requestedSize.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSize));

        if (requestedSize.X > AtlasSize || requestedSize.Y > AtlasSize)
        {
            blockSize = default;
            return false;
        }

        blockSize = new Vector2i(
            (int) BitOperations.RoundUpToPowerOf2((uint) Math.Max(requestedSize.X, MinimumBlockSize)),
            (int) BitOperations.RoundUpToPowerOf2((uint) Math.Max(requestedSize.Y, MinimumBlockSize)));
        return true;
    }

    public bool Free(ScpShadowAtlasSlot slot)
    {
        if (!TryValidateSlot(slot, out var widthOrder, out var heightOrder))
            return false;

        var allocatedMap = _allocatedNodes[GetClassIndex(widthOrder, heightOrder)];
        var allocatedIndex = GetClassPosition(slot.X, slot.Y, widthOrder, heightOrder);
        var nodeIndex = allocatedMap[allocatedIndex] - 1;
        if (nodeIndex < 0 ||
            nodeIndex >= _nextNode ||
            !MatchesAllocatedNode(nodeIndex, slot))
        {
            return false;
        }

        allocatedMap[allocatedIndex] = 0;
        _nodes[nodeIndex].State = NodeState.Free;
        AddFreeNode(nodeIndex);

        while (_nodes[nodeIndex].Parent >= 0)
        {
            var parentIndex = _nodes[nodeIndex].Parent;
            ref var parent = ref _nodes[parentIndex];
            var siblingIndex = parent.FirstChild == nodeIndex
                ? parent.SecondChild
                : parent.FirstChild;

            if (_nodes[siblingIndex].State != NodeState.Free)
                break;

            RemoveFreeNode(nodeIndex);
            RemoveFreeNode(siblingIndex);
            ReleaseNode(parent.FirstChild);
            ReleaseNode(parent.SecondChild);
            parent.FirstChild = -1;
            parent.SecondChild = -1;
            parent.State = NodeState.Free;
            nodeIndex = parentIndex;
            AddFreeNode(nodeIndex);
        }

        return true;
    }

    private int FindBestFreeNode(int targetWidthOrder, int targetHeightOrder)
    {
        var bestIndex = -1;
        var bestAreaOrder = int.MaxValue;
        var bestMaximumExcess = int.MaxValue;
        var bestY = int.MaxValue;
        var bestX = int.MaxValue;

        for (var heightOrder = targetHeightOrder; heightOrder <= MaximumOrder; heightOrder++)
        {
            for (var widthOrder = targetWidthOrder; widthOrder <= MaximumOrder; widthOrder++)
            {
                var classIndex = GetClassIndex(widthOrder, heightOrder);
                var position = FindFirstSet(_freeBits[classIndex]);
                if (position < 0)
                    continue;

                var encodedNodeIndex = _freeNodes[classIndex][position];
                if (encodedNodeIndex == 0)
                    throw new InvalidOperationException("Shadow atlas free bitmap is inconsistent with its node map.");

                var index = encodedNodeIndex - 1;
                ref readonly var node = ref _nodes[index];
                var areaOrder = widthOrder + heightOrder;
                var maximumExcess = Math.Max(
                    widthOrder - targetWidthOrder,
                    heightOrder - targetHeightOrder);
                if (areaOrder > bestAreaOrder ||
                    areaOrder == bestAreaOrder && maximumExcess > bestMaximumExcess ||
                    areaOrder == bestAreaOrder && maximumExcess == bestMaximumExcess && node.Y > bestY ||
                    areaOrder == bestAreaOrder && maximumExcess == bestMaximumExcess && node.Y == bestY && node.X >= bestX)
                {
                    continue;
                }

                bestIndex = index;
                bestAreaOrder = areaOrder;
                bestMaximumExcess = maximumExcess;
                bestY = node.Y;
                bestX = node.X;
            }
        }

        return bestIndex;
    }

    private int Split(int parentIndex, bool splitWidth)
    {
        ref var parent = ref _nodes[parentIndex];
        RemoveFreeNode(parentIndex);
        var firstIndex = AcquireNode();
        var secondIndex = AcquireNode();
        var widthOrder = parent.WidthOrder - (splitWidth ? 1 : 0);
        var heightOrder = parent.HeightOrder - (splitWidth ? 0 : 1);
        var childWidth = MinimumBlockSize << widthOrder;
        var childHeight = MinimumBlockSize << heightOrder;

        _nodes[firstIndex] = new Node
        {
            X = parent.X,
            Y = parent.Y,
            WidthOrder = widthOrder,
            HeightOrder = heightOrder,
            Parent = parentIndex,
            FirstChild = -1,
            SecondChild = -1,
            State = NodeState.Free,
        };
        _nodes[secondIndex] = new Node
        {
            X = parent.X + (splitWidth ? childWidth : 0),
            Y = parent.Y + (splitWidth ? 0 : childHeight),
            WidthOrder = widthOrder,
            HeightOrder = heightOrder,
            Parent = parentIndex,
            FirstChild = -1,
            SecondChild = -1,
            State = NodeState.Free,
        };

        parent.FirstChild = firstIndex;
        parent.SecondChild = secondIndex;
        parent.State = NodeState.Split;
        AddFreeNode(firstIndex);
        AddFreeNode(secondIndex);
        return firstIndex;
    }

    private int AcquireNode()
    {
        if (_releasedNodeCount > 0)
            return _releasedNodes[--_releasedNodeCount];

        if (_nextNode >= MaximumNodeCount)
            throw new InvalidOperationException("Shadow atlas allocator exhausted its fixed node pool.");

        return _nextNode++;
    }

    private void ReleaseNode(int index)
    {
        _nodes[index] = default;
        _releasedNodes[_releasedNodeCount++] = index;
    }

    private static int[][] CreateAllocatedNodeMaps()
    {
        var result = new int[OrderCount * OrderCount][];
        for (var heightOrder = 0; heightOrder < OrderCount; heightOrder++)
        {
            for (var widthOrder = 0; widthOrder < OrderCount; widthOrder++)
            {
                var columns = AtlasSize / (MinimumBlockSize << widthOrder);
                var rows = AtlasSize / (MinimumBlockSize << heightOrder);
                result[GetClassIndex(widthOrder, heightOrder)] = new int[columns * rows];
            }
        }

        return result;
    }

    private static ulong[][] CreateFreeBitMaps()
    {
        var result = new ulong[OrderCount * OrderCount][];
        for (var heightOrder = 0; heightOrder < OrderCount; heightOrder++)
        {
            for (var widthOrder = 0; widthOrder < OrderCount; widthOrder++)
            {
                var columns = AtlasSize / (MinimumBlockSize << widthOrder);
                var rows = AtlasSize / (MinimumBlockSize << heightOrder);
                result[GetClassIndex(widthOrder, heightOrder)] = new ulong[(columns * rows + 63) / 64];
            }
        }

        return result;
    }

    private void AddFreeNode(int nodeIndex)
    {
        ref readonly var node = ref _nodes[nodeIndex];
        var classIndex = GetClassIndex(node.WidthOrder, node.HeightOrder);
        var position = GetClassPosition(
            node.X,
            node.Y,
            node.WidthOrder,
            node.HeightOrder);
        _freeNodes[classIndex][position] = nodeIndex + 1;
        _freeBits[classIndex][position / 64] |= 1UL << position % 64;
    }

    private void RemoveFreeNode(int nodeIndex)
    {
        ref readonly var node = ref _nodes[nodeIndex];
        var classIndex = GetClassIndex(node.WidthOrder, node.HeightOrder);
        var position = GetClassPosition(
            node.X,
            node.Y,
            node.WidthOrder,
            node.HeightOrder);
        if (_freeNodes[classIndex][position] != nodeIndex + 1)
            throw new InvalidOperationException("Shadow atlas free-node map is inconsistent.");

        _freeNodes[classIndex][position] = 0;
        _freeBits[classIndex][position / 64] &= ~(1UL << position % 64);
    }

    private void SetAllocatedNode(ScpShadowAtlasSlot slot, int encodedNodeIndex)
    {
        var widthOrder = GetOrder(slot.Width);
        var heightOrder = GetOrder(slot.Height);
        _allocatedNodes[GetClassIndex(widthOrder, heightOrder)][
            GetClassPosition(slot.X, slot.Y, widthOrder, heightOrder)] = encodedNodeIndex;
    }

    private bool MatchesAllocatedNode(int nodeIndex, ScpShadowAtlasSlot slot)
    {
        ref readonly var node = ref _nodes[nodeIndex];
        return node.State == NodeState.Allocated &&
               node.X == slot.X &&
               node.Y == slot.Y &&
               MinimumBlockSize << node.WidthOrder == slot.Width &&
               MinimumBlockSize << node.HeightOrder == slot.Height;
    }

    private static bool TryValidateSlot(
        ScpShadowAtlasSlot slot,
        out int widthOrder,
        out int heightOrder)
    {
        if (slot.Width < MinimumBlockSize ||
            slot.Width > AtlasSize ||
            slot.Height < MinimumBlockSize ||
            slot.Height > AtlasSize ||
            !BitOperations.IsPow2((uint) slot.Width) ||
            !BitOperations.IsPow2((uint) slot.Height) ||
            slot.X < 0 ||
            slot.Y < 0 ||
            slot.X + slot.Width > AtlasSize ||
            slot.Y + slot.Height > AtlasSize ||
            slot.X % slot.Width != 0 ||
            slot.Y % slot.Height != 0)
        {
            widthOrder = 0;
            heightOrder = 0;
            return false;
        }

        widthOrder = GetOrder(slot.Width);
        heightOrder = GetOrder(slot.Height);
        return true;
    }

    private static int GetOrder(int size)
    {
        return BitOperations.Log2((uint) (size / MinimumBlockSize));
    }

    private static int GetClassIndex(int widthOrder, int heightOrder)
    {
        return heightOrder * OrderCount + widthOrder;
    }

    private static int GetClassPosition(int x, int y, int widthOrder, int heightOrder)
    {
        var width = MinimumBlockSize << widthOrder;
        var height = MinimumBlockSize << heightOrder;
        var columns = AtlasSize / width;
        return y / height * columns + x / width;
    }

    private static int FindFirstSet(ulong[] bits)
    {
        for (var index = 0; index < bits.Length; index++)
        {
            if (bits[index] != 0)
                return index * 64 + BitOperations.TrailingZeroCount(bits[index]);
        }

        return -1;
    }

    private struct Node
    {
        public int X;
        public int Y;
        public int Parent;
        public int FirstChild;
        public int SecondChild;
        public int WidthOrder;
        public int HeightOrder;
        public NodeState State;
    }

    private enum NodeState : byte
    {
        Unused,
        Free,
        Split,
        Allocated,
    }
}

internal readonly record struct ScpShadowAtlasSlot(int X, int Y, int Width, int Height)
{
    public ScpShadowAtlasSlot(int x, int y, int size) : this(x, y, size, size)
    {
    }

    public UIBox2i Bounds => UIBox2i.FromDimensions(X, Y, Width, Height);

    public long Area => (long) Width * Height;
}
// Scp added end - deterministic persistent shadow-atlas allocation.
