using System.Numerics;
using Content.Client.Clickable;
using Robust.Client.Graphics;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Lazily converts cached sprite alpha maps into reusable local-space shadow contours.
/// </summary>
internal sealed class ScpShadowContourCache
{
    #region Dependencies and cache

    private readonly IClickMapManager _clickMaps;
    private readonly Dictionary<Texture, CacheEntry> _textureCache = new();
    private readonly Dictionary<RsiFrameKey, CacheEntry> _rsiCache = new();

    #endregion

    public ScpShadowContourCache(IClickMapManager clickMaps)
    {
        _clickMaps = clickMaps;
    }

    #region Public API

    public bool TryGetContours(Texture texture, out ScpShadowContours contours)
    {
        if (_textureCache.TryGetValue(texture, out var entry) &&
            TryGetCachedContours(entry, out contours))
        {
            return contours.Loops.Length != 0;
        }

        if (!_clickMaps.TryGetRegion(texture, out var region))
        {
            contours = ScpShadowContours.Empty;
            return false;
        }

        if (entry == null)
        {
            entry = new CacheEntry();
            _textureCache.Add(texture, entry);
        }

        contours = GetOrBuild(entry, region);
        return contours.Loops.Length != 0;
    }

    public bool TryGetContours(
        RSI rsi,
        RSI.StateId state,
        RsiDirection direction,
        int frame,
        out ScpShadowContours contours)
    {
        var key = new RsiFrameKey(rsi, state, direction, frame);
        if (_rsiCache.TryGetValue(key, out var entry) &&
            TryGetCachedContours(entry, out contours))
        {
            return contours.Loops.Length != 0;
        }

        if (!_clickMaps.TryGetRegion(rsi, state, direction, frame, out var region))
        {
            contours = ScpShadowContours.Empty;
            return false;
        }

        if (entry == null)
        {
            entry = new CacheEntry();
            _rsiCache.Add(key, entry);
        }

        contours = GetOrBuild(entry, region);
        return contours.Loops.Length != 0;
    }

    public bool TryGetOpaqueBounds(Texture texture, out Box2 bounds)
    {
        if (_textureCache.TryGetValue(texture, out var entry) && entry.OpaqueBoundsCached)
            return TryGetCachedOpaqueBounds(entry, out bounds);

        if (!_clickMaps.TryGetRegion(texture, out var region))
        {
            bounds = default;
            return false;
        }

        if (entry == null)
        {
            entry = new CacheEntry();
            _textureCache.Add(texture, entry);
        }

        return BuildAndCacheOpaqueBounds(entry, region, out bounds);
    }

    public bool TryGetOpaqueBounds(
        RSI rsi,
        RSI.StateId state,
        RsiDirection direction,
        int frame,
        out Box2 bounds)
    {
        var key = new RsiFrameKey(rsi, state, direction, frame);
        if (_rsiCache.TryGetValue(key, out var entry) && entry.OpaqueBoundsCached)
            return TryGetCachedOpaqueBounds(entry, out bounds);

        if (!_clickMaps.TryGetRegion(rsi, state, direction, frame, out var region))
        {
            bounds = default;
            return false;
        }

        if (entry == null)
        {
            entry = new CacheEntry();
            _rsiCache.Add(key, entry);
        }

        return BuildAndCacheOpaqueBounds(entry, region, out bounds);
    }

    #endregion

    #region Contour building

    private static ScpShadowContours GetOrBuild(CacheEntry entry, ClickMapRegion region)
    {
        return entry.Sprite ??= BuildPixelContours(region);
    }

    private static bool TryGetCachedContours(CacheEntry entry, out ScpShadowContours contours)
    {
        var cached = entry.Sprite;
        if (cached == null)
        {
            contours = ScpShadowContours.Empty;
            return false;
        }

        contours = cached;
        return true;
    }

    private static bool TryGetCachedOpaqueBounds(CacheEntry entry, out Box2 bounds)
    {
        if (entry.OpaqueBounds is not { } cached)
        {
            bounds = default;
            return false;
        }

        bounds = cached;
        return true;
    }

    private static bool BuildAndCacheOpaqueBounds(
        CacheEntry entry,
        ClickMapRegion region,
        out Box2 bounds)
    {
        var minimum = new Vector2(float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity);
        var width = region.Size.X;
        var height = region.Size.Y;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!region.IsOccluded(x, y))
                    continue;

