using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.Graphics;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const int MaxPrimitiveVerticesPerDraw = ScpLightingBatchPlanner.MaxVerticesPerDraw;

    #region Render callbacks

    private void DrawShadowMask()
    {
        var handle = _drawHandle!;
        _renderHandle!.SetScissor(_wideMaskDrawBounds);
        try
        {
            handle.SetTransform(Matrix3x2.Identity);

            PrepareWideMaskClearVertices(_wideMaskDrawBounds);
            handle.UseShader(_atlasClearShader);
            DrawTriangleList(handle, _whiteTexture, _wideMaskClearVertices);

            handle.UseShader(_maskShader);
            DrawTriangleList(handle, _whiteTexture, CollectionsMarshal.AsSpan(_atlasMaskVertices));
        }
        finally
        {
            handle.UseShader(null);
            _renderHandle.SetScissor(null);
        }
    }

    private void PrepareWideMaskClearVertices(UIBox2i bounds)
    {
        var bottomLeft = new Vector2(bounds.Left, bounds.Bottom);
        var bottomRight = new Vector2(bounds.Right, bounds.Bottom);
        var topRight = new Vector2(bounds.Right, bounds.Top);
        var topLeft = new Vector2(bounds.Left, bounds.Top);
        _wideMaskClearVertices[0] = new DrawVertexUV2DColor(bottomLeft, Color.Black);
        _wideMaskClearVertices[1] = new DrawVertexUV2DColor(bottomRight, Color.Black);
        _wideMaskClearVertices[2] = new DrawVertexUV2DColor(topRight, Color.Black);
        _wideMaskClearVertices[3] = new DrawVertexUV2DColor(bottomLeft, Color.Black);
        _wideMaskClearVertices[4] = new DrawVertexUV2DColor(topRight, Color.Black);
        _wideMaskClearVertices[5] = new DrawVertexUV2DColor(topLeft, Color.Black);
    }

    private static void DrawTriangleList(
        DrawingHandleWorld handle,
        Texture texture,
        ReadOnlySpan<DrawVertexUV2DColor> vertices)
    {
        while (!vertices.IsEmpty)
        {
            var count = Math.Min(vertices.Length, MaxPrimitiveVerticesPerDraw);
            handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, texture, vertices[..count]);
            vertices = vertices[count..];
        }
    }

    private void DrawProtectionMask()
    {
        var handle = _drawHandle!;
        PrepareProtectionBatches();
        handle.UseShader(_protectionShader);
        handle.SetTransform(_targetMatrix);

        for (var batchIndex = 0; batchIndex < _activeProtectionBatches; batchIndex++)
        {
            var batch = _protectionBatches[batchIndex];
            DrawTriangleList(
                handle,
                batch.Source,
                CollectionsMarshal.AsSpan(batch.Vertices));
        }

        handle.UseShader(null);
    }

    #endregion

    #region Per-viewport resources

    private sealed class GeometrySnapshotState
    {
        private static readonly Comparison<GeometrySnapshotBudgetCandidate> BudgetCandidateComparison =
            static (left, right) =>
            {
                if (left.ForceEvict != right.ForceEvict)
                    return left.ForceEvict ? -1 : 1;

                var comparison = left.LastVisibleFrame.CompareTo(right.LastVisibleFrame);
                if (comparison != 0)
                    return comparison;

                comparison = left.Kind.CompareTo(right.Kind);
                if (comparison != 0)
                    return comparison;

                comparison = left.Key.Owner.CompareTo(right.Key.Owner);
                return comparison != 0
                    ? comparison
                    : left.Key.NetIdentity.Id.CompareTo(right.Key.NetIdentity.Id);
            };

        public readonly ScpExactSnapshot<CasterFrameSourceStamp, CasterFrameSourceStampComparer>
            CasterSnapshotSources = new();
        public readonly ScpExactSnapshot<CachedOccluder, CachedOccluderSnapshotComparer>
            OccluderSnapshotOccluders = new();
        public readonly ScpExactSnapshot<Vector2, Vector2SnapshotComparer> OccluderSnapshotVertices = new();
        public readonly Dictionary<ScpGeometryEntityKey, CasterEntitySnapshot> CasterEntitySnapshots = new(256);
        public readonly Dictionary<ScpGeometryEntityKey, OccluderEntitySnapshot> OccluderEntitySnapshots = new(256);
        public readonly List<ScpGeometryDependency> FrameCasterDependencies = new(256);
        public readonly List<ScpGeometryDependency> FrameOccluderDependencies = new(256);
        public readonly List<ScpGeometrySourceChange> CasterSourceChanges = new(32);
        public readonly List<ScpGeometrySourceChange> OccluderSourceChanges = new(32);
        private readonly List<GeometrySnapshotBudgetCandidate> _budgetCandidates =
            new(ScpGeometrySnapshotRetention.MaximumInactiveRecords * 2);

        public uint CasterEpoch;
        public uint OccluderEpoch;
        public ulong FrameStamp { get; private set; }
        public bool ValidateAllCasterDependencies;
        public bool ValidateAllOccluderDependencies;
        public long CasterRetainedBytes { get; private set; }
        public long OccluderRetainedBytes { get; private set; }
        private uint _nextGeometryGeneration;

        public long RetainedEntityBytes => CasterRetainedBytes + OccluderRetainedBytes;

        public long NonEvictableEstimatedBytes =>
            512L +
            CasterSnapshotSources.EstimatedBytes +
            OccluderSnapshotOccluders.EstimatedBytes +
            OccluderSnapshotVertices.EstimatedBytes +
            56L + (long) FrameCasterDependencies.Capacity * 16L +
            56L + (long) FrameOccluderDependencies.Capacity * 16L +
            56L + (long) CasterSourceChanges.Capacity * 44L +
            56L + (long) OccluderSourceChanges.Capacity * 44L +
            56L + (long) _budgetCandidates.Capacity * 32L +
            (long) (CasterEntitySnapshots.EnsureCapacity(0) +
                    OccluderEntitySnapshots.EnsureCapacity(0)) * 64L;

        public long EstimatedBytes => NonEvictableEstimatedBytes + RetainedEntityBytes;

        public void BeginFrame()
        {
            FrameStamp = unchecked(FrameStamp + 1);
            if (FrameStamp == 0)
                FrameStamp = 1;
        }

        public void AddCasterSnapshot(ScpGeometryEntityKey key, CasterEntitySnapshot state)
        {
            CasterEntitySnapshots.Add(key, state);
            CasterRetainedBytes += state.EstimatedBytes;
        }

        public void AddOccluderSnapshot(ScpGeometryEntityKey key, OccluderEntitySnapshot state)
        {
            OccluderEntitySnapshots.Add(key, state);
            OccluderRetainedBytes += state.EstimatedBytes;
        }

        public void AccountCasterResize(CasterEntitySnapshot state, long previousBytes)
        {
            CasterRetainedBytes += state.EstimatedBytes - previousBytes;
        }

        public void AccountOccluderResize(OccluderEntitySnapshot state, long previousBytes)
        {
            OccluderRetainedBytes += state.EstimatedBytes - previousBytes;
        }

        public bool RemoveCasterSnapshot(ScpGeometryEntityKey key)
        {
            if (!CasterEntitySnapshots.Remove(key, out var state))
                return false;

            CasterRetainedBytes -= state.EstimatedBytes;
            return true;
        }

        public bool RemoveOccluderSnapshot(ScpGeometryEntityKey key)
        {
            if (!OccluderEntitySnapshots.Remove(key, out var state))
                return false;

            OccluderRetainedBytes -= state.EstimatedBytes;
            return true;
        }

        public void ClearCasterSnapshots()
        {
            CasterEntitySnapshots.Clear();
            CasterRetainedBytes = 0;
        }

        public void ClearOccluderSnapshots()
        {
            OccluderEntitySnapshots.Clear();
            OccluderRetainedBytes = 0;
        }

        public void RemoveGeometrySource(ScpGeometryEntityKey key)
        {
            if (CasterEntitySnapshots.TryGetValue(key, out var caster))
            {
                if (caster.Residency.Active)
                    caster.DeletePending = true;
                else
                    RemoveCasterSnapshot(key);
            }

            if (OccluderEntitySnapshots.TryGetValue(key, out var occluder))
            {
                if (occluder.Residency.Active)
                    occluder.DeletePending = true;
                else
                    RemoveOccluderSnapshot(key);
            }
        }

        public void PruneInactiveToBudget(long maximumRetainedBytes)
        {
            if (RetainedEntityBytes <= maximumRetainedBytes)
                return;

            _budgetCandidates.Clear();
            foreach (var (key, state) in CasterEntitySnapshots)
            {
                if (!state.Residency.Active)
                {
                    _budgetCandidates.Add(new GeometrySnapshotBudgetCandidate(
                        GeometrySnapshotKind.Caster,
                        key,
                        state.LastVisibleFrame,
                        state.DeletePending));
                }
            }

            foreach (var (key, state) in OccluderEntitySnapshots)
            {
                if (!state.Residency.Active)
                {
                    _budgetCandidates.Add(new GeometrySnapshotBudgetCandidate(
                        GeometrySnapshotKind.Occluder,
                        key,
                        state.LastVisibleFrame,
                        state.DeletePending));
                }
            }

            if (_budgetCandidates.Count == 0)
                return;

            _budgetCandidates.Sort(BudgetCandidateComparison);
            for (var i = 0; i < _budgetCandidates.Count && RetainedEntityBytes > maximumRetainedBytes; i++)
            {
                var candidate = _budgetCandidates[i];
                if (candidate.Kind == GeometrySnapshotKind.Caster)
                {
                    if (CasterEntitySnapshots.TryGetValue(candidate.Key, out var state) &&
                        !state.Residency.Active)
                    {
                        RemoveCasterSnapshot(candidate.Key);
                    }
                }
                else if (OccluderEntitySnapshots.TryGetValue(candidate.Key, out var state) &&
                         !state.Residency.Active)
                {
                    RemoveOccluderSnapshot(candidate.Key);
                }
            }
        }

        public uint AllocateGeometryGeneration()
        {
            _nextGeometryGeneration = unchecked(_nextGeometryGeneration + 1);
            if (_nextGeometryGeneration == 0)
                _nextGeometryGeneration = 1;
            return _nextGeometryGeneration;
        }

        private readonly record struct GeometrySnapshotBudgetCandidate(
            GeometrySnapshotKind Kind,
            ScpGeometryEntityKey Key,
            ulong LastVisibleFrame,
            bool ForceEvict);

        private enum GeometrySnapshotKind : byte
        {
            Caster,
            Occluder,
        }
    }

    private sealed class CachedResources : IDisposable
    {
        private const long GeometryCpuBudgetBytes = 16L * 1024L * 1024L;

        private Action<CachedResources>? _onDispose;

        public IRenderTexture? ShadowMask;
        public IRenderTexture? ProtectionMask;
        public OwnedTexture? LightMetadata;
        public readonly PersistentAtlasState Persistent = new();
        public GeometrySnapshotState GeometrySnapshots = new();
        public bool UseSpriteTreeForActiveSet;

        private readonly Dictionary<PersistentLightIdentity, CachedLightGeometry> _lightGeometry = new(128);
        private MapId _geometryMapId = MapId.Nullspace;
        private ulong _geometryFrameStamp;
        private uint _nextGeometryIncarnation;

        private readonly List<PooledStandardShader> _standardShaders = new(8);
        private readonly List<PooledShadowShader> _shadowShaders = new(16);
        private readonly List<PooledShadowShader> _persistentShadowShaders = new(16);
        private int _standardShaderCount;
        private int _shadowShaderCount;
        private int _persistentShadowShaderCount;
        private int _lightMetadataCapacity;
        private Rgba32[] _wideMetadataPixels = Array.Empty<Rgba32>();
        private int _wideMetadataPixelCount;
        private bool _wideMetadataValid;
        private readonly List<WideMaskPageStamp> _wideMaskPageStamps = new(128);
        private IRenderTexture? _wideMaskTarget;
        private MapId _wideMaskMapId = MapId.Nullspace;
        private Vector2i _wideMaskTargetSize;
        private UIBox2i _wideMaskBounds;
        private bool _wideMaskValid;
        private Vector2i _targetSize;
        private bool _shadowMaskPersistent;
        private readonly List<ProtectedSpriteLayer> _protectionLayers = new(256);
        private Matrix3x2 _protectionMatrix;
        private bool _protectionValid;

        public CachedResources(Action<CachedResources> onDispose)
        {
            _onDispose = onDispose;
        }

        public void BeginFrame()
        {
            _standardShaderCount = 0;
            _shadowShaderCount = 0;
            _persistentShadowShaderCount = 0;
        }

        public void RemovePointLight(PersistentLightIdentity identity)
        {
            _lightGeometry.Remove(identity);
            Persistent.Remove(identity);
            InvalidateWideShadowMask();
        }

        public void RemoveGeometrySource(ScpGeometryEntityKey key)
        {
            GeometrySnapshots.RemoveGeometrySource(key);
            InvalidateWideShadowMask();
        }

        public GeometrySnapshotState GetGeometrySnapshots(MapId mapId)
        {
            BindGeometryMap(mapId);
            GeometrySnapshots.BeginFrame();
            return GeometrySnapshots;
        }

        public void BindGeometryFrame(
            MapId mapId,
            List<ScpShadowLightData> lights,
            int lightCount,
            LightGeometryBuffer emptyGeometry,
            List<LightGeometryBuffer> frameGeometry)
        {
            BindGeometryMap(mapId);

            _geometryFrameStamp++;
            frameGeometry.Clear();
            for (var index = 0; index < lightCount; index++)
            {
                var light = lights[index];
                if (!light.CastShadows || light.Radius <= 0f || light.Energy <= 0f)
                {
                    frameGeometry.Add(emptyGeometry);
                    continue;
                }

                var identity = new PersistentLightIdentity(light.Owner, light.CreationTick);
                if (!_lightGeometry.TryGetValue(identity, out var cached))
                {
                    cached = new CachedLightGeometry(AllocateGeometryIncarnation());
                    _lightGeometry.Add(identity, cached);
                }

                cached.LastVisibleFrame = _geometryFrameStamp;
                frameGeometry.Add(cached.Geometry);
            }

            // Snapshot residency is refreshed by PrepareGeometryBatch. Pruning
            // before that point could evict a caster that just re-entered PVS.
        }

        private void BindGeometryMap(MapId mapId)
        {
            if (_geometryMapId != mapId)
            {
                InvalidateWideShadowMask();
                _lightGeometry.Clear();
                if (_geometryMapId != MapId.Nullspace)
                    GeometrySnapshots = new GeometrySnapshotState();
                _geometryMapId = mapId;
                _geometryFrameStamp = 0;
            }
        }

        public void PruneGeometryCache(int maxShadowLights)
        {
            PruneGeometry(GetGeometryRecordLimit(maxShadowLights));
        }

        private static int GetGeometryRecordLimit(int maxShadowLights)
        {
            return (int) Math.Min(int.MaxValue, Math.Max(1L, (long) maxShadowLights * 2L));
        }

        private uint AllocateGeometryIncarnation()
        {
            _nextGeometryIncarnation = unchecked(_nextGeometryIncarnation + 1);
            if (_nextGeometryIncarnation == 0)
                _nextGeometryIncarnation = 1;
            return _nextGeometryIncarnation;
        }

        private void PruneGeometry(int maxRecords)
        {
            PruneGeometry(maxRecords, GeometryCpuBudgetBytes);
        }

        private void PruneGeometry(int maxRecords, long maximumBytes)
        {
            var lightGeometryBytes = EstimateLightGeometryBytes();
            var snapshotBytes = GeometrySnapshots.EstimatedBytes;
            while (_lightGeometry.Count > maxRecords || lightGeometryBytes + snapshotBytes > maximumBytes)
            {
                PersistentLightIdentity oldestIdentity = default;
                CachedLightGeometry? oldest = null;
                foreach (var (identity, cached) in _lightGeometry)
                {
                    var visible = cached.LastVisibleFrame == _geometryFrameStamp;
                    if (visible)
                        continue;

                    if (oldest == null || IsBetterGeometryEvictionCandidate(
                            identity,
                            cached,
                            oldestIdentity,
                            oldest))
                    {
                        oldest = cached;
                        oldestIdentity = identity;
                    }
                }

                // Current-frame buffers are referenced by _lightGeometryBuffers.
                // Evicting them here immediately recreates the same large data on
                // the next frame. The visible set is bounded by MaxShadowLights;
                // retain it temporarily and prune stale PVS entries next frame.
                if (oldest == null)
                    break;

                _lightGeometry.Remove(oldestIdentity);
                lightGeometryBytes -= oldest.Geometry.EstimatedBytes;
            }

            var retainedSnapshotBudget = maximumBytes -
                lightGeometryBytes -
                GeometrySnapshots.NonEvictableEstimatedBytes;
            GeometrySnapshots.PruneInactiveToBudget(Math.Max(0L, retainedSnapshotBudget));
        }

        private static bool IsBetterGeometryEvictionCandidate(
            PersistentLightIdentity identity,
            CachedLightGeometry candidate,
            PersistentLightIdentity selectedIdentity,
            CachedLightGeometry selected)
        {
            return candidate.LastVisibleFrame < selected.LastVisibleFrame ||
                candidate.LastVisibleFrame == selected.LastVisibleFrame &&
                identity.CompareTo(selectedIdentity) < 0;
        }

        private long EstimateLightGeometryBytes()
        {
            var result = 128L + (long) _lightGeometry.EnsureCapacity(0) * 64L;
            foreach (var cached in _lightGeometry.Values)
                result += cached.Geometry.EstimatedBytes;
            return result;
        }

        public ShaderInstance GetStandardShader(ShaderPrototype prototype, float curveFactor)
        {
            if (_standardShaderCount == _standardShaders.Count)
                _standardShaders.Add(new PooledStandardShader(prototype.InstanceUnique()));

            return _standardShaders[_standardShaderCount++].Configure(curveFactor);
        }

        public ShaderInstance GetShadowShader(
            ShaderPrototype prototype,
            Texture shadowMask,
            Texture protectionMask,
            Texture lightMetadata,
            float metadataPixelSize,
            Vector2 shadowUvScale,
            Vector4 lightCenterDecode,
            float softness,
            float falloff,
            float curveFactor,
            bool hasProtection,
            bool directionalFovActive,
            Vector2 directionalFovOffset,
            Vector2 directionalViewDirection,
            Vector2 directionalRadialParameters,
            Vector2 directionalConeThresholds)
        {
            if (_shadowShaderCount == _shadowShaders.Count)
                _shadowShaders.Add(new PooledShadowShader(prototype.InstanceUnique()));

            return _shadowShaders[_shadowShaderCount++].Configure(
                shadowMask,
                protectionMask,
                lightMetadata,
                metadataPixelSize,
                shadowUvScale,
                lightCenterDecode,
                softness,
                falloff,
                curveFactor,
                hasProtection,
                directionalFovActive,
                directionalFovOffset,
                directionalViewDirection,
                directionalRadialParameters,
                directionalConeThresholds);
        }

        public ShaderInstance GetPersistentShadowShader(
            ShaderPrototype prototype,
            Texture shadowMask,
            Texture protectionMask,
            Texture lightMetadata,
            float metadataPixelSize,
            float softness,
            float falloff,
            float curveFactor,
            bool hasProtection,
            bool directionalFovActive,
            Vector2 directionalFovOffset,
            Vector2 directionalViewDirection,
            Vector2 directionalRadialParameters,
            Vector2 directionalConeThresholds)
        {
            if (_persistentShadowShaderCount == _persistentShadowShaders.Count)
                _persistentShadowShaders.Add(new PooledShadowShader(prototype.InstanceUnique()));

            return _persistentShadowShaders[_persistentShadowShaderCount++].Configure(
                shadowMask,
                protectionMask,
                lightMetadata,
                metadataPixelSize,
                null,
                null,
                softness,
                falloff,
                curveFactor,
                hasProtection,
                directionalFovActive,
                directionalFovOffset,
                directionalViewDirection,
                directionalRadialParameters,
                directionalConeThresholds);
        }

        public void SetSize(Vector2i size)
        {
            if (_targetSize == size)
                return;

            _targetSize = size;
            InvalidateWideShadowMask();
            ProtectionMask?.Dispose();
            ProtectionMask = null;
            _protectionValid = false;
            _protectionLayers.Clear();
        }

        public bool EnsureShadowMask(IClyde clyde, Vector2i atlasSize)
        {
            if (!_shadowMaskPersistent && ShadowMask?.Size == atlasSize)
                return false;

            InvalidateWideShadowMask();
            ShadowMask?.Dispose();
            var samples = new TextureSampleParameters { Filter = true };
            ShadowMask = clyde.CreateRenderTarget(
                atlasSize,
                new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8),
                samples,
                "scp-shadow-packed-mask");
            _shadowMaskPersistent = false;
            return true;
        }

        public bool EnsurePersistentShadowMask(IClyde clyde)
        {
            InvalidateWideShadowMask();
            var size = new Vector2i(
                ScpShadowAtlasBuddyAllocator.AtlasSize,
                ScpShadowAtlasBuddyAllocator.AtlasSize);
            if (_shadowMaskPersistent && ShadowMask?.Size == size)
                return false;

            ShadowMask?.Dispose();
            var samples = new TextureSampleParameters { Filter = true };
            ShadowMask = clyde.CreateRenderTarget(
                size,
                new RenderTargetFormatParameters(RenderTargetColorFormat.Rgba8),
                samples,
                "scp-shadow-packed-mask");
            _shadowMaskPersistent = true;
            return true;
        }

        public bool IsWideShadowMaskCurrent(
            MapId mapId,
            Vector2i targetSize,
            UIBox2i bounds,
            ReadOnlySpan<WideMaskPageStamp> stamps)
        {
            if (!_wideMaskValid ||
                !ReferenceEquals(_wideMaskTarget, ShadowMask) ||
                _wideMaskMapId != mapId ||
                _wideMaskTargetSize != targetSize ||
                _wideMaskBounds != bounds ||
                _wideMaskPageStamps.Count != stamps.Length)
            {
                return false;
            }

            for (var index = 0; index < stamps.Length; index++)
            {
                if (_wideMaskPageStamps[index] != stamps[index])
                    return false;
            }

            return true;
        }

        public void CommitWideShadowMask(
            MapId mapId,
            Vector2i targetSize,
            UIBox2i bounds,
            ReadOnlySpan<WideMaskPageStamp> stamps)
        {
            _wideMaskPageStamps.Clear();
            for (var index = 0; index < stamps.Length; index++)
                _wideMaskPageStamps.Add(stamps[index]);

            _wideMaskTarget = ShadowMask;
            _wideMaskMapId = mapId;
            _wideMaskTargetSize = targetSize;
            _wideMaskBounds = bounds;
            _wideMaskValid = ShadowMask != null;
        }

        public void InvalidateWideShadowMask()
        {
            _wideMaskValid = false;
            _wideMaskTarget = null;
            _wideMaskMapId = MapId.Nullspace;
            _wideMaskTargetSize = default;
            _wideMaskBounds = default;
            _wideMaskPageStamps.Clear();
        }

        public bool EnsureProtectionMask(IClyde clyde)
        {
            var size = _targetSize;
            if (ProtectionMask?.Size == size)
                return false;

            ProtectionMask?.Dispose();
            var samples = new TextureSampleParameters { Filter = true };
            ProtectionMask = clyde.CreateRenderTarget(
                size,
                new RenderTargetFormatParameters(RenderTargetColorFormat.R8),
                samples,
                "scp-shadow-protection-mask");
            _protectionValid = false;
            _protectionLayers.Clear();
            return true;
        }

        public bool IsProtectionMaskCurrent(
            in Matrix3x2 targetMatrix,
            List<ProtectedSpriteLayer> layers)
        {
            if (!_protectionValid ||
                !_protectionMatrix.Equals(targetMatrix) ||
                _protectionLayers.Count != layers.Count)
            {
                return false;
            }

            for (var index = 0; index < layers.Count; index++)
            {
                if (_protectionLayers[index] != layers[index])
                    return false;
            }

            return true;
        }

        public void CommitProtectionMask(
            in Matrix3x2 targetMatrix,
            List<ProtectedSpriteLayer> layers)
        {
            _protectionMatrix = targetMatrix;
            _protectionLayers.Clear();
            _protectionLayers.AddRange(layers);
            _protectionValid = true;
        }

        public void EnsureLightMetadata(IClyde clyde, int lightCount)
        {
            var requiredCapacity = Math.Max(lightCount, 1);
            if (LightMetadata != null && _lightMetadataCapacity >= requiredCapacity)
                return;

            LightMetadata?.Dispose();
            var parameters = TextureLoadParameters.Default;
            parameters.Srgb = false;
            parameters.Preload = false;
            parameters.SampleParameters = new TextureSampleParameters
            {
                Filter = false,
                WrapMode = TextureWrapMode.None,
            };
            LightMetadata = clyde.CreateBlankTexture<Rgba32>(
                new Vector2i(requiredCapacity * 2, 1),
                "scp-shadow-light-metadata",
                parameters);
            _lightMetadataCapacity = requiredCapacity;
            _wideMetadataValid = false;
            Persistent.InvalidateMetadata();
        }

        public bool IsWideMetadataCurrent(ReadOnlySpan<Rgba32> pixels)
        {
            return _wideMetadataValid &&
                   _wideMetadataPixelCount == pixels.Length &&
                   pixels.SequenceEqual(_wideMetadataPixels.AsSpan(0, _wideMetadataPixelCount));
        }

        public void CommitWideMetadata(ReadOnlySpan<Rgba32> pixels)
        {
            if (_wideMetadataPixels.Length < pixels.Length)
                Array.Resize(ref _wideMetadataPixels, Math.Max(pixels.Length, _wideMetadataPixels.Length * 2));

            pixels.CopyTo(_wideMetadataPixels);
            _wideMetadataPixelCount = pixels.Length;
            _wideMetadataValid = true;

            // The persistent path shares the same ordinary texture. It must not
            // mistake bounds from an older upload for the texture's contents.
            Persistent.InvalidateMetadata();
        }

        public void CommitPersistentMetadataUpload()
        {
            // Likewise, a later wide frame must upload its exact screen-space
            // records even when its CPU-side values did not change.
            _wideMetadataValid = false;
        }

        public void Dispose()
        {
            for (var i = 0; i < _standardShaders.Count; i++)
                _standardShaders[i].Dispose();
            _standardShaders.Clear();

            for (var i = 0; i < _shadowShaders.Count; i++)
                _shadowShaders[i].Dispose();
            _shadowShaders.Clear();

            for (var i = 0; i < _persistentShadowShaders.Count; i++)
                _persistentShadowShaders[i].Dispose();
            _persistentShadowShaders.Clear();

            Persistent.Dispose();
            _lightGeometry.Clear();

            ShadowMask?.Dispose();
            ProtectionMask?.Dispose();
            LightMetadata?.Dispose();
            ShadowMask = null;
            ProtectionMask = null;
            LightMetadata = null;
            _lightMetadataCapacity = 0;
            _wideMetadataPixelCount = 0;
            _wideMetadataValid = false;
            InvalidateWideShadowMask();
            _targetSize = default;
            _shadowMaskPersistent = false;
            _protectionValid = false;
            _protectionLayers.Clear();
            _geometryMapId = MapId.Nullspace;
            _geometryFrameStamp = 0;
            var onDispose = _onDispose;
            _onDispose = null;
            onDispose?.Invoke(this);
        }

        private sealed class CachedLightGeometry(uint incarnation)
        {
            public readonly LightGeometryBuffer Geometry = new(incarnation);
            public ulong LastVisibleFrame;
        }

        private sealed class PooledShadowShader(ShaderInstance shader) : IDisposable
        {
            private readonly ShaderInstance _shader = shader;
            private readonly Vector2[] _directionalFovParameters = new Vector2[4];
            private Texture? _shadowMask;
            private Texture? _protectionMask;
            private Texture? _lightMetadata;
            private float _metadataPixelSize;
            private Vector2 _shadowUvScale;
            private Vector4 _lightCenterDecode;
            private Vector4 _lightGroupParameters;
            private bool _directionalFovActive;
            private bool _configured;

            public ShaderInstance Configure(
                Texture shadowMask,
                Texture protectionMask,
                Texture lightMetadata,
                float metadataPixelSize,
                Vector2? shadowUvScale,
                Vector4? lightCenterDecode,
                float softness,
                float falloff,
                float curveFactor,
                bool hasProtection,
                bool directionalFovActive,
                Vector2 directionalFovOffset,
                Vector2 directionalViewDirection,
                Vector2 directionalRadialParameters,
                Vector2 directionalConeThresholds)
            {
                if (!_configured || !ReferenceEquals(_shadowMask, shadowMask))
                {
                    _shadowMask = shadowMask;
                    _shader.SetParameter("shadowMask", shadowMask);
                }

                if (!_configured || !ReferenceEquals(_protectionMask, protectionMask))
                {
                    _protectionMask = protectionMask;
                    _shader.SetParameter("protectionMask", protectionMask);
                }

                if (!_configured || !ReferenceEquals(_lightMetadata, lightMetadata))
                {
                    _lightMetadata = lightMetadata;
                    _shader.SetParameter("lightMetadata", lightMetadata);
                }

                if (!_configured || _metadataPixelSize != metadataPixelSize)
                {
                    _metadataPixelSize = metadataPixelSize;
                    _shader.SetParameter("metadataPixelSize", metadataPixelSize);
                }

                if (shadowUvScale is { } scale &&
                    (!_configured || _shadowUvScale != scale))
                {
                    _shadowUvScale = scale;
                    _shader.SetParameter("shadowUvScale", scale);
                }

                if (lightCenterDecode is { } decode &&
                    (!_configured || _lightCenterDecode != decode))
                {
                    _lightCenterDecode = decode;
                    _shader.SetParameter("lightCenterDecode", decode);
                }

                var lightGroupParameters = new Vector4(softness, falloff, curveFactor, hasProtection ? 1f : 0f);
                if (!_configured || _lightGroupParameters != lightGroupParameters)
                {
                    _lightGroupParameters = lightGroupParameters;
                    _shader.SetParameter("lightGroupParameters", lightGroupParameters);
                }

                if (!_configured || _directionalFovActive != directionalFovActive)
                {
                    _directionalFovActive = directionalFovActive;
                    _shader.SetParameter("directionalFovMode", directionalFovActive ? 1 : 0);
                }

                if (directionalFovActive &&
                    (!_configured ||
                     _directionalFovParameters[0] != directionalFovOffset ||
                     _directionalFovParameters[1] != directionalViewDirection ||
                     _directionalFovParameters[2] != directionalRadialParameters ||
                     _directionalFovParameters[3] != directionalConeThresholds))
                {
                    _directionalFovParameters[0] = directionalFovOffset;
                    _directionalFovParameters[1] = directionalViewDirection;
                    _directionalFovParameters[2] = directionalRadialParameters;
                    _directionalFovParameters[3] = directionalConeThresholds;
                    _shader.SetParameter("directionalFovParameters", _directionalFovParameters);
                }

                _configured = true;

                return _shader;
            }

            public void Dispose()
            {
                _shader.Dispose();
            }
        }

        private sealed class PooledStandardShader(ShaderInstance shader) : IDisposable
        {
            private readonly ShaderInstance _shader = shader;
            private float _curveFactor;
            private bool _configured;

            public ShaderInstance Configure(float curveFactor)
            {
                if (!_configured || _curveFactor != curveFactor)
                {
                    _curveFactor = curveFactor;
                    _shader.SetParameter("curveFactor", curveFactor);
                    _configured = true;
                }

                return _shader;
            }

            public void Dispose()
            {
                _shader.Dispose();
            }
        }
    }

    #endregion
}
