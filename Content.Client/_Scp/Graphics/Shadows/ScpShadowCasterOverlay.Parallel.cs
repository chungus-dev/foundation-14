using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.Graphics;
using Robust.Shared.Profiling;
using Robust.Shared.Threading;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    #region Parallel light geometry

    private readonly List<LightGeometryBuffer> _lightGeometryBuffers = new(16);
    private readonly LightGeometryBuffer _emptyLightGeometryBuffer = new();
    private readonly LightGeometryJob _lightGeometryJob;
    private readonly AtlasGeometryJob _atlasGeometryJob;
    private readonly List<AtlasGeometryTask> _dirtyAtlasGeometryTasks = new(128);
    private int[] _dirtyGeometryIndices = new int[16];

    private void BindGeometryBuffers(CachedResources resources, int lightCount)
    {
        resources.BindGeometryFrame(
            _currentMapId,
            _lights,
            lightCount,
            _emptyLightGeometryBuffer,
            _lightGeometryBuffers);
    }

    private void PrepareGeometryBatch(
        int lightStart,
        int lightCount,
        bool drawShadows)
    {
        EnsureDirtyGeometryCapacity(lightCount);
        var dirtyCount = 0;
        var intersectionChecks = 0L;
        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.CacheValidation")
                   : (ProfManager.GroupGuard?) null)
        {
            using (_prof.IsEnabled || _prof.IsTracyEnabled
                       ? _prof.Group("ScpContentLighting.SourceRevisionValidation")
                       : (ProfManager.GroupGuard?) null)
            {
                UpdateGeometrySnapshots();
            }

            using (_prof.IsEnabled || _prof.IsTracyEnabled
                       ? _prof.Group("ScpContentLighting.DependencyValidation")
                       : (ProfManager.GroupGuard?) null)
            {
                for (var i = 0; i < lightCount; i++)
                {
                    var geometry = _lightGeometryBuffers[i];
                    geometry.BeginValidation();
                    var light = _lights[lightStart + i];
                    if (!drawShadows ||
                        !light.CastShadows ||
                        light.Radius <= 0f ||
                        light.Energy <= 0f)
                    {
                        continue;
                    }

                    var lightIntersectionChecks = 0L;

                    var casterKey = new ScpCasterGeometryCacheKey(
                        light.Owner,
                        light.Position,
                        light.ProjectionPosition,
                        light.Radius,
                        _directionalFovActive);
                    var casterKeyChanged = !geometry.CasterCache.IsCurrent(casterKey);
                    var casterEpochChanged = geometry.CasterSourceEpoch != _casterSnapshotEpoch;
                    var hasOnlyLatestCasterChanges = ScpGeometrySourceEpoch.HasOnlyLatestChanges(
                        geometry.CasterSourceEpoch,
                        _casterSnapshotEpoch);
                    var validateCasterDependencies = casterKeyChanged ||
                        casterEpochChanged &&
                        (!hasOnlyLatestCasterChanges || CasterChangesMayAffectLight(light));
                    if (validateCasterDependencies)
                    {
                        geometry.CasterCandidates = GatherCasterDependencies(
                            light,
                            _directionalFovActive,
                            geometry.PendingCasterDependencies,
                            out _);
                        var dependencies = CollectionsMarshal.AsSpan(geometry.PendingCasterDependencies);
                        var dependenciesCurrent = geometry.CasterDependencies.IsCurrent(
                            dependencies,
                            out geometry.PendingCasterDependencyHash);
                        if (casterKeyChanged || !dependenciesCurrent)
                        {
                            geometry.PendingCasterKey = casterKey;
                            geometry.PendingCasterSourceEpoch = _casterSnapshotEpoch;
                            geometry.RebuildCaster = true;
                            lightIntersectionChecks += geometry.CasterCandidates.Count;
                        }
                        else
                        {
                            geometry.CasterSourceEpoch = _casterSnapshotEpoch;
                        }
                    }
                    else if (casterEpochChanged)
                    {
                        geometry.CasterSourceEpoch = _casterSnapshotEpoch;
                    }

                    var occluderKey = new ScpOccluderGeometryCacheKey(
                        light.Owner,
                        light.Position,
                        light.Radius);
                    var occluderKeyChanged = !geometry.OccluderCache.IsCurrent(occluderKey);
                    var occluderEpochChanged = geometry.OccluderSourceEpoch != _occluderSnapshotEpoch;
                    var hasOnlyLatestOccluderChanges = ScpGeometrySourceEpoch.HasOnlyLatestChanges(
                        geometry.OccluderSourceEpoch,
                        _occluderSnapshotEpoch);
                    var validateOccluderDependencies = occluderKeyChanged ||
                        occluderEpochChanged &&
                        (!hasOnlyLatestOccluderChanges || OccluderChangesMayAffectLight(light));
                    if (validateOccluderDependencies)
                    {
                        geometry.OccluderCandidates = GatherOccluderDependencies(
                            light,
                            geometry.PendingOccluderDependencies,
                            out _);
                        var dependencies = CollectionsMarshal.AsSpan(geometry.PendingOccluderDependencies);
                        var dependenciesCurrent = geometry.OccluderDependencies.IsCurrent(
                            dependencies,
                            out geometry.PendingOccluderDependencyHash);
                        if (occluderKeyChanged || !dependenciesCurrent)
                        {
                            geometry.PendingOccluderKey = occluderKey;
                            geometry.PendingOccluderSourceEpoch = _occluderSnapshotEpoch;
                            geometry.RebuildOccluder = true;
                            lightIntersectionChecks += geometry.OccluderCandidates.Count;
                        }
                        else
                        {
                            geometry.OccluderSourceEpoch = _occluderSnapshotEpoch;
                        }
                    }
                    else if (occluderEpochChanged)
                    {
                        geometry.OccluderSourceEpoch = _occluderSnapshotEpoch;
                    }

                    if (geometry.RebuildCaster || geometry.RebuildOccluder)
                    {
                        _dirtyGeometryIndices[dirtyCount++] = i;
                        intersectionChecks += lightIntersectionChecks;
                    }
                }
            }
        }

        ProcessSelectedGeometry(lightStart, dirtyCount, intersectionChecks);
    }

    private void ProcessSelectedGeometry(
        int lightStart,
        int selectedCount,
        long intersectionChecks)
    {
        if (selectedCount == 0)
            return;

        _lightGeometryJob.LightStart = lightStart;
        _lightGeometryJob.BuildOutsideMask = _directionalFovActive;
        _lightGeometryJob.DirtyGeometryIndices = _dirtyGeometryIndices;

        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.GeometryGather")
                   : (ProfManager.GroupGuard?) null)
        {
            _system.ProcessGeometryBatch(_lightGeometryJob, selectedCount, intersectionChecks);
        }

        for (var index = 0; index < selectedCount; index++)
        {
            var geometryIndex = _dirtyGeometryIndices[index];
            _lightGeometryBuffers[geometryIndex].CommitRebuiltParts();
        }
    }

    private void EnsureDirtyGeometryCapacity(int capacity)
    {
        if (_dirtyGeometryIndices.Length >= capacity)
            return;

        Array.Resize(ref _dirtyGeometryIndices, Math.Max(capacity, _dirtyGeometryIndices.Length * 2));
    }

    private void BuildLightGeometry(int lightIndex, int bufferIndex, bool buildOutsideMask)
    {
        var geometry = _lightGeometryBuffers[bufferIndex];

        var light = _lights[lightIndex];
        if (!light.CastShadows || light.Radius <= 0f || light.Energy <= 0f)
            return;

        if (geometry.RebuildCaster)
        {
            geometry.CasterVertices.Clear();
            geometry.HasInsideMask = false;
            geometry.HasOutsideMask = false;
            BuildCasterMasks(light, buildOutsideMask, geometry, geometry.CasterCandidates);
        }

        if (geometry.RebuildOccluder)
        {
            geometry.OccluderVertices.Clear();
            geometry.HasOccluderMask = false;
            BuildOccluderMask(light, geometry, geometry.OccluderCandidates);
        }
    }

    private void ProcessAtlasGeometryBatch(long estimatedWork)
    {
        if (_dirtyAtlasGeometryTasks.Count == 0)
            return;

        _atlasGeometryJob.Tasks = _dirtyAtlasGeometryTasks;
        _system.ProcessGeometryBatch(
            _atlasGeometryJob,
            _dirtyAtlasGeometryTasks.Count,
            estimatedWork);
    }

    private sealed class LightGeometryBuffer
    {
        public readonly uint Incarnation;
        public readonly List<DrawVertexUV2DColor> CasterVertices = new(256);
        public readonly List<DrawVertexUV2DColor> OccluderVertices = new(256);
        public readonly List<DrawVertexUV2DColor> AtlasCasterVertices = new(256);
        public readonly List<DrawVertexUV2DColor> AtlasOccluderVertices = new(256);
        public readonly Vector2[] AtlasClipPolygonA = new Vector2[8];
        public readonly Vector2[] AtlasClipPolygonB = new Vector2[8];
        public readonly ScpOrderedGeometryDependencyCache CasterDependencies = new();
        public readonly ScpOrderedGeometryDependencyCache OccluderDependencies = new();
        public readonly List<ScpGeometryDependency> PendingCasterDependencies = new(32);
        public readonly List<ScpGeometryDependency> PendingOccluderDependencies = new(32);
        public ScpGeometryCacheState<ScpCasterGeometryCacheKey> CasterCache;
        public ScpGeometryCacheState<ScpOccluderGeometryCacheKey> OccluderCache;
        public ScpGeometryCacheState<ScpAtlasGeometryCacheKey> AtlasCasterCache;
        public ScpGeometryCacheState<ScpAtlasGeometryCacheKey> AtlasOccluderCache;
        public ScpCasterGeometryCacheKey PendingCasterKey;
        public ScpOccluderGeometryCacheKey PendingOccluderKey;
        public ulong PendingCasterDependencyHash;
        public ulong PendingOccluderDependencyHash;
        public uint CasterSourceEpoch;
        public uint OccluderSourceEpoch;
        public uint PendingCasterSourceEpoch;
        public uint PendingOccluderSourceEpoch;
        public Vector2 AtlasCasterOffset;
        public Vector2 AtlasOccluderOffset;
        public bool HasInsideMask;
        public bool HasOutsideMask;
        public bool HasOccluderMask;
        public bool AtlasHasCasterMask;
        public bool AtlasHasOccluderMask;
        public bool RebuildCaster;
        public bool RebuildOccluder;
        public ulong AtlasContentGeneration { get; private set; }
        public ScpAxisCandidateRange CasterCandidates;
        public ScpAxisCandidateRange OccluderCandidates;

        public bool HasCasterMask => HasInsideMask || HasOutsideMask;
        public bool HasMask => HasCasterMask || HasOccluderMask;

        public LightGeometryBuffer(uint incarnation = 0)
        {
            Incarnation = incarnation;
        }

        public void MarkAtlasContentChanged()
        {
            AtlasContentGeneration = unchecked(AtlasContentGeneration + 1);
            if (AtlasContentGeneration == 0)
                AtlasContentGeneration = 1;
        }

        public long EstimatedBytes =>
            640L +
            (long) CasterVertices.Capacity * 40L +
            (long) OccluderVertices.Capacity * 40L +
            (long) AtlasCasterVertices.Capacity * 40L +
            (long) AtlasOccluderVertices.Capacity * 40L +
            (long) PendingCasterDependencies.Capacity * 16L +
            (long) PendingOccluderDependencies.Capacity * 16L +
            CasterDependencies.EstimatedBytes +
            OccluderDependencies.EstimatedBytes;

        public void BeginValidation()
        {
            RebuildCaster = false;
            RebuildOccluder = false;
        }

        public void CommitRebuiltParts()
        {
            if (RebuildCaster)
            {
                CasterDependencies.Commit(
                    CollectionsMarshal.AsSpan(PendingCasterDependencies),
                    PendingCasterDependencyHash);
                CasterCache.Commit(PendingCasterKey);
                CasterSourceEpoch = PendingCasterSourceEpoch;
            }
            if (RebuildOccluder)
            {
                OccluderDependencies.Commit(
                    CollectionsMarshal.AsSpan(PendingOccluderDependencies),
                    PendingOccluderDependencyHash);
                OccluderCache.Commit(PendingOccluderKey);
                OccluderSourceEpoch = PendingOccluderSourceEpoch;
            }
        }
    }

    private readonly record struct AtlasGeometryTask(
        LightGeometryBuffer Geometry,
        Matrix3x2 SourceRelativeTargetMatrix,
        Vector2i SourceSize,
        Vector2 AtlasOffset,
        ScpAtlasGeometryCacheKey CasterKey,
        ScpAtlasGeometryCacheKey OccluderKey,
        bool RebuildCaster,
        bool RebuildOccluder);

    private sealed class LightGeometryJob(ScpShadowCasterOverlay overlay) : IParallelRobustJob
    {
        public int MinimumBatchParallel => 1;
        public int BatchSize => 2;

        public int LightStart;
        public bool BuildOutsideMask;
        public int[] DirtyGeometryIndices = [];

        public void Execute(int index)
        {
            var bufferIndex = DirtyGeometryIndices[index];
            overlay.BuildLightGeometry(LightStart + bufferIndex, bufferIndex, BuildOutsideMask);
        }
    }

    private sealed class AtlasGeometryJob(ScpShadowCasterOverlay overlay) : IParallelRobustJob
    {
        public int MinimumBatchParallel => 1;
        public int BatchSize => 2;

        public List<AtlasGeometryTask> Tasks = [];

        public void Execute(int index)
        {
            overlay.RebuildAtlasGeometry(Tasks[index]);
        }
    }

    #endregion
}
