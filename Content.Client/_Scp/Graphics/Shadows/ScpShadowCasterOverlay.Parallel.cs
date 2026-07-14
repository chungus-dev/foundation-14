using System.Numerics;
using Robust.Client.Graphics;
using Robust.Shared.Maths;
using Robust.Shared.Threading;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    #region Parallel light geometry

    private readonly List<LightGeometryBuffer> _lightGeometryBuffers = new(16);
    private readonly LightGeometryJob _lightGeometryJob;

    private void PrepareGeometryBatch(
        int lightStart,
        int lightCount,
        bool buildOutsideMask)
    {
        while (_lightGeometryBuffers.Count < lightCount)
            _lightGeometryBuffers.Add(new LightGeometryBuffer());

        _lightGeometryJob.LightStart = lightStart;
        _lightGeometryJob.BuildOutsideMask = buildOutsideMask;

        var intersectionChecks = (long) lightCount *
            (_frameCasters.Count + _frameOccluders.Count);
        _system.ProcessGeometryBatch(_lightGeometryJob, lightCount, intersectionChecks);
    }

    private void BuildLightGeometry(int lightIndex, int bufferIndex, bool buildOutsideMask)
    {
        var geometry = _lightGeometryBuffers[bufferIndex];
        geometry.Clear();

        var light = _lights[lightIndex];
        BuildCasterMasks(light, buildOutsideMask, geometry);
        if (geometry.HasInsideMask || geometry.HasOutsideMask)
            BuildOccluderMask(light, geometry);
    }

    private sealed class LightGeometryBuffer
    {
        public readonly List<DrawVertexUV2DColor> Vertices = new(512);
        public bool HasInsideMask;
        public bool HasOutsideMask;
        public bool HasOccluderMask;
        public Box2 InsideBounds;
        public Box2 OutsideBounds;

        public void Clear()
        {
            Vertices.Clear();
            HasInsideMask = false;
            HasOutsideMask = false;
            HasOccluderMask = false;
            InsideBounds = default;
            OutsideBounds = default;
        }

        public Box2 CombinedCasterBounds()
        {
            if (!HasInsideMask)
                return OutsideBounds;
            if (!HasOutsideMask)
                return InsideBounds;
            return InsideBounds.Union(OutsideBounds);
        }

        public void ExtendCasterBounds(int vertexStart, bool inside, bool outside)
        {
            var minimum = new Vector2(float.PositiveInfinity);
            var maximum = new Vector2(float.NegativeInfinity);
            for (var i = vertexStart; i < Vertices.Count; i++)
            {
                var position = Vertices[i].Position;
                minimum = Vector2.Min(minimum, position);
                maximum = Vector2.Max(maximum, position);
            }

            var bounds = new Box2(minimum, maximum);
            if (inside)
                InsideBounds = HasInsideMask ? InsideBounds.Union(bounds) : bounds;
            if (outside)
                OutsideBounds = HasOutsideMask ? OutsideBounds.Union(bounds) : bounds;
        }
    }

    private sealed class LightGeometryJob(ScpShadowCasterOverlay overlay) : IParallelRobustJob
    {
        public int MinimumBatchParallel => 1;

        public int LightStart;
        public bool BuildOutsideMask;

        public void Execute(int index)
        {
            overlay.BuildLightGeometry(LightStart + index, index, BuildOutsideMask);
        }
    }

    #endregion
}
