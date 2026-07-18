using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Profiling;
using Robust.Shared.Timing;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const long PersistentCpuBudgetBytes = 16L * 1024L * 1024L;

    private readonly List<PersistentPlannedLight> _persistentPlannedLights = new(128);
    private readonly List<PersistentAttemptStamp> _persistentAttemptStamps = new(128);
    private readonly List<PersistentVisibleLight> _persistentVisibleLights = new(128);
    private readonly List<PersistentVisibleLight> _persistentDirtyLights = new(128);
    private readonly List<PersistentVisibleLight> _persistentDirtyEntries = new(128);
    private static readonly Comparison<PersistentPlannedLight> PersistentPlannedLightComparison =
        ComparePersistentPlannedLights;
    private static readonly Comparison<PersistentAttemptStamp> PersistentAttemptStampComparison =
        ComparePersistentAttemptStamps;
    private static readonly Comparison<PersistentVisibleLight> PersistentVisibleLightComparison =
        ComparePersistentVisibleLights;
    private static readonly Comparison<PersistentVisibleLight> PersistentDirtyLightComparison =
        ComparePersistentDirtyLights;
    private readonly List<DrawVertexUV2DColor> _persistentClearVertices = new(768);
    // Allocate the one allowed primitive batch outside the render hot path. A PVS burst can otherwise
    // repeatedly grow and copy this list while the first dirty atlas page is being assembled.
    private readonly List<DrawVertexUV2DColor> _persistentMaskVertices = new(MaxPrimitiveVerticesPerDraw);
    private Rgba32[] _persistentMetadataPixels = new Rgba32[256];

    private void ClearPersistentPackingReferences()
    {
        _persistentPlannedLights.Clear();
        _persistentAttemptStamps.Clear();
        _persistentVisibleLights.Clear();
        _persistentDirtyLights.Clear();
        _persistentDirtyEntries.Clear();
    }

    private bool TryDrawPersistentLights(CachedResources resources, int lightCount)
    {
        var state = resources.Persistent;
        _persistentPlannedLights.Clear();
        _persistentAttemptStamps.Clear();

        var blockingFallback = ScpPersistentFallbackReason.None;

        var layoutSignature = 14695981039346656037UL;
        for (var lightIndex = 0; lightIndex < lightCount; lightIndex++)
        {
            var light = _lights[lightIndex];
            if (light.Radius <= 0f || light.Energy <= 0f || !light.CastShadows)
                continue;

            var geometry = _lightGeometryBuffers[lightIndex];
            if (!geometry.GeometryPending && !geometry.HasMask)
                continue;

            var softness = GetLightSoftness(light);
            var identity = new PersistentLightIdentity(light.Owner, light.CreationTick);
            var basis = ScpPersistentShadowMath.GetBasis(
                light.MaskRotation,
                light.EntityRotation,
                light.Mask != null,
                light.MaskAutoRotate);
            var diameter = ScpPersistentShadowMath.GetLightDiameterPixels(
                light.Radius,
                basis,
                _targetMatrix);

            // Hard shadows intentionally keep using the pixel-identical wide path.
            // Keep scanning so the viewport-wide latch sees the complete, stable
            // visible layout instead of depending on PVS enumeration order.
            if (softness <= 0f)
            {
                _persistentAttemptStamps.Add(new PersistentAttemptStamp(
                    identity,
                    default,
                    ScpPersistentFallbackReason.HardShadow));
                blockingFallback = ScpPersistentFallbackReason.HardShadow;
                continue;
            }

            if (!ScpPersistentShadowMath.TryGetRequestedSize(
                    diameter,
                    softness,
                    out var padding,
                    out var requested))
            {
                _persistentAttemptStamps.Add(new PersistentAttemptStamp(
                    identity,
                    default,
                    ScpPersistentFallbackReason.OversizedCell));
                if (blockingFallback == ScpPersistentFallbackReason.None)
                    blockingFallback = ScpPersistentFallbackReason.OversizedCell;
                continue;
            }

            _persistentAttemptStamps.Add(new PersistentAttemptStamp(
                identity,
                requested,
                ScpPersistentFallbackReason.None));

            _persistentPlannedLights.Add(new PersistentPlannedLight(
                lightIndex,
                light,
                identity,
                basis,
                diameter,
                padding,
                requested,
                softness));

        }

        if (_persistentAttemptStamps.Count == 0)
        {
            // A shadow-enabled viewport can legitimately contain no caster or
            // occluder geometry. Treat it as a completed standard-light frame;
            // falling through to wide would repeat atlas invalidation and the
            // complete geometry bookkeeping for no visual benefit.
            for (var lightIndex = 0; lightIndex < lightCount; lightIndex++)
            {
                var light = _lights[lightIndex];
                if (light.Radius > 0f && light.Energy > 0f)
                    AddStandardLight(light);
            }

            state.CancelInvisiblePending();
            state.ClearLayoutFailure();
            state.ClearViewportWideFallback();
            return true;
        }

        if (state.HasViewportWideFallback ||
            blockingFallback != ScpPersistentFallbackReason.None)
        {
            var attemptSignature = PreparePersistentAttemptSignature();
            var attemptStamps = CollectionsMarshal.AsSpan(_persistentAttemptStamps);
            if (state.ShouldDeferPersistentAttempt(
                    attemptSignature,
                    attemptStamps,
                    out _))
            {
                return false;
            }

            if (blockingFallback != ScpPersistentFallbackReason.None)
            {
                state.RememberViewportWideFallback(
                    attemptSignature,
                    attemptStamps,
                    blockingFallback);
                return false;
            }
        }

        _persistentPlannedLights.Sort(PersistentPlannedLightComparison);
        for (var index = 0; index < _persistentPlannedLights.Count; index++)
        {
            var planned = _persistentPlannedLights[index];
            layoutSignature = MixPersistentSignature(layoutSignature, planned.Identity.GetHashCode());
            layoutSignature = MixPersistentSignature(layoutSignature, planned.RequestedSize.GetHashCode());
        }

        var plannedLights = CollectionsMarshal.AsSpan(_persistentPlannedLights);
        if (state.IsKnownLayoutFailure(layoutSignature, plannedLights))
        {
            return false;
        }

        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.AtlasPacking")
                   : (ProfManager.GroupGuard?) null)
        {
            if (!state.CanPackLayout(
                    layoutSignature,
                    plannedLights))
            {
                _persistentVisibleLights.Clear();
                state.RememberLayoutFailure(
                    layoutSignature,
                    plannedLights,
                    ScpPersistentLayoutFailureKind.LayoutOverflow);
                var attemptSignature = PreparePersistentAttemptSignature();
                state.RememberViewportWideFallback(
                    attemptSignature,
                    CollectionsMarshal.AsSpan(_persistentAttemptStamps),
                    ScpPersistentFallbackReason.LayoutOverflow);
                return false;
            }

            if (!TryPackPersistentLights(state))
            {
                _persistentVisibleLights.Clear();
                state.RememberLayoutFailure(
                    layoutSignature,
                    plannedLights,
                    ScpPersistentLayoutFailureKind.AllocationFailure);
                return false;
            }
        }

        var maxRecords = (int) Math.Min(int.MaxValue, Math.Max(1L, (long) _system.MaxShadowLights * 2L));
        if (!state.Prune(maxRecords, PersistentCpuBudgetBytes))
        {
            _persistentVisibleLights.Clear();
            state.RememberLayoutFailure(
                layoutSignature,
                plannedLights,
                ScpPersistentLayoutFailureKind.CpuBudget);
            return false;
        }

        var recreated = resources.EnsurePersistentShadowMask(_clyde);
        if (recreated)
        {
            state.InvalidateAtlas();
            // AtlasGeneration is part of the exact key. Rebuild only the visible set.
            for (var index = 0; index < _persistentVisibleLights.Count; index++)
            {
                var visible = _persistentVisibleLights[index];
                var planned = visible.Planned;
                var entry = visible.Entry;
                var geometry = _lightGeometryBuffers[planned.GeometryIndex];
                var layout = ScpPersistentShadowMath.GetLayout(
                    entry.Slot,
                    planned.Diameter,
                    planned.Padding);
                entry.Layout = layout;
                var key = CreatePersistentMaskKey(planned, geometry, entry, state.AtlasGeneration);
                if (!geometry.GeometryPending)
                {
                    state.SetDesired(entry, key, geometry.MaskPendingSinceFrame);
                    if (entry.IsCurrent(key))
                        geometry.MaskPendingSinceFrame = 0;
                }
                else
                    entry.ReservePendingSince(geometry.GeometryPendingSinceFrame);
                _persistentVisibleLights[index] = visible with { MaskKey = key };
            }
        }

        PreparePersistentMaskUpdates(state);
        DrawPersistentMaskUpdates(resources);
        DrawPersistentContributions(resources, lightCount);
        state.CancelInvisiblePending();
        state.ClearLayoutFailure();
        state.ClearViewportWideFallback();
        return true;
    }

    private bool TryPackPersistentLights(PersistentAtlasState state)
    {
        // Exact linear validation keeps the common static frame out of
        // the dictionary and allocator; any mismatch uses the existing full path.
        if (state.HasReusablePacking && TryReusePersistentPacking(state))
        {
            return true;
        }

        state.InvalidatePackingReuse();
        _persistentVisibleLights.Clear();
        var plannedLights = CollectionsMarshal.AsSpan(_persistentPlannedLights);
        for (var index = 0; index < plannedLights.Length; index++)
            state.VisibleIdentities.Add(plannedLights[index].Identity);

        for (var index = 0; index < plannedLights.Length; index++)
        {
            ref readonly var planned = ref plannedLights[index];
            if (!state.TryGetOrAllocate(planned.Identity, planned.RequestedSize, out var entry))
            {
                // A clean layout already fitted during preflight. The live
                // allocator can still be fragmented by retained PVS entries;
                // rebuild only the visible layout without clearing the RT.
                if (!state.TryRepackVisible(plannedLights))
                    return false;

                _persistentVisibleLights.Clear();
                for (var repackedIndex = 0; repackedIndex < plannedLights.Length; repackedIndex++)
                {
                    ref readonly var repacked = ref plannedLights[repackedIndex];
                    if (!state.TryGet(repacked.Identity, out var repackedEntry) || !repackedEntry.HasSlot)
                        return false;

                    AddPersistentVisibleLight(state, repacked, repackedEntry);
                }

                _persistentVisibleLights.Sort(PersistentVisibleLightComparison);
                state.MarkPackingReusable();
                return true;
            }

            AddPersistentVisibleLight(state, planned, entry);
        }

        _persistentVisibleLights.Sort(PersistentVisibleLightComparison);
        state.MarkPackingReusable();
        return true;
    }

    private void AddPersistentVisibleLight(
        PersistentAtlasState state,
        in PersistentPlannedLight planned,
        PersistentLightEntry entry)
    {
        var geometry = _lightGeometryBuffers[planned.GeometryIndex];
        var layout = ScpPersistentShadowMath.GetLayout(
            entry.Slot,
            planned.Diameter,
            planned.Padding);
        entry.Layout = layout;
        var key = CreatePersistentMaskKey(planned, geometry, entry, state.AtlasGeneration);
        if (!geometry.GeometryPending)
        {
            state.SetDesired(entry, key, geometry.MaskPendingSinceFrame);
            if (entry.IsCurrent(key))
                geometry.MaskPendingSinceFrame = 0;
        }
        else
            entry.ReservePendingSince(geometry.GeometryPendingSinceFrame);

        entry.LastVisibleFrame = state.FrameStamp;
        _persistentVisibleLights.Add(new PersistentVisibleLight(planned, entry, key));
    }

    private bool TryReusePersistentPacking(PersistentAtlasState state)
    {
        _persistentVisibleLights.Clear();
        var plannedLights = CollectionsMarshal.AsSpan(_persistentPlannedLights);
        for (var index = 0; index < plannedLights.Length; index++)
        {
            ref readonly var planned = ref plannedLights[index];
            if (!state.TryGet(planned.Identity, out var entry))
                return false;

            if (!ScpShadowAtlasBuddyAllocator.TryGetRequiredBlockSize(
                    planned.RequestedSize,
                    out var requiredBlockSize) ||
                !entry.HasSlot ||
                entry.Slot.Width != requiredBlockSize.X ||
                entry.Slot.Height != requiredBlockSize.Y)
            {
                return false;
            }

            var geometry = _lightGeometryBuffers[planned.GeometryIndex];
            // The requested dimensions can move within the same buddy class.
            // Keep the exact layout in the mapping key and metadata current.
            entry.Layout = ScpPersistentShadowMath.GetLayout(
                entry.Slot,
                planned.Diameter,
                planned.Padding);
            var key = CreatePersistentMaskKey(planned, geometry, entry, state.AtlasGeneration);
            if (!geometry.GeometryPending && !entry.IsDesired(key))
                return false;
            if (geometry.GeometryPending)
                entry.ReservePendingSince(geometry.GeometryPendingSinceFrame);
            else if (entry.IsCurrent(key))
                geometry.MaskPendingSinceFrame = 0;

            state.VisibleIdentities.Add(planned.Identity);
            entry.LastVisibleFrame = state.FrameStamp;
            _persistentVisibleLights.Add(new PersistentVisibleLight(planned, entry, key));
        }

        _persistentVisibleLights.Sort(PersistentVisibleLightComparison);
        return true;
    }

    private ScpPersistentMaskKey CreatePersistentMaskKey(
        in PersistentPlannedLight planned,
        LightGeometryBuffer geometry,
        PersistentLightEntry entry,
        uint atlasGeneration)
    {
        var mapping = new ScpPersistentMaskMappingKey(
            planned.Identity.Owner,
            planned.Identity.CreationTick,
            planned.Light.Position,
            planned.Light.ProjectionPosition,
            planned.Light.Radius,
            planned.Basis,
            planned.Diameter,
            planned.Padding,
            planned.RequestedSize,
            planned.Softness,
            _directionalFovActive,
            _renderLocalFovException,
            _localPlayerCaster,
            entry.Slot,
            entry.Layout,
            atlasGeneration);
        return new ScpPersistentMaskKey(
            mapping,
            geometry.Incarnation,
            geometry.CasterCache.Revision,
            geometry.OccluderCache.Revision);
    }

    private void DrawPersistentEntry(
        DrawingHandleWorld handle,
        in PersistentVisibleLight visible)
    {
        var planned = visible.Planned;
        var entry = visible.Entry;
        var geometry = _lightGeometryBuffers[planned.GeometryIndex];
        var hasCasterMask = AppendPersistentGeometry(
            handle,
            CollectionsMarshal.AsSpan(geometry.CasterVertices),
            planned.Light,
            planned.Basis,
            entry.Layout);
        var hasOccluderMask = AppendPersistentGeometry(
            handle,
            CollectionsMarshal.AsSpan(geometry.OccluderVertices),
            planned.Light,
            planned.Basis,
            entry.Layout);
        entry.SetPendingMaskFlags(hasCasterMask, hasCasterMask || hasOccluderMask);
    }

    private void PreparePersistentMaskUpdates(PersistentAtlasState state)
    {
        _persistentDirtyLights.Clear();
        _persistentDirtyEntries.Clear();

        long totalVertices = 0;
        long totalArea = 0;
        var oldestAge = 0;
        for (var index = 0; index < _persistentVisibleLights.Count; index++)
        {
            var visible = _persistentVisibleLights[index];
            var geometry = _lightGeometryBuffers[visible.Planned.GeometryIndex];
            if (geometry.GeometryPending)
                continue;

            if (visible.Entry.IsCurrent(visible.MaskKey))
                continue;

            _persistentDirtyLights.Add(visible);
            totalVertices += geometry.CasterVertices.Count + geometry.OccluderVertices.Count;
            totalArea += visible.Entry.Slot.Area;
            oldestAge = Math.Max(oldestAge, state.GetPendingAge(visible.Entry));
        }

        if (_persistentDirtyLights.Count == 0)
            return;

        _persistentDirtyLights.Sort(PersistentDirtyLightComparison);
        var deferredFrames = _system.MaxDeferredShadowFrames;
        var vertexBudget = ScpPersistentShadowMath.GetDeferredWorkBudget(
            totalVertices,
            deferredFrames,
            oldestAge);
        var areaBudget = ScpPersistentShadowMath.GetDeferredWorkBudget(
            totalArea,
            deferredFrames,
            oldestAge);
        long selectedVertices = 0;
        long selectedArea = 0;

        for (var index = 0; index < _persistentDirtyLights.Count; index++)
        {
            var visible = _persistentDirtyLights[index];
            var entry = visible.Entry;
            var geometry = _lightGeometryBuffers[visible.Planned.GeometryIndex];
            var age = state.GetPendingAge(entry);
            var forced = ScpPersistentShadowMath.IsDeferredMaskUpdateDue(
                deferredFrames,
                age);
            if (!forced &&
                _persistentDirtyEntries.Count != 0 &&
                (selectedVertices >= vertexBudget || selectedArea >= areaBudget))
            {
                continue;
            }

            _persistentDirtyEntries.Add(visible);
            selectedVertices += geometry.CasterVertices.Count + geometry.OccluderVertices.Count;
            selectedArea += entry.Slot.Area;
        }

    }

    private bool AppendPersistentGeometry(
        DrawingHandleWorld handle,
        ReadOnlySpan<DrawVertexUV2DColor> vertices,
        in ScpShadowLightData light,
        in ScpShadowBasis basis,
        in ScpPersistentShadowLayout layout)
    {
        var appended = false;
        var bounds = new UIBox2(
            layout.PaddedBounds.Left,
            layout.PaddedBounds.Top,
            layout.PaddedBounds.Right,
            layout.PaddedBounds.Bottom);

        for (var vertex = 0; vertex + 2 < vertices.Length; vertex += 3)
        {
            var first = PersistentWorldToAtlas(vertices[vertex].Position, light, basis, layout);
            var second = PersistentWorldToAtlas(vertices[vertex + 1].Position, light, basis, layout);
            var third = PersistentWorldToAtlas(vertices[vertex + 2].Position, light, basis, layout);
            var relation = ScpLightingBatchPlanner.ClassifyTriangle(first, second, third, bounds);
            if (relation == ScpTriangleBoundsRelation.Outside)
                continue;

            var color = vertices[vertex].Color;
            if (relation == ScpTriangleBoundsRelation.Inside)
            {
                AppendPersistentTriangle(handle, first, second, third, color);
                appended = true;
                continue;
            }

            var count = ScpLightingBatchPlanner.ClipTriangle(
                first,
                second,
                third,
                bounds,
                _clipPolygonA,
                _clipPolygonB,
                _clipPolygonA);
            if (count < 3)
                continue;

            var fan = _clipPolygonA[0];
            for (var triangle = 1; triangle < count - 1; triangle++)
            {
                AppendPersistentTriangle(
                    handle,
                    fan,
                    _clipPolygonA[triangle],
                    _clipPolygonA[triangle + 1],
                    color);
            }

            appended = true;
        }

        return appended;
    }

    private void AppendPersistentTriangle(
        DrawingHandleWorld handle,
        Vector2 first,
        Vector2 second,
        Vector2 third,
        Color color)
    {
        _persistentMaskVertices.Add(new DrawVertexUV2DColor(first, color));
        _persistentMaskVertices.Add(new DrawVertexUV2DColor(second, color));
        _persistentMaskVertices.Add(new DrawVertexUV2DColor(third, color));

        if (_persistentMaskVertices.Count < MaxPrimitiveVerticesPerDraw)
            return;

        DrawTriangleList(
            handle,
            _whiteTexture,
            CollectionsMarshal.AsSpan(_persistentMaskVertices));
        _persistentMaskVertices.Clear();
    }

    private static Vector2 PersistentWorldToAtlas(
        Vector2 world,
        in ScpShadowLightData light,
        in ScpShadowBasis basis,
        in ScpPersistentShadowLayout layout)
    {
        var uv = ScpPersistentShadowMath.WorldToLightUv(
            world,
            light.Position,
            light.Radius,
            basis);
        return ScpPersistentShadowMath.LightUvToAtlasPixel(uv, layout);
    }

    private void DrawPersistentMaskUpdates(CachedResources resources)
    {
        _persistentClearVertices.Clear();
        for (var index = 0; index < _persistentDirtyEntries.Count; index++)
        {
            AppendPersistentClearQuad(_persistentDirtyEntries[index].Entry.Slot.Bounds);
        }

        if (_persistentClearVertices.Count == 0)
            return;

        using var profile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting.MaskUpdate")
            : (ProfManager.GroupGuard?) null;
        _renderHandle!.RenderInRenderTarget(
            resources.ShadowMask!,
            _drawPersistentMaskUpdate,
            null);
        var state = resources.Persistent;
        for (var index = 0; index < _persistentDirtyEntries.Count; index++)
        {
            var visible = _persistentDirtyEntries[index];
            state.CommitPending(visible.Entry);
            _lightGeometryBuffers[visible.Planned.GeometryIndex].MaskPendingSinceFrame = 0;
        }
        _persistentDirtyEntries.Clear();
    }

    private void DrawPersistentMaskUpdate()
    {
        var handle = _drawHandle!;
        handle.SetTransform(Matrix3x2.Identity);
        _renderHandle!.SetScissor(null);

        handle.UseShader(_atlasClearShader);
        DrawTriangleList(
            handle,
            _whiteTexture,
            CollectionsMarshal.AsSpan(_persistentClearVertices));

        _persistentMaskVertices.Clear();
        handle.UseShader(_maskShader);
        for (var index = 0; index < _persistentDirtyEntries.Count; index++)
            DrawPersistentEntry(handle, _persistentDirtyEntries[index]);

        if (_persistentMaskVertices.Count != 0)
        {
            DrawTriangleList(
                handle,
                _whiteTexture,
                CollectionsMarshal.AsSpan(_persistentMaskVertices));
            _persistentMaskVertices.Clear();
        }

        handle.UseShader(null);
    }

    private void AppendPersistentClearQuad(UIBox2i bounds)
    {
        var bottomLeft = new Vector2(bounds.Left, bounds.Bottom);
        var bottomRight = new Vector2(bounds.Right, bounds.Bottom);
        var topRight = new Vector2(bounds.Right, bounds.Top);
        var topLeft = new Vector2(bounds.Left, bounds.Top);
        _persistentClearVertices.Add(new DrawVertexUV2DColor(bottomLeft, Color.Black));
        _persistentClearVertices.Add(new DrawVertexUV2DColor(bottomRight, Color.Black));
        _persistentClearVertices.Add(new DrawVertexUV2DColor(topRight, Color.Black));
        _persistentClearVertices.Add(new DrawVertexUV2DColor(bottomLeft, Color.Black));
        _persistentClearVertices.Add(new DrawVertexUV2DColor(topRight, Color.Black));
        _persistentClearVertices.Add(new DrawVertexUV2DColor(topLeft, Color.Black));
    }

    private void DrawPersistentContributions(CachedResources resources, int lightCount)
    {
        var pageHasCasterMask = false;
        for (var index = 0; index < _persistentVisibleLights.Count; index++)
        {
            var visible = _persistentVisibleLights[index];
            if (visible.Entry.CanUseCommitted(visible.MaskKey) && visible.Entry.HasCasterMask)
            {
                pageHasCasterMask = true;
                break;
            }
        }

        if (!_currentHasProtection &&
            pageHasCasterMask &&
            _protectedSpriteLayers.Count != 0)
        {
            EnsureProtectionMask(resources);
        }

        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.MetadataUpload")
                   : (ProfManager.GroupGuard?) null)
        {
            PreparePersistentMetadata(resources);
        }

        BeginShadowLightBatches();
        var persistentIndex = 0;
        for (var lightIndex = 0; lightIndex < lightCount; lightIndex++)
        {
            var light = _lights[lightIndex];
            if (light.Radius <= 0f || light.Energy <= 0f)
                continue;

            if (persistentIndex >= _persistentVisibleLights.Count ||
                _persistentVisibleLights[persistentIndex].Planned.GeometryIndex != lightIndex)
            {
                AddStandardLight(light);
                continue;
            }

            var visible = _persistentVisibleLights[persistentIndex];
            var entry = visible.Entry;
            if (!entry.CanUseCommitted(visible.MaskKey) || !entry.HasMask)
            {
                AddStandardLight(light);
                persistentIndex++;
                continue;
            }

            var hasProtection = _currentHasProtection && entry.HasCasterMask;
            var key = new ShadowLightBatchKey(
                light.Mask ?? _whiteTexture,
                light.Falloff,
                light.CurveFactor,
                visible.Planned.Softness,
                hasProtection);
            var batch = GetShadowLightBatch(key);
            var metadataX = (2f * persistentIndex + 0.5f) / resources.LightMetadata!.Width;
            AppendLightQuad(batch.Vertices, light, new Vector2(light.Radius, metadataX));
            persistentIndex++;
        }

        using var contributionProfile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting.ShadowContributions")
            : (ProfManager.GroupGuard?) null;
        DrawPersistentShadowLightBatches(resources, pageHasCasterMask);
    }

    private void PreparePersistentMetadata(CachedResources resources)
    {
        resources.EnsureLightMetadata(_clyde, _persistentVisibleLights.Count);
        var state = resources.Persistent;
        if (state.IsMetadataCurrent(resources.LightMetadata!, _persistentVisibleLights))
            return;

        var pixelCount = _persistentVisibleLights.Count * 2;
        if (_persistentMetadataPixels.Length < pixelCount)
        {
            Array.Resize(
                ref _persistentMetadataPixels,
                Math.Max(pixelCount, _persistentMetadataPixels.Length * 2));
        }

        const float inverseAtlasSize = 1f / ScpShadowAtlasBuddyAllocator.AtlasSize;
        for (var index = 0; index < _persistentVisibleLights.Count; index++)
        {
            var inner = _persistentVisibleLights[index].Entry.Layout.InnerBounds;
            var values = new Vector4(
                inner.Left * inverseAtlasSize,
                1f - inner.Bottom * inverseAtlasSize,
                inner.Width * inverseAtlasSize,
                inner.Height * inverseAtlasSize);
            ScpShadowMetadataCodec.Encode(
                values,
                out _persistentMetadataPixels[index * 2],
                out _persistentMetadataPixels[index * 2 + 1]);
        }

        resources.LightMetadata!.SetSubImage(
            Vector2i.Zero,
            new Vector2i(pixelCount, 1),
            _persistentMetadataPixels.AsSpan(0, pixelCount));
        resources.CommitPersistentMetadataUpload();
        state.CommitMetadata(resources.LightMetadata!, _persistentVisibleLights);
    }

    private void DrawPersistentShadowLightBatches(CachedResources resources, bool pageHasCasterMask)
    {
        var handle = _drawHandle!;
        var shadowMask = resources.ShadowMask!.Texture;
        var protectionMask = resources.ProtectionMask?.Texture ?? shadowMask;
        var metadata = resources.LightMetadata!;
        var metadataPixelSize = 1f / metadata.Width;
        handle.SetTransform(_targetMatrix);

        for (var batchIndex = 0; batchIndex < _activeShadowLightBatches; batchIndex++)
        {
            var batch = _shadowLightBatches[batchIndex];
            var key = batch.Key;
            handle.UseShader(resources.GetPersistentShadowShader(
                _persistentContributionPrototype,
                shadowMask,
                protectionMask,
                metadata,
                metadataPixelSize,
                key.Softness,
                key.Falloff,
                key.CurveFactor,
                key.HasProtection,
                _directionalFovActive && pageHasCasterMask,
                _directionalFovOffset,
                _directionalViewDirection,
                _directionalRadialParameters,
                _directionalConeThresholds));
            DrawTriangleList(handle, key.Mask, CollectionsMarshal.AsSpan(batch.Vertices));
        }

        handle.UseShader(null);
    }

    private static ulong MixPersistentSignature(ulong hash, int value)
    {
        return (hash ^ (uint) value) * 1099511628211UL;
    }

    private ulong PreparePersistentAttemptSignature()
    {
        _persistentAttemptStamps.Sort(PersistentAttemptStampComparison);
        var signature = 14695981039346656037UL;
        for (var index = 0; index < _persistentAttemptStamps.Count; index++)
        {
            var stamp = _persistentAttemptStamps[index];
            signature = MixPersistentSignature(signature, stamp.Identity.GetHashCode());
            signature = MixPersistentSignature(signature, stamp.RequestedSize.GetHashCode());
            signature = MixPersistentSignature(signature, (int) stamp.FallbackReason);
        }

        return signature;
    }

    private readonly record struct PersistentPlannedLight(
        int GeometryIndex,
        ScpShadowLightData Light,
        PersistentLightIdentity Identity,
        ScpShadowBasis Basis,
        Vector2 Diameter,
        int Padding,
        Vector2i RequestedSize,
        float Softness);

    private readonly record struct PersistentVisibleLight(
        PersistentPlannedLight Planned,
        PersistentLightEntry Entry,
        ScpPersistentMaskKey MaskKey);

    private readonly record struct PersistentLayoutStamp(
        PersistentLightIdentity Identity,
        Vector2i RequestedSize);

    private readonly record struct PersistentAttemptStamp(
        PersistentLightIdentity Identity,
        Vector2i RequestedSize,
        ScpPersistentFallbackReason FallbackReason);

    private static int ComparePersistentAttemptStamps(
        PersistentAttemptStamp left,
        PersistentAttemptStamp right)
    {
        var comparison = left.Identity.CompareTo(right.Identity);
        if (comparison != 0)
            return comparison;

        comparison = left.RequestedSize.X.CompareTo(right.RequestedSize.X);
        if (comparison != 0)
            return comparison;

        comparison = left.RequestedSize.Y.CompareTo(right.RequestedSize.Y);
        return comparison != 0
            ? comparison
            : left.FallbackReason.CompareTo(right.FallbackReason);
    }

    private static int ComparePersistentPlannedLights(
        PersistentPlannedLight left,
        PersistentPlannedLight right)
    {
        // Allocate larger rectangular buddy classes first. This avoids an
        // order-dependent false overflow while identity keeps ties stable
        // across PVS reorder.
        ScpShadowAtlasBuddyAllocator.TryGetRequiredBlockSize(left.RequestedSize, out var leftRequired);
        ScpShadowAtlasBuddyAllocator.TryGetRequiredBlockSize(right.RequestedSize, out var rightRequired);
        var leftArea = (long) leftRequired.X * leftRequired.Y;
        var rightArea = (long) rightRequired.X * rightRequired.Y;
        var comparison = rightArea.CompareTo(leftArea);
        if (comparison != 0)
            return comparison;

        comparison = Math.Max(rightRequired.X, rightRequired.Y)
            .CompareTo(Math.Max(leftRequired.X, leftRequired.Y));
        if (comparison != 0)
            return comparison;

        comparison = rightRequired.X.CompareTo(leftRequired.X);
        if (comparison != 0)
            return comparison;

        comparison = left.Identity.Owner.CompareTo(right.Identity.Owner);
        return comparison != 0
            ? comparison
            : left.Identity.CreationTick.CompareTo(right.Identity.CreationTick);
    }

    private static int ComparePersistentVisibleLights(
        PersistentVisibleLight left,
        PersistentVisibleLight right)
    {
        return left.Planned.GeometryIndex.CompareTo(right.Planned.GeometryIndex);
    }

    private static int ComparePersistentDirtyLights(
        PersistentVisibleLight left,
        PersistentVisibleLight right)
    {
        var comparison = left.Entry.PendingSinceFrame.CompareTo(right.Entry.PendingSinceFrame);
        if (comparison != 0)
            return comparison;

        comparison = ScpPersistentMaskUpdateOrdering.CompareAvailability(
            left.Entry.CanUseCommitted(left.MaskKey),
            right.Entry.CanUseCommitted(right.MaskKey));
        if (comparison != 0)
            return comparison;

        comparison = left.Planned.Light.DistanceSquared.CompareTo(right.Planned.Light.DistanceSquared);
        if (comparison != 0)
            return comparison;

        comparison = left.Planned.Identity.Owner.CompareTo(right.Planned.Identity.Owner);
        return comparison != 0
            ? comparison
            : left.Planned.Identity.CreationTick.CompareTo(right.Planned.Identity.CreationTick);
    }

    private sealed class PersistentLightEntry(PersistentLightIdentity identity)
    {
        private ScpPersistentMaskKey _committedKey;
        private ScpPersistentMaskKey _pendingKey;
        private bool _committed;
        private bool _pending;
        private bool _pendingHasCasterMask;
        private bool _pendingHasMask;

        public readonly PersistentLightIdentity Identity = identity;
        public ScpShadowAtlasSlot Slot;
        public ScpPersistentShadowLayout Layout;
        public ulong LastVisibleFrame;
        public ulong PendingSinceFrame;
        public bool HasSlot;
        public bool HasCasterMask;
        public bool HasMask;
        public bool HasCommittedMask => _committed;
        public bool HasPending => _pending;

        public bool IsCurrent(in ScpPersistentMaskKey key)
        {
            return _committed && _committedKey == key;
        }

        public bool CanUseCommitted(in ScpPersistentMaskKey key)
        {
            return _committed && _committedKey.HasCompatibleMapping(key);
        }

        public bool IsDesired(in ScpPersistentMaskKey key)
        {
            return IsCurrent(key) || _pending && _pendingKey == key;
        }

        public void SetPending(in ScpPersistentMaskKey key, ulong frameStamp)
        {
            if (!_pending && PendingSinceFrame == 0)
                PendingSinceFrame = frameStamp;

            _pendingKey = key;
            _pending = true;
        }

        public void ReservePendingSince(ulong pendingSinceFrame)
        {
            if (pendingSinceFrame == 0)
                return;

            if (PendingSinceFrame == 0 || pendingSinceFrame < PendingSinceFrame)
                PendingSinceFrame = pendingSinceFrame;
        }

        public void CommitPending()
        {
            if (!_pending)
                return;

            _committedKey = _pendingKey;
            HasCasterMask = _pendingHasCasterMask;
            HasMask = _pendingHasMask;
            _committed = true;
            _pending = false;
            PendingSinceFrame = 0;
        }

        public void SetPendingMaskFlags(bool hasCasterMask, bool hasMask)
        {
            _pendingHasCasterMask = hasCasterMask;
            _pendingHasMask = hasMask;
        }

        public void CancelPending()
        {
            _pending = false;
            PendingSinceFrame = 0;
        }

        // Two exact committed/pending mapping keys dominate this object. 512 B
        // is conservative on both x64 and x86 and keeps budget accounting O(1).
        public const long EstimatedBytes = 512L;
    }

    private sealed class PersistentAtlasState : IDisposable
    {
        private const long StateInstanceBytes = 512L;
        private const long CollectionBackingBaseBytes = 64L;
        private const long LookupCapacityBytes = 64L;
        private const int IdentityElementBytes = 16;
        private const int AtlasSlotElementBytes = 16;
        private const int BoundsElementBytes = 16;
        private const int LayoutStampElementBytes = 16;
        private const int AttemptStampElementBytes = 24;

        public ScpShadowAtlasBuddyAllocator Allocator = new();
        public readonly Dictionary<PersistentLightIdentity, PersistentLightEntry> Entries = new(256);
        public readonly HashSet<PersistentLightIdentity> VisibleIdentities = new(256);
        private readonly List<PersistentLightIdentity> _dirtyQueue = new(128);
        private readonly HashSet<PersistentLightIdentity> _dirtySet = new(128);
        private readonly List<PersistentLightIdentity> _geometryDirtyQueue = new(128);
        private readonly HashSet<PersistentLightIdentity> _geometryDirtySet = new(128);
        private readonly HashSet<PersistentLightIdentity> _frameGeometryDirty = new(128);
        private readonly ScpShadowAtlasBuddyAllocator _preflightAllocator = new();
        private ScpShadowAtlasBuddyAllocator _repackAllocator = new();
        private readonly List<ScpShadowAtlasSlot> _repackSlots = new(128);
        private readonly List<Box2> _metadataBounds = new(256);
        private readonly List<PersistentLayoutStamp> _successfulPreflightLayout = new(128);
        private readonly List<PersistentLayoutStamp> _failedLayout = new(128);
        private readonly List<PersistentAttemptStamp> _viewportWideFallbackLayout = new(128);
        private OwnedTexture? _metadataTexture;
        private ulong _successfulPreflightSignature;
        private bool _hasSuccessfulPreflight;
        private bool _hasReusablePacking;
        private ulong _layoutRevision;

        public MapId MapId = MapId.Nullspace;
        public ulong FrameStamp;
        public uint AtlasGeneration;
        private ulong _failedLayoutSignature;
        private ulong _failedLayoutRevision;
        private ScpPersistentLayoutFailureKind _failedLayoutKind;
        private ulong _viewportWideFallbackSignature;
        private ulong _viewportWideFallbackLayoutRevision;
        private ulong _viewportWideFallbackLastChangeFrame;
        private ScpPersistentFallbackReason _viewportWideFallbackReason;

        public bool HasReusablePacking => _hasReusablePacking;

        public bool HasViewportWideFallback => _viewportWideFallbackSignature != 0;

        public void MarkPackingReusable()
        {
            _hasReusablePacking = true;
        }

        public void InvalidatePackingReuse()
        {
            _hasReusablePacking = false;
        }

        public void BeginFrame(MapId mapId)
        {
            if (MapId != mapId)
                Reset(mapId);

            FrameStamp++;
            VisibleIdentities.Clear();
            _frameGeometryDirty.Clear();
        }

        public bool CanPackLayout(
            ulong signature,
            ReadOnlySpan<PersistentPlannedLight> plannedLights)
        {
            if (_hasSuccessfulPreflight &&
                _successfulPreflightSignature == signature &&
                LayoutMatches(_successfulPreflightLayout, plannedLights))
            {
                return true;
            }

            _preflightAllocator.Reset();
            for (var index = 0; index < plannedLights.Length; index++)
            {
                if (!_preflightAllocator.TryAllocate(plannedLights[index].RequestedSize, out _))
                    return false;
            }

            _successfulPreflightSignature = signature;
            _hasSuccessfulPreflight = true;
            CopyLayout(_successfulPreflightLayout, plannedLights);
            return true;
        }

        public bool TryGetOrAllocate(
            PersistentLightIdentity identity,
            Vector2i requested,
            out PersistentLightEntry entry)
        {
            if (!Entries.TryGetValue(identity, out entry!))
            {
                entry = new PersistentLightEntry(identity);
                Entries.Add(identity, entry);
                AdvanceLayoutRevision();
            }

            if (!ScpShadowAtlasBuddyAllocator.TryGetRequiredBlockSize(requested, out var requiredBlockSize))
                return false;

            if (entry.HasSlot &&
                entry.Slot.Width == requiredBlockSize.X &&
                entry.Slot.Height == requiredBlockSize.Y)
                return true;

            if (entry.HasSlot)
            {
                Allocator.Free(entry.Slot);
                entry.HasSlot = false;
                AdvanceLayoutRevision();
            }

            ScpShadowAtlasSlot slot;
            while (!Allocator.TryAllocate(requested, out slot))
            {
                if (!EvictOldestInvisible())
                    return false;
            }

            entry.Slot = slot;
            entry.HasSlot = true;
            AdvanceLayoutRevision();
            return true;
        }

        public bool TryRepackVisible(ReadOnlySpan<PersistentPlannedLight> plannedLights)
        {
            _repackAllocator.Reset();
            _repackSlots.Clear();
            for (var index = 0; index < plannedLights.Length; index++)
            {
                if (!_repackAllocator.TryAllocate(plannedLights[index].RequestedSize, out var slot))
                {
                    _repackAllocator.Reset();
                    _repackSlots.Clear();
                    return false;
                }

                _repackSlots.Add(slot);
            }

            // Nothing below can fail: only after the complete scratch layout is
            // known do we replace the live allocator and its visible slots.
            for (var index = 0; index < plannedLights.Length; index++)
            {
                var identity = plannedLights[index].Identity;
                if (!Entries.ContainsKey(identity))
                {
                    Entries.Add(identity, new PersistentLightEntry(identity));
                    AdvanceLayoutRevision();
                }
            }

            // Retained PVS entries are the only entries eligible for eviction.
            // Their geometry cache is independent and can still survive PVS leave.
            while (EvictOldestInvisible())
            {
            }

            var previousAllocator = Allocator;
            Allocator = _repackAllocator;
            _repackAllocator = previousAllocator;

            for (var index = 0; index < plannedLights.Length; index++)
            {
                var entry = Entries[plannedLights[index].Identity];
                entry.Slot = _repackSlots[index];
                entry.HasSlot = true;
            }

            _repackAllocator.Reset();
            _repackSlots.Clear();
            AdvanceLayoutRevision();
            InvalidatePackingReuse();
            return true;
        }

        public bool TryGet(PersistentLightIdentity identity, out PersistentLightEntry entry)
        {
            return Entries.TryGetValue(identity, out entry!);
        }

        public void Remove(PersistentLightIdentity identity)
        {
            var removed = Entries.Remove(identity, out var entry);
            if (removed && entry is { HasSlot: true })
                Allocator.Free(entry.Slot);
            if (removed)
                AdvanceLayoutRevision();

            removed |= _dirtySet.Remove(identity);
            removed |= _geometryDirtySet.Remove(identity);
            removed |= _frameGeometryDirty.Remove(identity);
            removed |= VisibleIdentities.Remove(identity);
            if (!removed)
                return;

            CompactDirtyQueue();
            CompactGeometryDirtyQueue();
            InvalidatePackingReuse();
            _metadataTexture = null;
            _metadataBounds.Clear();
        }

        public ulong TrackGeometryDirty(
            PersistentLightIdentity identity,
            LightGeometryBuffer geometry)
        {
            _frameGeometryDirty.Add(identity);
            if (_geometryDirtySet.Add(identity))
            {
                _geometryDirtyQueue.Add(identity);
                geometry.GeometryPendingSinceFrame = FrameStamp;
            }
            else if (geometry.GeometryPendingSinceFrame == 0)
            {
                // A PVS-pruned buffer may be recreated while the stable queue
                // still contains its identity. Restarting its deadline is safer
                // than inheriting an unrelated buffer's age.
                geometry.GeometryPendingSinceFrame = FrameStamp;
            }

            return geometry.GeometryPendingSinceFrame;
        }

        public void CompleteGeometryDirty(
            PersistentLightIdentity identity,
            LightGeometryBuffer geometry)
        {
            if (geometry.GeometryPendingSinceFrame != 0 &&
                (geometry.MaskPendingSinceFrame == 0 ||
                 geometry.GeometryPendingSinceFrame < geometry.MaskPendingSinceFrame))
            {
                geometry.MaskPendingSinceFrame = geometry.GeometryPendingSinceFrame;
            }

            geometry.GeometryPendingSinceFrame = 0;
            _geometryDirtySet.Remove(identity);
        }

        public void CancelInvisibleGeometryDirty()
        {
            for (var index = 0; index < _geometryDirtyQueue.Count; index++)
            {
                var identity = _geometryDirtyQueue[index];
                if (!_geometryDirtySet.Contains(identity) || _frameGeometryDirty.Contains(identity))
                    continue;

                _geometryDirtySet.Remove(identity);
                if (Entries.TryGetValue(identity, out var entry) && !entry.HasPending)
                    entry.CancelPending();
            }

            CompactGeometryDirtyQueue();
        }

        public void CancelAllGeometryDirty()
        {
            for (var index = 0; index < _geometryDirtyQueue.Count; index++)
            {
                var identity = _geometryDirtyQueue[index];
                if (!_geometryDirtySet.Remove(identity))
                    continue;

                if (Entries.TryGetValue(identity, out var entry) && !entry.HasPending)
                    entry.CancelPending();
            }

            _geometryDirtyQueue.Clear();
            _frameGeometryDirty.Clear();
        }

        public void SetDesired(
            PersistentLightEntry entry,
            in ScpPersistentMaskKey key,
            ulong inheritedPendingSinceFrame = 0)
        {
            if (entry.IsCurrent(key))
            {
                _dirtySet.Remove(entry.Identity);
                // Also clear a geometry-reserved deadline that has not reached
                // the mask queue yet and then reverted to the committed key.
                entry.CancelPending();

                return;
            }

            entry.ReservePendingSince(inheritedPendingSinceFrame);
            entry.SetPending(key, FrameStamp);
            if (!_dirtySet.Add(entry.Identity))
                return;

            _dirtyQueue.Add(entry.Identity);
        }

        public void CommitPending(PersistentLightEntry entry)
        {
            entry.CommitPending();
            _dirtySet.Remove(entry.Identity);
        }

        public int GetPendingAge(PersistentLightEntry entry)
        {
            return (int) Math.Min(int.MaxValue, FrameStamp - entry.PendingSinceFrame);
        }

        public void CancelInvisiblePending()
        {
            for (var index = 0; index < _dirtyQueue.Count; index++)
            {
                var identity = _dirtyQueue[index];
                if (!_dirtySet.Contains(identity) || VisibleIdentities.Contains(identity))
                    continue;

                if (Entries.TryGetValue(identity, out var entry))
                    entry.CancelPending();
                _dirtySet.Remove(identity);
            }

            CompactDirtyQueue();
        }

        public void CancelAllPending()
        {
            for (var index = 0; index < _dirtyQueue.Count; index++)
            {
                var identity = _dirtyQueue[index];
                if (!_dirtySet.Remove(identity))
                    continue;

                if (Entries.TryGetValue(identity, out var entry))
                    entry.CancelPending();
            }

            _dirtyQueue.Clear();
        }

        private void CompactDirtyQueue()
        {
            if (_dirtyQueue.Count <= _dirtySet.Count * 2 + 16)
                return;

            var destination = 0;
            for (var source = 0; source < _dirtyQueue.Count; source++)
            {
                var identity = _dirtyQueue[source];
                if (!_dirtySet.Contains(identity))
                    continue;
                _dirtyQueue[destination++] = identity;
            }

            if (destination < _dirtyQueue.Count)
                _dirtyQueue.RemoveRange(destination, _dirtyQueue.Count - destination);
        }

        private void CompactGeometryDirtyQueue()
        {
            if (_geometryDirtyQueue.Count <= _geometryDirtySet.Count * 2 + 16)
                return;

            var destination = 0;
            for (var source = 0; source < _geometryDirtyQueue.Count; source++)
            {
                var identity = _geometryDirtyQueue[source];
                if (!_geometryDirtySet.Contains(identity))
                    continue;
                _geometryDirtyQueue[destination++] = identity;
            }

            if (destination < _geometryDirtyQueue.Count)
            {
                _geometryDirtyQueue.RemoveRange(
                    destination,
                    _geometryDirtyQueue.Count - destination);
            }
        }

        public bool Prune(int maxRecords, long maximumBytes)
        {
            while (Entries.Count > maxRecords || EstimateBytes() > maximumBytes)
            {
                if (!EvictOldestInvisible())
                    return false;
            }

            return true;
        }

        private bool EvictOldestInvisible()
        {
            PersistentLightEntry? oldest = null;
            foreach (var entry in Entries.Values)
            {
                if (VisibleIdentities.Contains(entry.Identity))
                    continue;
                if (oldest == null ||
                    entry.LastVisibleFrame < oldest.LastVisibleFrame ||
                    entry.LastVisibleFrame == oldest.LastVisibleFrame &&
                    entry.Identity.CompareTo(oldest.Identity) < 0)
                {
                    oldest = entry;
                }
            }

            if (oldest == null)
                return false;

            if (oldest.HasSlot)
                Allocator.Free(oldest.Slot);
            if (_dirtySet.Remove(oldest.Identity))
                oldest.CancelPending();
            Entries.Remove(oldest.Identity);
            AdvanceLayoutRevision();
            return true;
        }

        private long EstimateBytes()
        {
            return StateInstanceBytes +
                   Allocator.EstimatedBytes +
                   _preflightAllocator.EstimatedBytes +
                   _repackAllocator.EstimatedBytes +
                   (long) Entries.Count * PersistentLightEntry.EstimatedBytes +
                   EstimateLookupBacking(Entries.EnsureCapacity(0)) +
                   EstimateLookupBacking(VisibleIdentities.EnsureCapacity(0)) +
                   EstimateListBacking(_dirtyQueue.Capacity, IdentityElementBytes) +
                   EstimateLookupBacking(_dirtySet.EnsureCapacity(0)) +
                   EstimateListBacking(_geometryDirtyQueue.Capacity, IdentityElementBytes) +
                   EstimateLookupBacking(_geometryDirtySet.EnsureCapacity(0)) +
                   EstimateLookupBacking(_frameGeometryDirty.EnsureCapacity(0)) +
                   EstimateListBacking(_repackSlots.Capacity, AtlasSlotElementBytes) +
                   EstimateListBacking(_metadataBounds.Capacity, BoundsElementBytes) +
                   EstimateListBacking(_successfulPreflightLayout.Capacity, LayoutStampElementBytes) +
                   EstimateListBacking(_failedLayout.Capacity, LayoutStampElementBytes) +
                   EstimateListBacking(_viewportWideFallbackLayout.Capacity, AttemptStampElementBytes);
        }

        private static long EstimateLookupBacking(int capacity)
        {
            // Covers buckets, entries, collection object and array headers. The
            // actual Dictionary/HashSet entry payload is smaller for these keys.
            return CollectionBackingBaseBytes + (long) capacity * LookupCapacityBytes;
        }

        private static long EstimateListBacking(int capacity, int elementBytes)
        {
            return CollectionBackingBaseBytes + (long) capacity * elementBytes;
        }

        public void InvalidateAtlas()
        {
            AtlasGeneration = unchecked(AtlasGeneration + 1);
        }

        public bool IsKnownLayoutFailure(
            ulong signature,
            ReadOnlySpan<PersistentPlannedLight> plannedLights)
        {
            if (_failedLayoutSignature == 0 ||
                _failedLayoutSignature != signature ||
                !LayoutMatches(_failedLayout, plannedLights))
            {
                return false;
            }

            if (!ScpPersistentLayoutFailurePolicy.ShouldRetry(
                    _failedLayoutKind,
                    _failedLayoutRevision,
                    _layoutRevision))
            {
                return true;
            }

            ClearLayoutFailure();
            return false;
        }

        public bool ShouldDeferPersistentAttempt(
            ulong signature,
            ReadOnlySpan<PersistentAttemptStamp> attemptLayout,
            out ScpPersistentFallbackReason fallbackReason)
        {
            fallbackReason = ScpPersistentFallbackReason.None;
            if (_viewportWideFallbackSignature == 0)
                return false;

            var normalizedSignature = signature == 0 ? 1UL : signature;
            if (_viewportWideFallbackSignature != normalizedSignature ||
                !AttemptLayoutMatches(_viewportWideFallbackLayout, attemptLayout))
            {
                // PVS can deliver a different visible subset every server tick.
                // Treat it as churn, not as permission to retry the expensive
                // preflight: only 120 render frames with the same exact subset
                // prove that the viewport has settled.
                _viewportWideFallbackSignature = normalizedSignature;
                _viewportWideFallbackLastChangeFrame = FrameStamp;
                CopyAttemptLayout(_viewportWideFallbackLayout, attemptLayout);
            }

            if (ScpPersistentViewportFallbackRetryPolicy.ShouldRetry(
                    _viewportWideFallbackLayoutRevision,
                    _layoutRevision,
                    _viewportWideFallbackLastChangeFrame,
                    FrameStamp))
            {
                ClearViewportWideFallback();
                // A viewport latch supersedes the exact-layout failure cache.
                // Its expiry must perform a real preflight instead of being
                // rejected immediately by the older permanent overflow entry.
                ClearLayoutFailure();
                return false;
            }

            fallbackReason = _viewportWideFallbackReason;
            return true;
        }

        public void RememberViewportWideFallback(
            ulong signature,
            ReadOnlySpan<PersistentAttemptStamp> attemptLayout,
            ScpPersistentFallbackReason reason)
        {
            _viewportWideFallbackSignature = signature == 0 ? 1UL : signature;
            _viewportWideFallbackLayoutRevision = _layoutRevision;
            _viewportWideFallbackLastChangeFrame = FrameStamp;
            _viewportWideFallbackReason = reason;
            CopyAttemptLayout(_viewportWideFallbackLayout, attemptLayout);
        }

        public void ClearViewportWideFallback()
        {
            _viewportWideFallbackSignature = 0;
            _viewportWideFallbackLayoutRevision = 0;
            _viewportWideFallbackLastChangeFrame = 0;
            _viewportWideFallbackReason = ScpPersistentFallbackReason.None;
            _viewportWideFallbackLayout.Clear();
        }

        public void RememberLayoutFailure(
            ulong signature,
            ReadOnlySpan<PersistentPlannedLight> plannedLights,
            ScpPersistentLayoutFailureKind kind)
        {
            _failedLayoutSignature = signature == 0 ? 1UL : signature;
            _failedLayoutRevision = _layoutRevision;
            _failedLayoutKind = kind;
            CopyLayout(_failedLayout, plannedLights);
        }

        public void ClearLayoutFailure()
        {
            _failedLayoutSignature = 0;
            _failedLayoutRevision = 0;
            _failedLayoutKind = ScpPersistentLayoutFailureKind.None;
            _failedLayout.Clear();
        }

        private void AdvanceLayoutRevision()
        {
            _layoutRevision = unchecked(_layoutRevision + 1);
        }

        private static bool LayoutMatches(
            List<PersistentLayoutStamp> cached,
            ReadOnlySpan<PersistentPlannedLight> plannedLights)
        {
            if (cached.Count != plannedLights.Length)
                return false;

            for (var index = 0; index < plannedLights.Length; index++)
            {
                var planned = plannedLights[index];
                if (cached[index] != new PersistentLayoutStamp(planned.Identity, planned.RequestedSize))
                    return false;
            }

            return true;
        }

        private static void CopyLayout(
            List<PersistentLayoutStamp> destination,
            ReadOnlySpan<PersistentPlannedLight> plannedLights)
        {
            destination.Clear();
            for (var index = 0; index < plannedLights.Length; index++)
            {
                var planned = plannedLights[index];
                destination.Add(new PersistentLayoutStamp(planned.Identity, planned.RequestedSize));
            }
        }

        private static bool AttemptLayoutMatches(
            List<PersistentAttemptStamp> cached,
            ReadOnlySpan<PersistentAttemptStamp> attemptLayout)
        {
            if (cached.Count != attemptLayout.Length)
                return false;

            for (var index = 0; index < attemptLayout.Length; index++)
            {
                if (cached[index] != attemptLayout[index])
                    return false;
            }

            return true;
        }

        private static void CopyAttemptLayout(
            List<PersistentAttemptStamp> destination,
            ReadOnlySpan<PersistentAttemptStamp> attemptLayout)
        {
            destination.Clear();
            for (var index = 0; index < attemptLayout.Length; index++)
                destination.Add(attemptLayout[index]);
        }

        public bool IsMetadataCurrent(
            OwnedTexture texture,
            List<PersistentVisibleLight> visibleLights)
        {
            if (!ReferenceEquals(_metadataTexture, texture) ||
                _metadataBounds.Count != visibleLights.Count)
            {
                return false;
            }

            for (var index = 0; index < visibleLights.Count; index++)
            {
                if (!_metadataBounds[index].Equals(visibleLights[index].Entry.Layout.InnerBounds))
                    return false;
            }

            return true;
        }

        public void CommitMetadata(
            OwnedTexture texture,
            List<PersistentVisibleLight> visibleLights)
        {
            _metadataTexture = texture;
            _metadataBounds.Clear();
            for (var index = 0; index < visibleLights.Count; index++)
                _metadataBounds.Add(visibleLights[index].Entry.Layout.InnerBounds);
        }

        public void InvalidateMetadata()
        {
            _metadataTexture = null;
            _metadataBounds.Clear();
        }

        public void Reset(MapId mapId)
        {
            Allocator.Reset();
            _repackAllocator.Reset();
            _repackSlots.Clear();
            Entries.Clear();
            VisibleIdentities.Clear();
            _dirtyQueue.Clear();
            _dirtySet.Clear();
            _geometryDirtyQueue.Clear();
            _geometryDirtySet.Clear();
            _frameGeometryDirty.Clear();
            MapId = mapId;
            FrameStamp = 0;
            AtlasGeneration = unchecked(AtlasGeneration + 1);
            AdvanceLayoutRevision();
            _failedLayoutSignature = 0;
            _failedLayoutRevision = 0;
            _failedLayoutKind = ScpPersistentLayoutFailureKind.None;
            _failedLayout.Clear();
            ClearViewportWideFallback();
            _successfulPreflightLayout.Clear();
            _successfulPreflightSignature = 0;
            _hasSuccessfulPreflight = false;
            _hasReusablePacking = false;
        }

        public void Dispose()
        {
            Reset(MapId.Nullspace);
            _metadataTexture = null;
            _metadataBounds.Clear();
        }
    }
}

