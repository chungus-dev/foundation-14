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
        {
            _lightGeometryBuffers.Add(new LightGeometryBuffer());
        }

        var validLightCount = 0;
        long intersectionChecks = 0;
        for (var i = 0; i < lightCount; i++)
        {
            var geometry = _lightGeometryBuffers[i];
            geometry.Clear();
            var light = _system.ViewportLights[lightStart + i];
            if (!drawShadows ||
                !light.CastShadows ||
                light.Radius <= 0f ||
                light.Energy <= 0f)
            {
                continue;
            }

            geometry.CasterCandidates = GetCasterCandidateRange(light);
            geometry.OccluderCandidates = GetOccluderCandidateRange(light);
            intersectionChecks += geometry.CasterCandidates.Count + geometry.OccluderCandidates.Count;
            validLightCount++;
        }

        if (validLightCount == 0)
            return;

        _lightGeometryJob.LightStart = lightStart;
        _lightGeometryJob.BuildOutsideMask = _directionalFovActive;

        _system.ProcessGeometryBatch(_lightGeometryJob, lightCount, validLightCount, intersectionChecks);
    }

    private void BuildLightGeometry(int lightIndex, int bufferIndex, bool buildOutsideMask)
    {
        var geometry = _lightGeometryBuffers[bufferIndex];

        var light = _system.ViewportLights[lightIndex];
        if (!light.CastShadows || light.Radius <= 0f || light.Energy <= 0f)
            return;

        BuildCasterMasks(light, buildOutsideMask, geometry, geometry.CasterCandidates);
        BuildOccluderMask(light, geometry, geometry.OccluderCandidates);
    }

    private sealed class LightGeometryBuffer
    {
        public readonly List<DrawVertexUV2DColor> Vertices = new(512);
        public bool HasInsideMask;
        public bool HasOutsideMask;
        public bool HasOccluderMask;
        public ScpAxisCandidateRange CasterCandidates;
        public ScpAxisCandidateRange OccluderCandidates;

        public bool HasCasterMask => HasInsideMask || HasOutsideMask;
        public bool HasMask => HasCasterMask || HasOccluderMask;

        public void Clear()
        {
            Vertices.Clear();
            HasInsideMask = false;
            HasOutsideMask = false;
            HasOccluderMask = false;
            CasterCandidates = default;
            OccluderCandidates = default;
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