                var bottom = height - y - 1;
                minimum = Vector2.Min(minimum, new Vector2(x, bottom));
                maximum = Vector2.Max(maximum, new Vector2(x + 1, bottom + 1));
            }
        }

        entry.OpaqueBoundsCached = true;
        if (!float.IsFinite(minimum.X))
        {
            entry.OpaqueBounds = null;
            bounds = default;
            return false;
        }

        var center = new Vector2(width, height) * 0.5f;
        bounds = new Box2(
            (minimum - center) / EyeManager.PixelsPerMeter,
            (maximum - center) / EyeManager.PixelsPerMeter);
        entry.OpaqueBounds = bounds;
        return true;
    }

    private static ScpShadowContours BuildPixelContours(ClickMapRegion region)
    {
        var edges = new List<GridEdge>();
        var outgoing = new Dictionary<Vector2i, List<int>>();
        var width = region.Size.X;
        var height = region.Size.Y;

        void AddEdge(Vector2i start, Vector2i end)
        {
            var index = edges.Count;
            edges.Add(new GridEdge(start, end));

            if (!outgoing.TryGetValue(start, out var indices))
            {
                indices = new List<int>(1);
                outgoing.Add(start, indices);
            }

            indices.Add(index);
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!region.IsOccluded(x, y))
                    continue;

                var bottom = height - y - 1;
                var bottomLeft = new Vector2i(x, bottom);
                var bottomRight = new Vector2i(x + 1, bottom);
                var topRight = new Vector2i(x + 1, bottom + 1);
                var topLeft = new Vector2i(x, bottom + 1);

                if (!region.IsOccluded(x, y + 1))
                    AddEdge(bottomLeft, bottomRight);
                if (!region.IsOccluded(x + 1, y))
                    AddEdge(bottomRight, topRight);
                if (!region.IsOccluded(x, y - 1))
                    AddEdge(topRight, topLeft);
                if (!region.IsOccluded(x - 1, y))
                    AddEdge(topLeft, bottomLeft);
            }
        }

        if (edges.Count == 0)
            return ScpShadowContours.Empty;

        var used = new bool[edges.Count];
        var loops = new List<Vector2[]>();
        var gridLoop = new List<Vector2i>();

        for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
        {
            if (used[edgeIndex])
                continue;

            gridLoop.Clear();
            var edge = edges[edgeIndex];
            var start = edge.Start;
            var current = edge.End;
            var previousDirection = edge.End - edge.Start;
            used[edgeIndex] = true;
            gridLoop.Add(start);

            var closed = false;
            for (var guard = 0; guard <= edges.Count; guard++)
            {
                if (current == start)
                {
                    closed = true;
                    break;
                }

                gridLoop.Add(current);
                if (!outgoing.TryGetValue(current, out var candidates))
                    break;

                var nextIndex = SelectNextEdge(candidates, edges, used, previousDirection);
                if (nextIndex < 0)
                    break;

                var next = edges[nextIndex];
                used[nextIndex] = true;
                previousDirection = next.End - next.Start;
                current = next.End;
            }

            if (!closed || gridLoop.Count < 3 || SignedArea(gridLoop) <= 0)
                continue;

            var simplified = RemoveCollinear(gridLoop);
            if (simplified.Count >= 3)
                loops.Add(ConvertLoop(simplified, width, height));
        }

        return loops.Count == 0 ? ScpShadowContours.Empty : new ScpShadowContours(loops.ToArray());
    }

    private static int SelectNextEdge(
        List<int> candidates,
        List<GridEdge> edges,
        bool[] used,
        Vector2i previousDirection)
    {
        var selected = -1;
        var selectedScore = int.MinValue;

        for (var i = 0; i < candidates.Count; i++)
        {
            var index = candidates[i];
            if (used[index])
                continue;

            var direction = edges[index].End - edges[index].Start;
            var cross = Cross(previousDirection, direction);
            var dot = Dot(previousDirection, direction);
            var score = cross > 0 ? 3 : dot > 0 ? 2 : cross < 0 ? 1 : 0;

            if (score <= selectedScore)
                continue;

            selected = index;
            selectedScore = score;
        }

        return selected;
    }

    private static List<Vector2i> RemoveCollinear(List<Vector2i> loop)
    {
        var result = new List<Vector2i>(loop.Count);

        for (var i = 0; i < loop.Count; i++)
        {
            var previous = loop[(i + loop.Count - 1) % loop.Count];
            var current = loop[i];
            var next = loop[(i + 1) % loop.Count];
            var incoming = current - previous;
            var outgoing = next - current;

            if (Cross(incoming, outgoing) == 0 && Dot(incoming, outgoing) > 0)
                continue;

            result.Add(current);
        }

        return result;
    }

    private static Vector2[] ConvertLoop(List<Vector2i> loop, int width, int height)
    {
        var result = new Vector2[loop.Count];
        var center = new Vector2(width, height) * 0.5f;

        for (var i = 0; i < loop.Count; i++)
            result[i] = ((Vector2) loop[i] - center) / EyeManager.PixelsPerMeter;

        return result;
    }

    private static long SignedArea(List<Vector2i> loop)
    {
        long area = 0;

        for (var i = 0; i < loop.Count; i++)
        {
            var next = loop[(i + 1) % loop.Count];
            area += (long) loop[i].X * next.Y - (long) next.X * loop[i].Y;
        }

        return area;
    }

    private static int Cross(Vector2i left, Vector2i right)
    {
        return left.X * right.Y - left.Y * right.X;
    }

    private static int Dot(Vector2i left, Vector2i right)
    {
        return left.X * right.X + left.Y * right.Y;
    }

    #endregion

    #region Cached types

    private sealed class CacheEntry
    {
        public ScpShadowContours? Sprite;
        public bool OpaqueBoundsCached;
        public Box2? OpaqueBounds;
    }

    private readonly record struct RsiFrameKey(
        RSI Rsi,
        RSI.StateId State,
        RsiDirection Direction,
        int Frame);

    private readonly record struct GridEdge(Vector2i Start, Vector2i End);

    #endregion
}

internal sealed class ScpShadowContours(Vector2[][] loops)
{
    public static readonly ScpShadowContours Empty = new([]);

    public Vector2[][] Loops { get; } = loops;
}