/// <summary>
/// Exact atlas mapping of a committed local shadow mask. Geometry revisions are
/// intentionally absent: while a replacement is deferred, the previous mask is
/// safe to sample only when every coordinate and channel semantic still matches.
/// </summary>
internal readonly record struct ScpPersistentMaskMappingKey(
    EntityUid Owner,
    GameTick CreationTick,
    Vector2 Position,
    Vector2 ProjectionPosition,
    float Radius,
    ScpShadowBasis Basis,
    Vector2 Diameter,
    int Padding,
    Vector2i RequestedSize,
    float Softness,
    bool DirectionalFovActive,
    bool RenderLocalFovException,
    EntityUid? LocalPlayerCaster,
    ScpShadowAtlasSlot Slot,
    ScpPersistentShadowLayout Layout,
    uint AtlasGeneration);

/// <summary>
/// Full desired mask state. A geometry-only change requires an atlas update but
/// preserves mapping compatibility with the last successfully committed mask.
/// </summary>
internal readonly record struct ScpPersistentMaskKey(
    ScpPersistentMaskMappingKey Mapping,
    uint GeometryIncarnation,
    uint CasterRevision,
    uint OccluderRevision)
{
    public bool HasCompatibleMapping(in ScpPersistentMaskKey other)
    {
        return Mapping == other.Mapping;
    }
}

