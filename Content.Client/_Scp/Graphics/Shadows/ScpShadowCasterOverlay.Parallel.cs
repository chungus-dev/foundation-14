using Robust.Client.Graphics;
using Robust.Shared.Threading;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    #region Parallel light geometry

    private readonly List<LightGeometryBuffer> _lightGeometryBuffers = new(16);
    private readonly LightGeometryJob _lightGeometryJob;

    private void PrepareGeometryBatch(int lightStart, int lightCount, bool drawShadows)
    {
        while (_lightGeometryBuffers.Count < lightCount)
            _lightGeometryBuffers.Add(new LightGeometryBuffer());

        var validLightCount = 0;
        for (var i = 0; i < lightCount; i++)
        {
            var light = _lights[lightStart + i];
            if (!drawShadows ||
                !light.CastShadows ||
                light.Radius <= 0f ||
                light.Energy <= 0f)
            {
                _lightGeometryBuffers[i].Clear();
                continue;
            }

            validLightCount++;
        }

        if (validLightCount == 0)
            return;

        _lightGeometryJob.LightStart = lightStart;
        _lightGeometryJob.BuildOutsideMask = _directionalFovActive;

        var intersectionChecks = (long) validLightCount *
            (_frameCasters.Count + _frameOccluders.Count);
        _system.ProcessGeometryBatch(_lightGeometryJob, lightCount, intersectionChecks);
    }

    private void BuildLightGeometry(int lightIndex, int bufferIndex, bool buildOutsideMask)
    {
        var geometry = _lightGeometryBuffers[bufferIndex];
        geometry.Clear();

        var light = _lights[lightIndex];
        if (!light.CastShadows || light.Radius <= 0f || light.Energy <= 0f)
            return;

        BuildCasterMasks(light, buildOutsideMask, geometry);
        BuildOccluderMask(light, geometry);
    }

    private sealed class LightGeometryBuffer
    {
        public readonly List<DrawVertexUV2DColor> Vertices = new(512);
        public bool HasInsideMask;
        public bool HasOutsideMask;
        public bool HasOccluderMask;

        public bool HasCasterMask => HasInsideMask || HasOutsideMask;
        public bool HasMask => HasCasterMask || HasOccluderMask;

        public void Clear()
        {
            Vertices.Clear();
            HasInsideMask = false;
            HasOutsideMask = false;
            HasOccluderMask = false;
        }
    }

    private sealed class LightGeometryJob(ScpShadowCasterOverlay overlay) : IParallelRobustJob
    {
        public int MinimumBatchParallel => 1;
        public int BatchSize => 2;

        public int LightStart;
        public bool BuildOutsideMask;

        public void Execute(int index)
        {
            overlay.BuildLightGeometry(LightStart + index, index, BuildOutsideMask);
        }
    }

    #endregion
}