internal static class ScpPersistentMaskUpdateOrdering
{
    public static int CompareAvailability(bool leftHasValidMask, bool rightHasValidMask)
    {
        // False sorts first: a moved light that cannot sample its committed mask
        // should regain a valid shadow before a geometry-only replacement.
        return leftHasValidMask.CompareTo(rightHasValidMask);
    }
}

internal enum ScpPersistentLayoutFailureKind : byte
{
    None,
    LayoutOverflow,
    AllocationFailure,
    CpuBudget,
}

internal static class ScpPersistentLayoutFailurePolicy
{
    public static bool ShouldRetry(
        ScpPersistentLayoutFailureKind kind,
        ulong failedLayoutRevision,
        ulong currentLayoutRevision)
    {
        return kind switch
        {
            ScpPersistentLayoutFailureKind.None => true,
            // An empty allocator could not hold this exact set. Changes to the
            // retained live atlas cannot make the same set fit later.
            ScpPersistentLayoutFailureKind.LayoutOverflow => false,
            // These failures depend on retained entries or allocator topology.
            // Wide fallback invalidates atlas contents, but deliberately does
            // not advance the layout revision and therefore cannot cause a
            // periodic retry spike by itself.
            ScpPersistentLayoutFailureKind.AllocationFailure or
                ScpPersistentLayoutFailureKind.CpuBudget =>
                failedLayoutRevision != currentLayoutRevision,
            _ => true,
        };
    }
}

internal static class ScpPersistentViewportFallbackRetryPolicy
{
    public const ulong QuietFrameCount = 120;

    public static bool ShouldRetry(
        ulong failedLayoutRevision,
        ulong currentLayoutRevision,
        ulong lastLayoutChangeFrame,
        ulong currentFrame)
    {
        if (failedLayoutRevision != currentLayoutRevision)
            return true;

        return currentFrame - lastLayoutChangeFrame >= QuietFrameCount;
    }
}
