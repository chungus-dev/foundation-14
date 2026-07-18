using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using static Robust.Client.GameObjects.SpriteComponent;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    private const float GeometryEpsilon = 0.0001f;
    private const float NearLightDistance = 1f / EyeManager.PixelsPerMeter;
    private const float SpriteAlphaThreshold = 0.1f;
    private const int MaximumDirectActiveSpriteQueries = 128;
    private const int ResumeDirectActiveSpriteQueries = 96;
    private const int MaximumSpatialChangeChecks = 8192;

    #region Frame geometry cache

    private readonly List<CachedCaster> _frameCasters = new(256);
    private readonly List<CasterFrameSourceStamp> _frameCasterSourceStamps = new(256);
    private readonly List<CachedContour> _frameContours = new(512);
    private readonly List<Vector2> _frameContourVertices = new(4096);
    private readonly List<float> _frameCasterCentersX = new(256);
    private readonly List<CachedOccluder> _frameOccluders = new(256);
    private readonly List<Vector2> _frameOccluderVertices = new(1024);
    private readonly List<float> _frameOccluderCentersX = new(256);
    private readonly List<OccluderSelectionCandidate> _occluderSelectionScratch = new(1024);
    private readonly List<ProtectedSpriteLayer> _protectedSpriteLayers = new(512);
    private readonly HashSet<EntityUid> _frameSpriteEntities = new(256);
    private readonly List<Box2> _spriteQueryBounds = new(16);
    private readonly List<ScpGeometrySnapshotEvictionCandidate> _geometrySnapshotEvictionCandidates = new(32);
    private readonly List<ScpCasterLayerGeometrySnapshot> _casterLayerGeometrySnapshots = new(8);
    private static readonly Color InsideMaskColor = new(1f, 0f, 0f, 1f);
    private static readonly Color OutsideMaskColor = new(0f, 1f, 0f, 1f);
    private static readonly Color BothMaskColor = new(1f, 1f, 0f, 1f);
    private static readonly Color OccluderMaskColor = new(0f, 0f, 1f, 1f);

    private readonly Dictionary<EntityUid, Vector2> _foregroundProjectionPositions = new(32);
    private readonly Vector2[] _boxContour = new Vector2[4];
    private float _maximumCasterHalfWidth;
    private float _maximumOccluderHalfWidth;
    private bool _occluderSelectionHeapReady;

    // These accessors intentionally have no overlay-owned backing state. The
    // validation job uses them after UpdateGeometrySnapshots in the same pass,
    // while the actual epochs remain isolated in the current viewport cache.
    private uint _casterSnapshotEpoch => _currentResources!.GeometrySnapshots.CasterEpoch;
    private uint _occluderSnapshotEpoch => _currentResources!.GeometrySnapshots.OccluderEpoch;

    private void UpdateGeometrySnapshots()
    {
        var snapshots = _currentResources!.GeometrySnapshots;
        var casterChanged = snapshots.CasterSnapshotSources.Update(
            CollectionsMarshal.AsSpan(_frameCasterSourceStamps));
        if (casterChanged)
            RefreshCasterEntitySnapshots(snapshots);

        var occluderChanged = snapshots.OccluderSnapshotOccluders.Update(CollectionsMarshal.AsSpan(_frameOccluders));
        occluderChanged |= snapshots.OccluderSnapshotVertices.Update(CollectionsMarshal.AsSpan(_frameOccluderVertices));
        if (occluderChanged)
            RefreshOccluderEntitySnapshots(snapshots);

        var shadowLightCount = 0;
        for (var index = 0; index < _lights.Count; index++)
        {
            if (_lights[index].CastShadows)
                shadowLightCount++;
        }

        snapshots.ValidateAllCasterDependencies = casterChanged &&
            (long) snapshots.CasterSourceChanges.Count * shadowLightCount > MaximumSpatialChangeChecks;
        snapshots.ValidateAllOccluderDependencies = occluderChanged &&
            (long) snapshots.OccluderSourceChanges.Count * shadowLightCount > MaximumSpatialChangeChecks;

    }

    private void RefreshCasterEntitySnapshots(GeometrySnapshotState snapshots)
    {
        snapshots.CasterEpoch = unchecked(snapshots.CasterEpoch + 1);
        if (snapshots.CasterEpoch == 0)
        {
            snapshots.ClearCasterSnapshots();
            snapshots.CasterEpoch = 1;
        }

        snapshots.FrameCasterDependencies.Clear();
        snapshots.CasterSourceChanges.Clear();
        var contours = CollectionsMarshal.AsSpan(_frameContours);
        var vertices = CollectionsMarshal.AsSpan(_frameContourVertices);

        for (var i = 0; i < _frameCasters.Count; i++)
        {
            var caster = _frameCasters[i];
            var key = new ScpGeometryEntityKey(caster.Owner, caster.NetIdentity);
            var currentState = GetOrCreateCasterEntitySnapshot(snapshots, key);

            var entityContours = contours.Slice(caster.ContourStart, caster.ContourCount);
            var vertexStart = entityContours.Length == 0 ? 0 : entityContours[0].VertexStart;
            var vertexEnd = entityContours.Length == 0
                ? 0
                : entityContours[^1].VertexStart + entityContours[^1].VertexCount;
            var entityVertices = vertices.Slice(vertexStart, vertexEnd - vertexStart);
            var header = new CasterEntitySnapshotHeader(caster.Bounds, caster.FovVisibility);
            var previousBounds = currentState.Bounds;
            var hadPrevious = currentState.Residency.MarkSeen(snapshots.CasterEpoch);
            currentState.LastVisibleFrame = snapshots.FrameStamp;
            var previousBytes = currentState.EstimatedBytes;
            var changed = currentState.Exact.Update(in header, entityContours, entityVertices);
            snapshots.AccountCasterResize(currentState, previousBytes);

            currentState.Bounds = caster.Bounds;
            snapshots.FrameCasterDependencies.Add(new ScpGeometryDependency(
                currentState.Identity,
                currentState.Exact.Revision));

            if (!hadPrevious || changed)
            {
                snapshots.CasterSourceChanges.Add(new ScpGeometrySourceChange(
                    caster.Owner,
                    hadPrevious,
                    previousBounds,
                    true,
                    caster.Bounds));
            }
        }

        RemoveStaleCasterSnapshots(snapshots);
    }

    private static CasterEntitySnapshot GetOrCreateCasterEntitySnapshot(
        GeometrySnapshotState snapshots,
        ScpGeometryEntityKey key)
    {
        if (snapshots.CasterEntitySnapshots.TryGetValue(key, out var state))
            return state;

        state = new CasterEntitySnapshot(
            new ScpGeometrySourceIdentity(
                key.Owner,
                key.NetIdentity,
                snapshots.AllocateGeometryGeneration()));
        snapshots.AddCasterSnapshot(key, state);
        return state;
    }

    private void RefreshOccluderEntitySnapshots(GeometrySnapshotState snapshots)
    {
        snapshots.OccluderEpoch = unchecked(snapshots.OccluderEpoch + 1);
        if (snapshots.OccluderEpoch == 0)
        {
            snapshots.ClearOccluderSnapshots();
            snapshots.OccluderEpoch = 1;
        }

        snapshots.FrameOccluderDependencies.Clear();
        snapshots.OccluderSourceChanges.Clear();
        var vertices = CollectionsMarshal.AsSpan(_frameOccluderVertices);

        for (var i = 0; i < _frameOccluders.Count; i++)
        {
            var occluder = _frameOccluders[i];
            var key = new ScpGeometryEntityKey(occluder.Owner, occluder.NetIdentity);
            var isNew = !snapshots.OccluderEntitySnapshots.TryGetValue(key, out var state);
            if (isNew)
            {
                state = new OccluderEntitySnapshot(
                    new ScpGeometrySourceIdentity(
                        occluder.Owner,
                        occluder.NetIdentity,
                        snapshots.AllocateGeometryGeneration()));
                snapshots.AddOccluderSnapshot(key, state);
            }
            var currentState = state!;

            var entityVertices = vertices.Slice(occluder.VertexStart, 4);
            var header = new OccluderEntitySnapshotHeader(occluder.Bounds, occluder.Winding);
            var previousBounds = currentState.Bounds;
            var hadPrevious = currentState.Residency.MarkSeen(snapshots.OccluderEpoch);
            currentState.LastVisibleFrame = snapshots.FrameStamp;
            var previousBytes = currentState.EstimatedBytes;
            var changed = currentState.Exact.Update(
                in header,
                entityVertices,
                ReadOnlySpan<Vector2>.Empty);
            snapshots.AccountOccluderResize(currentState, previousBytes);

            currentState.Bounds = occluder.Bounds;
            snapshots.FrameOccluderDependencies.Add(new ScpGeometryDependency(
                currentState.Identity,
                currentState.Exact.Revision));

            if (!hadPrevious || changed)
            {
                snapshots.OccluderSourceChanges.Add(new ScpGeometrySourceChange(
                    occluder.Owner,
                    hadPrevious,
                    previousBounds,
                    true,
                    occluder.Bounds));
            }
        }

        RemoveStaleOccluderSnapshots(snapshots);
    }

    private void RemoveStaleCasterSnapshots(GeometrySnapshotState snapshots)
    {
        _geometrySnapshotEvictionCandidates.Clear();
        foreach (var (key, state) in snapshots.CasterEntitySnapshots)
        {
            if (state.Residency.WasSeen(snapshots.CasterEpoch))
                continue;

            if (state.Residency.MarkMissing())
            {
                state.LastVisibleFrame = snapshots.FrameStamp;
                snapshots.CasterSourceChanges.Add(new ScpGeometrySourceChange(
                    key.Owner,
                    true,
                    state.Bounds,
                    false,
                    default));
            }

            _geometrySnapshotEvictionCandidates.Add(new ScpGeometrySnapshotEvictionCandidate(
                key,
                state.Residency.LastSeenEpoch,
                state.DeletePending));
        }

        var removalCount = ScpGeometrySnapshotRetention.SortAndGetRemovalCount(
            _geometrySnapshotEvictionCandidates,
            snapshots.CasterEpoch);
        for (var i = 0; i < removalCount; i++)
            snapshots.RemoveCasterSnapshot(_geometrySnapshotEvictionCandidates[i].Key);
    }

    private void RemoveStaleOccluderSnapshots(GeometrySnapshotState snapshots)
    {
        _geometrySnapshotEvictionCandidates.Clear();
        foreach (var (key, state) in snapshots.OccluderEntitySnapshots)
        {
            if (state.Residency.WasSeen(snapshots.OccluderEpoch))
                continue;

            if (state.Residency.MarkMissing())
            {
                state.LastVisibleFrame = snapshots.FrameStamp;
                snapshots.OccluderSourceChanges.Add(new ScpGeometrySourceChange(
                    key.Owner,
                    true,
                    state.Bounds,
                    false,
                    default));
            }

            _geometrySnapshotEvictionCandidates.Add(new ScpGeometrySnapshotEvictionCandidate(
                key,
                state.Residency.LastSeenEpoch,
                state.DeletePending));
        }

        var removalCount = ScpGeometrySnapshotRetention.SortAndGetRemovalCount(
            _geometrySnapshotEvictionCandidates,
            snapshots.OccluderEpoch);
        for (var i = 0; i < removalCount; i++)
            snapshots.RemoveOccluderSnapshot(_geometrySnapshotEvictionCandidates[i].Key);
    }

    private Box2 GetFrameOccluderQueryBounds(Box2 worldAabb)
    {
        var result = worldAabb;

        // Match Clyde's occluder selection bounds: the viewport plus the centres of
        // every selected light. Light radii must not expand this query because that
        // would change which occluders survive the global cap.
        for (var i = 0; i < _lights.Count; i++)
            result = result.ExtendToContain(_lights[i].Position);

        return result;
    }

    private void ClearFrameOccluderCache()
    {
        _frameOccluders.Clear();
        _frameOccluderVertices.Clear();
        _frameOccluderCentersX.Clear();
        _occluderSelectionScratch.Clear();
        _maximumOccluderHalfWidth = 0f;
        _occluderSelectionHeapReady = false;
    }

    private void BuildFrameOccluderCache(MapId mapId, Box2 queryBounds)
    {
        ClearFrameOccluderCache();

        // Clyde flushes the occluder tree in UpdateOcclusionGeometry immediately
        // before BeforeLighting overlays for every viewport. No game-state or
        // frame-system update can run between that query and this pass.

        var maximumOccluders = _system.MaxOccluders;
        if (maximumOccluders <= 0)
            return;

        var priorityOrigin = queryBounds.Center;

        FindIntersectingTreeGrids(mapId, queryBounds);
        for (var i = 0; i < _intersectingTreeGrids.Count; i++)
        {
            var treeUid = _intersectingTreeGrids[i].Owner;
            if (!_entityManager.TryGetComponent(treeUid, out OccluderTreeComponent? tree))
                continue;

            var (treePosition, treeRotation) = _transformSystem.GetWorldPositionRotation(treeUid);
            var localBounds = Matrix3Helpers.CreateInverseTransform(treePosition, treeRotation)
                .TransformBox(queryBounds);
            var state = new OccluderQueryState(
                this,
                treeUid,
                treePosition,
                treeRotation,
                priorityOrigin,
                maximumOccluders);
            tree.Tree.QueryAabb(ref state, QueryOccluder, localBounds);
        }

        if (_mapSystem.TryGetMap(mapId, out var mapUid) &&
            _entityManager.TryGetComponent(mapUid.Value, out OccluderTreeComponent? mapTree))
        {
            var (treePosition, treeRotation) = _transformSystem.GetWorldPositionRotation(mapUid.Value);
            var localBounds = Matrix3Helpers.CreateInverseTransform(treePosition, treeRotation)
                .TransformBox(queryBounds);
            var state = new OccluderQueryState(
                this,
                mapUid.Value,
                treePosition,
                treeRotation,
                priorityOrigin,
                maximumOccluders);
            mapTree.Tree.QueryAabb(ref state, QueryOccluder, localBounds);
        }

        MaterializeSelectedOccluders();
        FinalizeFrameOccluderCache();
    }

    private static bool QueryOccluder(
        ref OccluderQueryState state,
        in ComponentTreeEntry<OccluderComponent> entry)
    {
        var overlay = state.Overlay;
        var occluder = entry.Component;
        if (!occluder.Enabled)
            return true;

        var box = occluder.BoundingBox;
        Box2 worldBounds;

        if (!TryTransformDirectAxisAlignedOccluder(
                entry.Transform.ParentUid,
                state.TreeUid,
                entry.Transform.LocalPosition,
                entry.Transform.LocalRotation,
                box,
                state.TreePosition,
                state.TreeRotation,
                state.TreeMatrix,
                overlay._boxContour,
                out worldBounds))
        {
            var (worldPosition, worldRotation) = overlay.GetEntryWorldPositionRotation(
                entry.Transform,
                state.TreeUid,
                state.TreePosition,
                state.TreeRotation);

            if (worldRotation == Angle.Zero)
            {
                overlay._boxContour[0] = box.BottomLeft + worldPosition;
                overlay._boxContour[1] = box.BottomRight + worldPosition;
                overlay._boxContour[2] = box.TopRight + worldPosition;
                overlay._boxContour[3] = box.TopLeft + worldPosition;
                worldBounds = box.Translated(worldPosition);
            }
            else
            {
                var worldMatrix = Matrix3Helpers.CreateTransform(worldPosition, worldRotation);
                TransformBounds(box, worldMatrix, overlay._boxContour);
                worldBounds = GetQuadBounds(overlay._boxContour);
            }
        }

        var candidate = new OccluderSelectionCandidate(
            entry.Uid,
            overlay._entityManager.GetNetEntity(entry.Uid),
            worldBounds,
            GetDistanceSquared(state.PriorityOrigin, worldBounds),
            1f,
            overlay._boxContour[0],
            overlay._boxContour[1],
            overlay._boxContour[2],
            overlay._boxContour[3]);
        overlay.TrySelectOccluder(in candidate, state.MaximumOccluders);

        // The tree traversal order is not stable across PVS changes. Keep walking
        // after reaching the cap so the same top-K set wins independently of it.
        return true;
    }

    private void TrySelectOccluder(
        in OccluderSelectionCandidate candidate,
        int maximumOccluders)
    {
        if (_occluderSelectionScratch.Count < maximumOccluders)
        {
            _occluderSelectionScratch.Add(candidate);
            return;
        }

        if (!_occluderSelectionHeapReady)
        {
            BuildOccluderSelectionHeap();
            _occluderSelectionHeapReady = true;
        }

        var candidates = CollectionsMarshal.AsSpan(_occluderSelectionScratch);
        if (CompareOccluderSelectionPriority(in candidate, in candidates[0]) >= 0)
            return;

        candidates[0] = candidate;
        SiftOccluderSelectionDown(0);
    }

    private void BuildOccluderSelectionHeap()
    {
        for (var index = _occluderSelectionScratch.Count / 2 - 1; index >= 0; index--)
            SiftOccluderSelectionDown(index);
    }

    private void SiftOccluderSelectionDown(int index)
    {
        var candidates = CollectionsMarshal.AsSpan(_occluderSelectionScratch);
        while (true)
        {
            var left = (index << 1) + 1;
            if (left >= candidates.Length)
                return;

            var right = left + 1;
            var worseChild = right < candidates.Length &&
                CompareOccluderSelectionPriority(in candidates[right], in candidates[left]) > 0
                ? right
                : left;
            if (CompareOccluderSelectionPriority(
                    in candidates[index],
                    in candidates[worseChild]) >= 0)
            {
                return;
            }

            (candidates[index], candidates[worseChild]) = (candidates[worseChild], candidates[index]);
            index = worseChild;
        }
    }

    private void MaterializeSelectedOccluders()
    {
        _occluderSelectionScratch.Sort(static (left, right) => CompareOccluderForIndex(in left, in right));

        for (var i = 0; i < _occluderSelectionScratch.Count; i++)
        {
            var candidate = _occluderSelectionScratch[i];
            var vertexStart = _frameOccluderVertices.Count;
            _frameOccluderVertices.Add(candidate.Vertex0);
            _frameOccluderVertices.Add(candidate.Vertex1);
            _frameOccluderVertices.Add(candidate.Vertex2);
            _frameOccluderVertices.Add(candidate.Vertex3);
            _frameOccluders.Add(new CachedOccluder(
                candidate.Owner,
                candidate.NetIdentity,
                vertexStart,
                candidate.Bounds,
                candidate.Winding));
        }
    }

    private static int CompareOccluderSelectionPriority(
        in OccluderSelectionCandidate left,
        in OccluderSelectionCandidate right)
    {
        var comparison = left.PriorityDistanceSquared.CompareTo(right.PriorityDistanceSquared);
        if (comparison != 0)
            return comparison;

        // NetEntity survives a PVS leave/re-entry, while Owner disambiguates
        // client-side entities and duplicate invalid network identities.
        comparison = left.NetIdentity.Id.CompareTo(right.NetIdentity.Id);
        if (comparison != 0)
            return comparison;

        comparison = left.Owner.CompareTo(right.Owner);
        return comparison != 0 ? comparison : CompareBounds(left.Bounds, right.Bounds);
    }

    private static float GetDistanceSquared(Vector2 point, Box2 bounds)
    {
        var deltaX = point.X < bounds.Left
            ? bounds.Left - point.X
            : point.X > bounds.Right
                ? point.X - bounds.Right
                : 0f;
        var deltaY = point.Y < bounds.Bottom
            ? bounds.Bottom - point.Y
            : point.Y > bounds.Top
                ? point.Y - bounds.Top
                : 0f;
        return deltaX * deltaX + deltaY * deltaY;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryTransformDirectAxisAlignedOccluder(
        EntityUid parentUid,
        EntityUid treeUid,
        Vector2 localPosition,
        Angle localRotation,
        Box2 localBounds,
        Vector2 treePosition,
        Angle treeRotation,
        Matrix3x2 treeMatrix,
        Span<Vector2> result,
        out Box2 worldBounds)
    {
        // This is an exact per-query transform, not a static geometry cache. A PVS
        // arrival or either transform moving is reflected by the live arguments.
        if (parentUid != treeUid || localRotation != Angle.Zero)
        {
            worldBounds = default;
            return false;
        }

        if (treeRotation == Angle.Zero)
        {
            var worldPosition = localPosition + treePosition;
            result[0] = localBounds.BottomLeft + worldPosition;
            result[1] = localBounds.BottomRight + worldPosition;
            result[2] = localBounds.TopRight + worldPosition;
            result[3] = localBounds.TopLeft + worldPosition;
            worldBounds = localBounds.Translated(worldPosition);
            return true;
        }

        TransformBounds(localBounds.Translated(localPosition), treeMatrix, result);
        worldBounds = GetQuadBounds(result);
        return true;
    }

    private static Box2 GetQuadBounds(ReadOnlySpan<Vector2> vertices)
    {
        var minimum = Vector2.Min(
            Vector2.Min(vertices[0], vertices[1]),
            Vector2.Min(vertices[2], vertices[3]));
        var maximum = Vector2.Max(
            Vector2.Max(vertices[0], vertices[1]),
            Vector2.Max(vertices[2], vertices[3]));
        return new Box2(minimum, maximum);
    }

    private void BuildFrameCache(
        MapId mapId,
        Box2 viewportBounds,
        CachedResources resources,
        GeometrySnapshotState geometrySnapshots)
    {
        var mapPaused = _mapSystem.IsPaused(mapId);
        var activeSpriteCount = _system.ActiveShadowCasterEntities.Count +
                                _system.ActiveShadowForegroundEntities.Count;
        if (!mapPaused)
        {
            if (resources.UseSpriteTreeForActiveSet)
                resources.UseSpriteTreeForActiveSet = activeSpriteCount >= ResumeDirectActiveSpriteQueries;
            else
                resources.UseSpriteTreeForActiveSet = activeSpriteCount > MaximumDirectActiveSpriteQueries;
        }

        var useSpriteTree = mapPaused || resources.UseSpriteTreeForActiveSet;

        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.SpriteQuerySetup")
                   : (Robust.Shared.Profiling.ProfManager.GroupGuard?) null)
        {
            ClearFrameSpriteCache();
            if (useSpriteTree)
            {
                // Paused mapping/test maps deliberately keep their entities out
                // of the active PVS index. Very large active sets also retain the
                // spatial query so sparse viewports do not scan the whole map.
                _spriteTree.UpdateTreePositions();
                AddSpriteQueryBounds(viewportBounds);

                for (var i = 0; i < _lights.Count; i++)
                {
                    var light = _lights[i];
                    var radius = new Vector2(light.Radius);
                    AddSpriteQueryBounds(new Box2(light.Position - radius, light.Position + radius));
                }
            }
        }

        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.SpriteQuery")
                   : (Robust.Shared.Profiling.ProfManager.GroupGuard?) null)
        {
            if (useSpriteTree)
            {
                for (var i = 0; i < _spriteQueryBounds.Count; i++)
                {
                    QuerySpriteBounds(
                        mapId,
                        _spriteQueryBounds[i],
                        viewportBounds,
                        mapPaused,
                        geometrySnapshots);
                }
            }
            else
            {
                QueryActiveSpriteIndex(mapId, viewportBounds, geometrySnapshots);
            }
        }

        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.SpriteFinalize")
                   : (Robust.Shared.Profiling.ProfManager.GroupGuard?) null)
        {
            FinalizeFrameCasterCache();
        }
    }

    private void QueryActiveSpriteIndex(
        MapId mapId,
        Box2 viewportBounds,
        GeometrySnapshotState geometrySnapshots)
    {
        foreach (var uid in _system.ActiveShadowCasterEntities)
            CacheActiveSprite(uid, mapId, viewportBounds, geometrySnapshots);

        foreach (var uid in _system.ActiveShadowForegroundEntities)
            CacheActiveSprite(uid, mapId, viewportBounds, geometrySnapshots);
    }

    private void CacheActiveSprite(
        EntityUid uid,
        MapId mapId,
        Box2 viewportBounds,
        GeometrySnapshotState geometrySnapshots)
    {
        if (!_metadataQuery.TryGetComponent(uid, out var metadata) ||
            metadata.EntityPaused ||
            (metadata.Flags & MetaDataFlags.Detached) != 0 ||
            !_spriteQuery.TryGetComponent(uid, out var sprite) ||
            !_transformQuery.TryGetComponent(uid, out var transform) ||
            !sprite.AddToTree ||
            sprite.Color.A == 0f ||
            transform.MapID != mapId)
        {
            return;
        }

        var isForeground = _system.ActiveShadowForegroundEntities.Contains(uid) &&
                           _foregroundQuery.HasComp(uid);
        ScpShadowCasterVisualsComponent? shadow = null;
        var isCaster = _system.ActiveShadowCasterEntities.Contains(uid) &&
                       _shadowQuery.TryGetComponent(uid, out shadow);
        var quality = ScpShadowQuality.Disabled;
        if (isCaster)
        {
            quality = shadow!.Kind == ScpShadowCasterKind.Mob
                ? _system.MobQuality
                : _system.ObjectQuality;
            isCaster = quality != ScpShadowQuality.Disabled &&
                (!_occluderQuery.TryGetComponent(uid, out var occluder) || !occluder.Enabled);
        }

        if ((!isCaster && !isForeground) || !_frameSpriteEntities.Add(uid))
            return;

        // Passing the entity itself as the tree owner deliberately selects the
        // exact world-transform path. Active PVS sets are tiny in practice and
        // this avoids maintaining a second parent/grid grouping cache.
        CacheSprite(
            (uid, sprite, transform),
            isCaster ? shadow : null,
            quality,
            isForeground,
            viewportBounds,
            uid,
            default,
            default,
            geometrySnapshots);
    }

    private void ClearFrameSpriteCache()
    {
        _frameCasters.Clear();
        _frameCasterSourceStamps.Clear();
        _frameContours.Clear();
        _frameContourVertices.Clear();
        _frameCasterCentersX.Clear();
        _protectedSpriteLayers.Clear();
        _frameSpriteEntities.Clear();
        _spriteQueryBounds.Clear();
        _foregroundProjectionPositions.Clear();
        _maximumCasterHalfWidth = 0f;
    }

    private void FinalizeFrameCasterCache()
    {
        _frameCasters.Sort(static (left, right) =>
        {
            var comparison = CompareBounds(left.Bounds, right.Bounds);
            return comparison != 0 ? comparison : left.Owner.CompareTo(right.Owner);
        });
        _protectedSpriteLayers.Sort(static (left, right) =>
            left.StableKey.CompareTo(right.StableKey));
        _frameCasterSourceStamps.Sort(static (left, right) =>
        {
            var comparison = left.Owner.CompareTo(right.Owner);
            return comparison != 0
                ? comparison
                : left.NetIdentity.Id.CompareTo(right.NetIdentity.Id);
        });
        _frameCasterCentersX.Clear();
        _maximumCasterHalfWidth = 0f;

        for (var i = 0; i < _frameCasters.Count; i++)
        {
            var bounds = _frameCasters[i].Bounds;
            _frameCasterCentersX.Add(bounds.Center.X);
            _maximumCasterHalfWidth = MathF.Max(
                _maximumCasterHalfWidth,
                (bounds.Right - bounds.Left) * 0.5f);
        }
    }

    private void FinalizeFrameOccluderCache()
    {
        // MaterializeSelectedOccluders already emits this list in the exact
        // CompareOccluderForIndex order. Sorting it again made dense-occluder
        // scenes pay a second O(K log K) cost every shadow frame.
        _frameOccluderCentersX.Clear();
        _maximumOccluderHalfWidth = 0f;

        for (var i = 0; i < _frameOccluders.Count; i++)
        {
            var bounds = _frameOccluders[i].Bounds;
            _frameOccluderCentersX.Add(bounds.Center.X);
            _maximumOccluderHalfWidth = MathF.Max(
                _maximumOccluderHalfWidth,
                (bounds.Right - bounds.Left) * 0.5f);
        }
    }

    private static int CompareOccluderForIndex(in CachedOccluder left, in CachedOccluder right)
    {
        var comparison = CompareBounds(left.Bounds, right.Bounds);
        if (comparison != 0)
            return comparison;

        comparison = left.NetIdentity.Id.CompareTo(right.NetIdentity.Id);
        return comparison != 0 ? comparison : left.Owner.CompareTo(right.Owner);
    }

    private static int CompareOccluderForIndex(
        in OccluderSelectionCandidate left,
        in OccluderSelectionCandidate right)
    {
        var comparison = CompareBounds(left.Bounds, right.Bounds);
        if (comparison != 0)
            return comparison;

        comparison = left.NetIdentity.Id.CompareTo(right.NetIdentity.Id);
        return comparison != 0 ? comparison : left.Owner.CompareTo(right.Owner);
    }

    private static int CompareBounds(Box2 left, Box2 right)
    {
        var comparison = left.Center.X.CompareTo(right.Center.X);
        if (comparison != 0)
            return comparison;

        comparison = left.Center.Y.CompareTo(right.Center.Y);
        if (comparison != 0)
            return comparison;

        comparison = left.Left.CompareTo(right.Left);
        if (comparison != 0)
            return comparison;

        return left.Bottom.CompareTo(right.Bottom);
    }

    private void FindIntersectingTreeGrids(MapId mapId, Box2 worldBounds)
    {
        _intersectingTreeGrids.Clear();
        _mapSystem.FindGridsIntersecting(
            mapId,
            worldBounds,
            ref _intersectingTreeGrids,
            approx: true,
            includeMap: false);
    }

    private ScpAxisCandidateRange GetCasterCandidateRange(in ScpShadowLightData light)
    {
        return ScpLightingBatchPlanner.GetAxisCandidateRange(
            CollectionsMarshal.AsSpan(_frameCasterCentersX),
            light.Position.X,
            light.Radius,
            _maximumCasterHalfWidth);
    }

    private ScpAxisCandidateRange GetOccluderCandidateRange(in ScpShadowLightData light)
    {
        return ScpLightingBatchPlanner.GetAxisCandidateRange(
            CollectionsMarshal.AsSpan(_frameOccluderCentersX),
            light.Position.X,
            light.Radius,
            _maximumOccluderHalfWidth);
    }

    private bool CasterChangesMayAffectLight(in ScpShadowLightData light)
    {
        var snapshots = _currentResources!.GeometrySnapshots;
        if (snapshots.ValidateAllCasterDependencies)
            return true;

        for (var i = 0; i < snapshots.CasterSourceChanges.Count; i++)
        {
            var change = snapshots.CasterSourceChanges[i];
            if (change.Owner == light.Owner)
                continue;

            if (change.Intersects(light.Position, light.Radius))
                return true;
        }

        return false;
    }

    private bool OccluderChangesMayAffectLight(in ScpShadowLightData light)
    {
        var snapshots = _currentResources!.GeometrySnapshots;
        if (snapshots.ValidateAllOccluderDependencies)
            return true;

        for (var i = 0; i < snapshots.OccluderSourceChanges.Count; i++)
        {
            if (snapshots.OccluderSourceChanges[i].Intersects(light.Position, light.Radius))
                return true;
        }

        return false;
    }

    private ScpAxisCandidateRange GatherCasterDependencies(
        in ScpShadowLightData light,
        bool buildOutsideMask,
        List<ScpGeometryDependency> dependencies,
        out long estimatedGeometryVertices)
    {
        dependencies.Clear();
        estimatedGeometryVertices = 0;
        var frameDependencies = _currentResources!.GeometrySnapshots.FrameCasterDependencies;
        var candidates = GetCasterCandidateRange(light);
        var lightCircle = new Circle(light.Position, light.Radius);

        for (var i = candidates.Start; i < candidates.End; i++)
        {
            var caster = _frameCasters[i];
            if (caster.Owner == light.Owner || !lightCircle.Intersects(caster.Bounds))
                continue;

            var renderInside = (caster.FovVisibility & DirectionalFovVisibility.Inside) != 0;
            var renderOutside = buildOutsideMask &&
                (caster.FovVisibility & DirectionalFovVisibility.Outside) != 0;
            if (!renderInside && !renderOutside)
                continue;

            dependencies.Add(frameDependencies[i]);
            for (var contourIndex = 0; contourIndex < caster.ContourCount; contourIndex++)
            {
                estimatedGeometryVertices +=
                    _frameContours[caster.ContourStart + contourIndex].VertexCount;
            }
        }

        return candidates;
    }

    private ScpAxisCandidateRange GatherOccluderDependencies(
        in ScpShadowLightData light,
        List<ScpGeometryDependency> dependencies,
        out long estimatedGeometryVertices)
    {
        dependencies.Clear();
        estimatedGeometryVertices = 0;
        var frameDependencies = _currentResources!.GeometrySnapshots.FrameOccluderDependencies;
        var candidates = GetOccluderCandidateRange(light);
        var lightCircle = new Circle(light.Position, light.Radius);

        for (var i = candidates.Start; i < candidates.End; i++)
        {
            if (!lightCircle.Intersects(_frameOccluders[i].Bounds))
                continue;

            dependencies.Add(frameDependencies[i]);
            estimatedGeometryVertices += 4;
        }

        return candidates;
    }

    private void AddSpriteQueryBounds(Box2 bounds)
    {
        for (var i = 0; i < _spriteQueryBounds.Count; i++)
        {
            var current = _spriteQueryBounds[i];
            if (current.Contains(bounds))
                return;

            if (!current.Intersects(bounds))
                continue;

            bounds = current.Union(bounds);
            _spriteQueryBounds.RemoveAt(i);
            i = -1;
        }

        _spriteQueryBounds.Add(bounds);
    }

    private void QuerySpriteBounds(
        MapId mapId,
        Box2 queryBounds,
        Box2 viewportBounds,
        bool allowInactive,
        GeometrySnapshotState geometrySnapshots)
    {
        FindIntersectingTreeGrids(mapId, queryBounds);
        for (var i = 0; i < _intersectingTreeGrids.Count; i++)
        {
            var treeUid = _intersectingTreeGrids[i].Owner;
            if (!_entityManager.TryGetComponent(treeUid, out SpriteTreeComponent? tree))
                continue;

            QuerySpriteTree(
                treeUid,
                tree,
                mapId,
                queryBounds,
                viewportBounds,
                allowInactive,
                geometrySnapshots);
        }

        if (_mapSystem.TryGetMap(mapId, out var mapUid) &&
            _entityManager.TryGetComponent(mapUid.Value, out SpriteTreeComponent? mapTree))
        {
            QuerySpriteTree(
                mapUid.Value,
                mapTree,
                mapId,
                queryBounds,
                viewportBounds,
                allowInactive,
                geometrySnapshots);
        }
    }

    private void QuerySpriteTree(
        EntityUid treeUid,
        SpriteTreeComponent tree,
        MapId mapId,
        Box2 queryBounds,
        Box2 viewportBounds,
        bool allowInactive,
        GeometrySnapshotState geometrySnapshots)
    {
        var (treePosition, treeRotation) = _transformSystem.GetWorldPositionRotation(treeUid);
        var localBounds = ScpSparseSpriteQuery.ToTreeLocalBounds(
            queryBounds,
            treePosition,
            treeRotation);
        var state = new SpriteQueryState(
            this,
            mapId,
            viewportBounds,
            treeUid,
            treePosition,
            treeRotation,
            allowInactive,
            geometrySnapshots);
        tree.Tree.QueryAabb(ref state, QuerySprite, localBounds, true);
    }

    private static bool QuerySprite(
        ref SpriteQueryState state,
        in ComponentTreeEntry<SpriteComponent> entry)
    {
        var overlay = state.Overlay;
        var sprite = entry.Component;
        if (!sprite.AddToTree || sprite.Color.A == 0f ||
            entry.Transform.MapID != state.MapId)
        {
            return true;
        }

        var isForeground = overlay._foregroundQuery.HasComp(entry.Uid);
        var isCaster = overlay._shadowQuery.TryGetComponent(entry.Uid, out var shadow);
        if (!state.AllowInactive)
        {
            isForeground &= overlay._system.ActiveShadowForegroundEntities.Contains(entry.Uid);
            isCaster &= overlay._system.ActiveShadowCasterEntities.Contains(entry.Uid);
        }

        var quality = ScpShadowQuality.Disabled;
        if (isCaster)
        {
            quality = shadow!.Kind == ScpShadowCasterKind.Mob
                ? overlay._system.MobQuality
                : overlay._system.ObjectQuality;
            isCaster = quality != ScpShadowQuality.Disabled &&
                (!overlay._occluderQuery.TryGetComponent(entry.Uid, out var occluder) || !occluder.Enabled);
        }

        if ((!isCaster && !isForeground) || !overlay._frameSpriteEntities.Add(entry.Uid))
            return true;

        overlay.CacheSprite(
            entry,
            isCaster ? shadow : null,
            quality,
            isForeground,
            state.ViewportBounds,
            state.TreeUid,
            state.TreePosition,
            state.TreeRotation,
            state.GeometrySnapshots);
        return true;
    }

    private void CacheSprite(
        Entity<SpriteComponent, TransformComponent> candidate,
        ScpShadowCasterVisualsComponent? shadow,
        ScpShadowQuality quality,
        bool isForeground,
        Box2 viewportBounds,
        EntityUid treeUid,
        Vector2 treePosition,
        Angle treeRotation,
        GeometrySnapshotState geometrySnapshots)
    {
        var sprite = candidate.Comp1;
        var (position, rotation) = GetEntryWorldPositionRotation(
            candidate.Comp2,
            treeUid,
            treePosition,
            treeRotation);
        var matrices = GetSpriteMatrices(sprite, position, rotation);

        _casterLayerGeometrySnapshots.Clear();
        var hasOpaqueBounds = false;
        var opaqueBounds = default(Box2);
        var spriteFovAlpha = 1f;
        var spriteFovAlphaReady = !_directionalFovActive;

        if (sprite.AllLayers is IReadOnlyList<ISpriteLayer> layers)
        {
            var overrideDirection = sprite.EnableDirectionOverride ? sprite.DirectionOverride : (Direction?) null;

            for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                if (layers[layerIndex] is not Layer layer ||
                    !_spriteSystem.IsVisible(layer) ||
                    layer.Blank ||
                    sprite.Color.A * layer.Color.A < SpriteAlphaThreshold)
                {
                    continue;
                }

                var rsi = layer.ActualRsi;
                RSI.State? state = null;
                if (rsi != null)
                    rsi.TryGetState(layer.State, out state);

                var matrixDirection = state == null
                    ? RsiDirection.South
                    : Layer.GetDirection(state.RsiDirections, matrices.ScreenAngle);
                layer.GetLayerDrawMatrix(matrixDirection, out var layerMatrix);

                var drawDirection = matrixDirection;
                if (overrideDirection != null && state != null)
                    drawDirection = overrideDirection.Value.Convert(state.RsiDirections);
                drawDirection = drawDirection.OffsetRsiDir(layer.DirOffset);

                var layerBase = GetLayerBaseMatrix(layer.RenderingStrategy, sprite.GranularLayersRendering, matrices);
                var worldMatrix = Matrix3x2.Multiply(layerMatrix, layerBase);

                var texture = state?.GetFrame(drawDirection, layer.AnimationFrame) ??
                    layer.Texture ??
                    _spriteSystem.GetFallbackTexture();
                var textureSize = texture.Size / (float) EyeManager.PixelsPerMeter;
                var quad = Box2.FromDimensions(textureSize / -2f, textureSize);

                if (shadow != null && quality is not (ScpShadowQuality.Bounds or ScpShadowQuality.Disabled))
                {
                    var sourceKind = ScpCasterContourSourceKind.None;
                    object? source = null;
                    string? sourceState = null;
                    var sourceDirection = (byte) 0;
                    var sourceFrame = 0;

                    if (state != null && rsi != null)
                    {
                        sourceKind = ScpCasterContourSourceKind.Rsi;
                        source = rsi;
                        sourceState = layer.State.Name;
                        sourceDirection = (byte) drawDirection;
                        sourceFrame = layer.AnimationFrame;
                    }
                    else if (layer.Texture != null)
                    {
                        sourceKind = ScpCasterContourSourceKind.Texture;
                        source = layer.Texture;
                    }

                    _casterLayerGeometrySnapshots.Add(new ScpCasterLayerGeometrySnapshot(
                        layerIndex,
                        sourceKind,
                        source,
                        sourceState,
                        sourceDirection,
                        sourceFrame,
                        worldMatrix));
                }

                if (worldMatrix.TransformBox(quad).Intersects(viewportBounds))
                {
                    if (!spriteFovAlphaReady)
                    {
                        spriteFovAlpha = GetSpriteDirectionalFovAlpha(candidate.Owner, candidate.Comp2);
                        if (MathF.Abs(sprite.Color.A - sprite.Color.A * spriteFovAlpha) <= 0.01f)
                            spriteFovAlpha = 1f;
                        spriteFovAlphaReady = true;
                    }

                    var modulate = sprite.Color * layer.Color;
                    modulate = modulate.WithAlpha(modulate.A * spriteFovAlpha);
                    if (modulate.A >= SpriteAlphaThreshold)
                    {
                        _protectedSpriteLayers.Add(new ProtectedSpriteLayer(
                            candidate.Owner,
                            layerIndex,
                            texture,
                            worldMatrix,
                            quad,
                            modulate));
                    }
                }

                if (isForeground)
                {
                    var localOpaqueBounds = default(Box2);
                    var hasLayerBounds = state != null && rsi != null
                        ? _contourCache.TryGetOpaqueBounds(
                            rsi,
                            layer.State,
                            drawDirection,
                            layer.AnimationFrame,
                            out localOpaqueBounds)
                        : layer.Texture != null &&
                        _contourCache.TryGetOpaqueBounds(layer.Texture, out localOpaqueBounds);

                    if (hasLayerBounds)
                    {
                        var worldOpaqueBounds = worldMatrix.TransformBox(localOpaqueBounds);
                        opaqueBounds = hasOpaqueBounds
                            ? opaqueBounds.Union(worldOpaqueBounds)
                            : worldOpaqueBounds;
                        hasOpaqueBounds = true;
                    }
                }
            }
        }

        if (isForeground && hasOpaqueBounds)
            _foregroundProjectionPositions[candidate.Owner] = opaqueBounds.Center;

        if (shadow == null)
            return;

        var netIdentity = _entityManager.GetNetEntity(candidate.Owner);
        var entityState = GetOrCreateCasterEntitySnapshot(
            geometrySnapshots,
            new ScpGeometryEntityKey(candidate.Owner, netIdentity));
        var geometryHeader = new ScpCasterWorldGeometryHeader(
            quality,
            shadow.Bounds,
            matrices.Sprite);
        var layerSnapshots = CollectionsMarshal.AsSpan(_casterLayerGeometrySnapshots);
        if (!entityState.WorldGeometry.Input.IsCurrent(
                in geometryHeader,
                layerSnapshots,
                entityState.WorldGeometry.UsesFallback,
                out var pendingGeometryCommit))
        {
            var previousBytes = entityState.EstimatedBytes;
            try
            {
                RebuildCasterWorldGeometry(
                    entityState.WorldGeometry,
                    in geometryHeader,
                    layerSnapshots);
                entityState.WorldGeometry.Input.Commit(
                    in geometryHeader,
                    layerSnapshots,
                    in pendingGeometryCommit);
            }
            finally
            {
                geometrySnapshots.AccountCasterResize(entityState, previousBytes);
            }
        }

        AppendCasterWorldGeometry(
            candidate.Owner,
            netIdentity,
            candidate.Comp2,
            entityState.WorldGeometry,
            entityState.WorldGeometry.Input.Revision);
    }

    private void RebuildCasterWorldGeometry(
        CasterWorldGeometryCache geometry,
        in ScpCasterWorldGeometryHeader header,
        ReadOnlySpan<ScpCasterLayerGeometrySnapshot> layers)
    {
        geometry.Contours.Clear();
        geometry.Vertices.Clear();
        var hasBounds = false;
        var bounds = default(Box2);

        if (header.Quality != ScpShadowQuality.Bounds)
        {
            for (var layerIndex = 0; layerIndex < layers.Length; layerIndex++)
            {
                var layer = layers[layerIndex];
                var contours = ScpShadowContours.Empty;
                var hasContours = layer.SourceKind switch
                {
                    ScpCasterContourSourceKind.Rsi =>
                        _contourCache.TryGetContours(
                            (RSI) layer.Source!,
                            new RSI.StateId(layer.State),
                            (RsiDirection) layer.Direction,
                            layer.Frame,
                            out contours),
                    ScpCasterContourSourceKind.Texture =>
                        _contourCache.TryGetContours((Texture) layer.Source!, out contours),
                    _ => false,
                };

                if (!hasContours)
                    continue;

                for (var loopIndex = 0; loopIndex < contours.Loops.Length; loopIndex++)
                {
                    var contourBounds = CacheTransformedContour(
                        contours.Loops[loopIndex],
                        layer.WorldMatrix,
                        geometry.Contours,
                        geometry.Vertices);
                    bounds = hasBounds ? bounds.Union(contourBounds) : contourBounds;
                    hasBounds = true;
                }
            }
        }

        if (!hasBounds)
        {
            TransformBounds(header.FallbackBounds, header.FallbackWorldMatrix, _boxContour);
            bounds = CacheContour(_boxContour, geometry.Contours, geometry.Vertices);
        }

        geometry.UsesFallback = !hasBounds;
        geometry.Bounds = bounds;
    }

    private void AppendCasterWorldGeometry(
        EntityUid owner,
        NetEntity netIdentity,
        TransformComponent transform,
        CasterWorldGeometryCache geometry,
        uint geometryRevision)
    {
        var contourStart = _frameContours.Count;
        var vertexOffset = _frameContourVertices.Count;
        _frameContourVertices.EnsureCapacity(vertexOffset + geometry.Vertices.Count);
        for (var i = 0; i < geometry.Vertices.Count; i++)
            _frameContourVertices.Add(geometry.Vertices[i]);

        _frameContours.EnsureCapacity(contourStart + geometry.Contours.Count);
        for (var i = 0; i < geometry.Contours.Count; i++)
        {
            var contour = geometry.Contours[i];
            _frameContours.Add(contour with { VertexStart = contour.VertexStart + vertexOffset });
        }

        var fovVisibility = GetCasterDirectionalFovVisibility(owner, transform);
        _frameCasters.Add(new CachedCaster(
            owner,
            netIdentity,
            contourStart,
            geometry.Contours.Count,
            geometry.Bounds,
            fovVisibility));
        _frameCasterSourceStamps.Add(new CasterFrameSourceStamp(
            owner,
            netIdentity,
            geometryRevision,
            geometry.Bounds,
            fovVisibility));
    }

    private static Box2 CacheContour(
        ReadOnlySpan<Vector2> contour,
        List<CachedContour> contours,
        List<Vector2> vertices)
    {
        var vertexStart = vertices.Count;
        var minimum = new Vector2(float.PositiveInfinity);
        var maximum = new Vector2(float.NegativeInfinity);
        var signedArea = 0f;

        for (var i = 0; i < contour.Length; i++)
        {
            var vertex = contour[i];
            var next = contour[(i + 1) % contour.Length];
            vertices.Add(vertex);
            minimum = Vector2.Min(minimum, vertex);
            maximum = Vector2.Max(maximum, vertex);
            signedArea += vertex.X * next.Y - next.X * vertex.Y;
        }

        var bounds = new Box2(minimum, maximum);
        contours.Add(new CachedContour(
            vertexStart,
            contour.Length,
            signedArea >= 0f ? 1f : -1f,
            bounds));
        return bounds;
    }

    /// <summary>
    /// Transforms and snapshots one local sprite contour in a single pass.
    /// </summary>
    private static Box2 CacheTransformedContour(
        ReadOnlySpan<Vector2> contour,
        in Matrix3x2 worldMatrix,
        List<CachedContour> contours,
        List<Vector2> vertices)
    {
        // Contour extraction normally guarantees a polygon. Keep the old empty
        // input behavior instead of turning malformed resource data into a render
        // exception.
        if (contour.IsEmpty)
            return CacheContour(contour, contours, vertices);

        var vertexStart = vertices.Count;
        var first = Vector2.Transform(contour[0], worldMatrix);
        var previous = first;
        var minimum = first;
        var maximum = first;
        var signedArea = 0f;
        vertices.Add(first);

        for (var i = 1; i < contour.Length; i++)
        {
            var current = Vector2.Transform(contour[i], worldMatrix);
            vertices.Add(current);
            minimum = Vector2.Min(minimum, current);
            maximum = Vector2.Max(maximum, current);
            signedArea += previous.X * current.Y - current.X * previous.Y;
            previous = current;
        }

        signedArea += previous.X * first.Y - first.X * previous.Y;
        var bounds = new Box2(minimum, maximum);
        contours.Add(new CachedContour(
            vertexStart,
            contour.Length,
            signedArea >= 0f ? 1f : -1f,
            bounds));
        return bounds;
    }

    #endregion

    #region Caster mask

    private void BuildCasterMasks(
        in ScpShadowLightData light,
        bool buildOutsideMask,
        LightGeometryBuffer geometry,
        ScpAxisCandidateRange candidates)
    {
        var lightCircle = new Circle(light.Position, light.Radius);
        var projectionPosition = light.ProjectionPosition;

        for (var i = candidates.Start; i < candidates.End; i++)
        {
            var caster = _frameCasters[i];
            if (caster.Owner == light.Owner ||
                !lightCircle.Intersects(caster.Bounds))
            {
                continue;
            }

            var renderInside = (caster.FovVisibility & DirectionalFovVisibility.Inside) != 0;
            var renderOutside = buildOutsideMask &&
                (caster.FovVisibility & DirectionalFovVisibility.Outside) != 0;

            if (!renderInside && !renderOutside)
                continue;

            var maskColor = renderInside
                ? renderOutside ? BothMaskColor : InsideMaskColor
                : OutsideMaskColor;

            for (var contourIndex = 0; contourIndex < caster.ContourCount; contourIndex++)
            {
                var contour = _frameContours[caster.ContourStart + contourIndex];
                if (!lightCircle.Intersects(contour.Bounds))
                    continue;

                var vertices = CollectionsMarshal.AsSpan(_frameContourVertices)
                    .Slice(contour.VertexStart, contour.VertexCount);

                // Only the overlapping sprite layer is geometrically ambiguous. Skipping
                // the whole multi-layer caster makes the body disappear when a hand touches a lamp.
                if (contour.Bounds.Enlarged(NearLightDistance).Contains(projectionPosition) &&
                    ContainsOrNear(vertices, projectionPosition, NearLightDistance))
                {
                    continue;
                }

                if (!AppendShadowVolume(
                    vertices,
                    contour.Winding,
                    projectionPosition,
                    light.Position,
                    light.Radius,
                    geometry.CasterVertices,
                    maskColor))
                {
                    continue;
                }

                geometry.HasInsideMask |= renderInside;
                geometry.HasOutsideMask |= renderOutside;
            }
        }
    }

    #endregion

    #region Stock occluder mask

    private void BuildOccluderMask(
        in ScpShadowLightData light,
        LightGeometryBuffer geometry,
        ScpAxisCandidateRange candidates)
    {
        var lightCircle = new Circle(light.Position, light.Radius);
        for (var i = candidates.Start; i < candidates.End; i++)
        {
            var occluder = _frameOccluders[i];
            if (!lightCircle.Intersects(occluder.Bounds))
                continue;

            var vertices = CollectionsMarshal.AsSpan(_frameOccluderVertices)
                .Slice(occluder.VertexStart, 4);
            geometry.HasOccluderMask |= AppendFilledContour(
                vertices,
                geometry.OccluderVertices,
                OccluderMaskColor);

            var projectionOrigin = occluder.Bounds.Enlarged(NearLightDistance).Contains(light.Position)
                ? GetSafeOccluderProjectionOrigin(
                    vertices,
                    occluder.Winding,
                    light.Position,
                    NearLightDistance)
                : light.Position;
            geometry.HasOccluderMask |= AppendShadowVolume(
                vertices,
                occluder.Winding,
                projectionOrigin,
                light.Position,
                light.Radius,
                geometry.OccluderVertices,
                OccluderMaskColor);
        }
    }

    #endregion

    #region Sprite transforms

    private SpriteMatrices GetSpriteMatrices(SpriteComponent sprite, Vector2 worldPosition, Angle worldRotation)
    {
        var angle = (worldRotation + _eyeRotation).Reduced().FlipPositive();
        var cardinal = sprite is { NoRotation: false, SnapCardinals: true }
            ? angle.RoundToCardinalAngle()
            : Angle.Zero;

        var entityMatrix = Matrix3Helpers.CreateTransform(
            worldPosition,
            sprite.NoRotation ? -_eyeRotation : worldRotation - cardinal);
        var spriteMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);

        if (!sprite.GranularLayersRendering)
            return new SpriteMatrices(angle, spriteMatrix, spriteMatrix, spriteMatrix, spriteMatrix);

        entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, worldRotation);
        var defaultMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);

        entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, worldRotation - angle.RoundToCardinalAngle());
        var snapMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);

        entityMatrix = Matrix3Helpers.CreateTransform(worldPosition, -_eyeRotation);
        var noRotationMatrix = Matrix3x2.Multiply(sprite.LocalMatrix, entityMatrix);

        return new SpriteMatrices(angle, spriteMatrix, defaultMatrix, snapMatrix, noRotationMatrix);
    }

    private (Vector2 Position, Angle Rotation) GetEntryWorldPositionRotation(
        TransformComponent transform,
        EntityUid treeUid,
        Vector2 treePosition,
        Angle treeRotation)
    {
        if (transform.ParentUid != treeUid)
            return _transformSystem.GetWorldPositionRotation(transform);

        // Component trees already tell us the direct parent. Reusing its world
        // transform avoids walking the same grid/map hierarchy for every entry.
        return ComposeDirectTreeChildTransform(
            transform.LocalPosition,
            transform.LocalRotation,
            treePosition,
            treeRotation);
    }

    private (Vector2 Position, Angle Rotation) GetEntryTreePositionRotation(
        TransformComponent transform,
        EntityUid treeUid)
    {
        if (transform.ParentUid == treeUid)
            return (transform.LocalPosition, transform.LocalRotation);

        return _transformSystem.GetRelativePositionRotation(transform, treeUid);
    }

    internal static (Vector2 Position, Angle Rotation) ComposeDirectTreeChildTransform(
        Vector2 localPosition,
        Angle localRotation,
        Vector2 treePosition,
        Angle treeRotation)
    {
        var position = treeRotation.RotateVec(localPosition) + treePosition;
        return (position, localRotation + treeRotation);
    }

    private static Matrix3x2 GetLayerBaseMatrix(
        LayerRenderingStrategy strategy,
        bool granular,
        in SpriteMatrices matrices)
    {
        if (!granular)
            return matrices.Sprite;

        return strategy switch
        {
            LayerRenderingStrategy.UseSpriteStrategy => matrices.Sprite,
            LayerRenderingStrategy.Default => matrices.Default,
            LayerRenderingStrategy.SnapToCardinals => matrices.SnapToCardinals,
            LayerRenderingStrategy.NoRotation => matrices.NoRotation,
            _ => matrices.Sprite,
        };
    }

    private static void TransformBounds(Box2 bounds, Matrix3x2 matrix, Span<Vector2> result)
    {
        result[0] = Vector2.Transform(bounds.BottomLeft, matrix);
        result[1] = Vector2.Transform(bounds.BottomRight, matrix);
        result[2] = Vector2.Transform(bounds.TopRight, matrix);
        result[3] = Vector2.Transform(bounds.TopLeft, matrix);
    }

    #endregion

    #region Shadow volumes

    private static bool AppendShadowVolume(
        ReadOnlySpan<Vector2> contour,
        float winding,
        Vector2 projectionOrigin,
        Vector2 lightPosition,
        float lightRadius,
        List<DrawVertexUV2DColor> vertices,
        Color color)
    {
        if (contour.Length < 2)
            return false;

        var appended = false;

        for (var i = 0; i < contour.Length; i++)
        {
            var next = (i + 1) % contour.Length;
            var start = contour[i];
            var end = contour[next];
            var edge = end - start;
            var outward = new Vector2(edge.Y, -edge.X) * winding;

            if (Vector2.Dot(projectionOrigin - (start + end) * 0.5f, outward) > 0f)
                continue;

            var farStart = ProjectToLightRadius(start, projectionOrigin, lightPosition, lightRadius);
            var farEnd = ProjectToLightRadius(end, projectionOrigin, lightPosition, lightRadius);
            AppendQuad(start, end, farEnd, farStart, vertices, color);
            appended = true;
        }

        return appended;
    }

    private static Vector2 ProjectToLightRadius(
        Vector2 vertex,
        Vector2 projectionOrigin,
        Vector2 lightPosition,
        float lightRadius)
    {
        var direction = vertex - projectionOrigin;
        var directionLengthSquared = direction.LengthSquared();
        if (directionLengthSquared <= GeometryEpsilon * GeometryEpsilon)
            return vertex;

        if (projectionOrigin == lightPosition)
        {
            var radialAmount = lightRadius / MathF.Sqrt(directionLengthSquared);
            if (!float.IsFinite(radialAmount) || radialAmount <= 1f)
                return vertex;

            return lightPosition + direction * radialAmount;
        }

        var originOffset = projectionOrigin - lightPosition;
        var b = 2f * Vector2.Dot(originOffset, direction);
        var c = originOffset.LengthSquared() - lightRadius * lightRadius;
        var discriminant = b * b - 4f * directionLengthSquared * c;
        if (discriminant <= 0f)
            return vertex;

        var amount = (-b + MathF.Sqrt(discriminant)) / (2f * directionLengthSquared);
        if (!float.IsFinite(amount) || amount <= 1f)
            return vertex;

        return projectionOrigin + direction * amount;
    }

    private static bool AppendFilledContour(
        ReadOnlySpan<Vector2> contour,
        List<DrawVertexUV2DColor> vertices,
        Color color)
    {
        if (contour.Length < 3)
            return false;

        for (var i = 1; i < contour.Length - 1; i++)
        {
            vertices.Add(new DrawVertexUV2DColor(contour[0], color));
            vertices.Add(new DrawVertexUV2DColor(contour[i], color));
            vertices.Add(new DrawVertexUV2DColor(contour[i + 1], color));
        }

        return true;
    }

    private static void AppendQuad(
        Vector2 bottomLeft,
        Vector2 bottomRight,
        Vector2 topRight,
        Vector2 topLeft,
        List<DrawVertexUV2DColor> vertices,
        Color color)
    {
        vertices.Add(new DrawVertexUV2DColor(bottomLeft, color));
        vertices.Add(new DrawVertexUV2DColor(bottomRight, color));
        vertices.Add(new DrawVertexUV2DColor(topRight, color));
        vertices.Add(new DrawVertexUV2DColor(bottomLeft, color));
        vertices.Add(new DrawVertexUV2DColor(topRight, color));
        vertices.Add(new DrawVertexUV2DColor(topLeft, color));
    }

    #endregion

    #region Point and edge checks

    private static bool ContainsOrNear(ReadOnlySpan<Vector2> contour, Vector2 point, float maximumDistance)
    {
        if (contour.Length < 2)
            return false;

        var inside = false;
        var maximumDistanceSquared = maximumDistance * maximumDistance;
        var previous = contour[^1];

        for (var i = 0; i < contour.Length; i++)
        {
            var current = contour[i];
            var edge = current - previous;
            var edgeLengthSquared = edge.LengthSquared();
            if (edgeLengthSquared > GeometryEpsilon * GeometryEpsilon)
            {
                var amount = Math.Clamp(Vector2.Dot(point - previous, edge) / edgeLengthSquared, 0f, 1f);
                var nearest = previous + edge * amount;
                if (Vector2.DistanceSquared(point, nearest) <= maximumDistanceSquared)
                    return true;
            }

            if ((current.Y > point.Y) != (previous.Y > point.Y) &&
                point.X < (previous.X - current.X) * (point.Y - current.Y) /
                (previous.Y - current.Y) + current.X)
            {
                inside = !inside;
            }

            previous = current;
        }

        return inside;
    }

    /// <summary>
    /// Moves a virtual projection origin outside a contour when a light touches or enters it.
    /// The real light position remains unchanged for attenuation and range clipping.
    /// </summary>
    private static Vector2 GetSafeOccluderProjectionOrigin(
        ReadOnlySpan<Vector2> contour,
        float winding,
        Vector2 lightPosition,
        float minimumDistance)
    {
        if (contour.Length < 2)
            return lightPosition;

        var inside = false;
        var nearestDistanceSquared = float.PositiveInfinity;
        var nearestPoint = lightPosition;
        var nearestOutward = Vector2.Zero;
        var previous = contour[^1];

        for (var i = 0; i < contour.Length; i++)
        {
            var current = contour[i];
            var edge = current - previous;
            var edgeLengthSquared = edge.LengthSquared();
            if (edgeLengthSquared > GeometryEpsilon * GeometryEpsilon)
            {
                var amount = Math.Clamp(
                    Vector2.Dot(lightPosition - previous, edge) / edgeLengthSquared,
                    0f,
                    1f);
                var point = previous + edge * amount;
                var distanceSquared = Vector2.DistanceSquared(lightPosition, point);
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestPoint = point;
                    nearestOutward = Vector2.Normalize(new Vector2(edge.Y, -edge.X) * winding);
                }
            }

            if ((current.Y > lightPosition.Y) != (previous.Y > lightPosition.Y) &&
                lightPosition.X < (previous.X - current.X) * (lightPosition.Y - current.Y) /
                (previous.Y - current.Y) + current.X)
            {
                inside = !inside;
            }

            previous = current;
        }

        if (!inside && nearestDistanceSquared > minimumDistance * minimumDistance)
            return lightPosition;

        if (nearestOutward.LengthSquared() <= GeometryEpsilon * GeometryEpsilon)
            return lightPosition;

        var result = nearestPoint + nearestOutward * (minimumDistance + GeometryEpsilon);
        return float.IsFinite(result.X) && float.IsFinite(result.Y) ? result : lightPosition;
    }

    private static float GetWinding(ReadOnlySpan<Vector2> contour)
    {
        var signedArea = 0f;
        for (var i = 0; i < contour.Length; i++)
        {
            var next = (i + 1) % contour.Length;
            signedArea += contour[i].X * contour[next].Y - contour[next].X * contour[i].Y;
        }

        return signedArea >= 0f ? 1f : -1f;
    }

    #endregion

    #region Exact frame snapshot comparers

    private static ulong AddSnapshotInt(ulong hash, int value)
    {
        return ScpExactSnapshotHash.Mix(hash, unchecked((uint) value));
    }

    private static ulong AddSnapshotFloat(ulong hash, float value)
    {
        // Single.GetHashCode canonicalizes signed zero and NaN in the same way as
        // Single.Equals, avoiding harmless but noisy cache misses.
        return ScpExactSnapshotHash.Mix(
            hash,
            unchecked((uint) value.GetHashCode()));
    }

    private static ulong AddSnapshotVector(ulong hash, in Vector2 value)
    {
        hash = AddSnapshotFloat(hash, value.X);
        return AddSnapshotFloat(hash, value.Y);
    }

    private static ulong AddSnapshotBox(ulong hash, in Box2 value)
    {
        hash = AddSnapshotFloat(hash, value.Left);
        hash = AddSnapshotFloat(hash, value.Bottom);
        hash = AddSnapshotFloat(hash, value.Right);
        return AddSnapshotFloat(hash, value.Top);
    }

    private readonly struct Vector2SnapshotComparer : IScpExactSnapshotComparer<Vector2>
    {
        public int ElementSizeBytes => 8;

        public bool AreEqual(in Vector2 left, in Vector2 right)
        {
            return left.X.Equals(right.X) && left.Y.Equals(right.Y);
        }

        public ulong AddToHash(ulong hash, in Vector2 value)
        {
            return AddSnapshotVector(hash, value);
        }
    }

    private readonly struct CasterFrameSourceStampComparer : IScpExactSnapshotComparer<CasterFrameSourceStamp>
    {
        public int ElementSizeBytes => 32;

        public bool AreEqual(in CasterFrameSourceStamp left, in CasterFrameSourceStamp right)
        {
            return left.Owner == right.Owner &&
                left.NetIdentity == right.NetIdentity &&
                left.GeometryRevision == right.GeometryRevision &&
                left.Bounds.Equals(right.Bounds) &&
                left.FovVisibility == right.FovVisibility;
        }

        public ulong AddToHash(ulong hash, in CasterFrameSourceStamp value)
        {
            hash = AddSnapshotInt(hash, value.Owner.Id);
            hash = AddSnapshotInt(hash, value.NetIdentity.Id);
            hash = AddSnapshotInt(hash, unchecked((int) value.GeometryRevision));
            hash = AddSnapshotBox(hash, value.Bounds);
            return AddSnapshotInt(hash, (int) value.FovVisibility);
        }
    }

    private readonly struct CachedContourEntitySnapshotComparer : IScpExactSnapshotComparer<CachedContour>
    {
        public int ElementSizeBytes => 28;

        public bool AreEqual(in CachedContour left, in CachedContour right)
        {
            // Absolute offsets belong to the flattened frame buffer. They may move
            // when an unrelated entity changes and are not source geometry.
            return left.VertexCount == right.VertexCount &&
                left.Winding.Equals(right.Winding) &&
                left.Bounds.Equals(right.Bounds);
        }

        public ulong AddToHash(ulong hash, in CachedContour value)
        {
            hash = AddSnapshotInt(hash, value.VertexCount);
            hash = AddSnapshotFloat(hash, value.Winding);
            return AddSnapshotBox(hash, value.Bounds);
        }
    }

    private readonly struct CachedOccluderSnapshotComparer : IScpExactSnapshotComparer<CachedOccluder>
    {
        public int ElementSizeBytes => 32;

        public bool AreEqual(in CachedOccluder left, in CachedOccluder right)
        {
            return left.Owner == right.Owner &&
                left.NetIdentity == right.NetIdentity &&
                left.VertexStart == right.VertexStart &&
                left.Bounds.Equals(right.Bounds) &&
                left.Winding.Equals(right.Winding);
        }

        public ulong AddToHash(ulong hash, in CachedOccluder value)
        {
            hash = AddSnapshotInt(hash, value.Owner.Id);
            hash = AddSnapshotInt(hash, value.NetIdentity.Id);
            hash = AddSnapshotInt(hash, value.VertexStart);
            hash = AddSnapshotBox(hash, value.Bounds);
            return AddSnapshotFloat(hash, value.Winding);
        }
    }

    #endregion

    #region Per-entity exact geometry

    private readonly record struct CasterEntitySnapshotHeader(
        Box2 Bounds,
        DirectionalFovVisibility FovVisibility);

    private readonly record struct OccluderEntitySnapshotHeader(
        Box2 Bounds,
        float Winding);

    private sealed class CasterEntitySnapshot(ScpGeometrySourceIdentity identity)
    {
        public readonly ScpGeometrySourceIdentity Identity = identity;
        public readonly CasterWorldGeometryCache WorldGeometry = new();
        public readonly ScpGeometryEntityRevisionState<
            CasterEntitySnapshotHeader,
            CachedContour,
            CachedContourEntitySnapshotComparer,
            Vector2,
            Vector2SnapshotComparer> Exact = new();

        public Box2 Bounds;
        public ScpGeometrySnapshotResidency Residency;
        public ulong LastVisibleFrame;
        public bool DeletePending;

        public long EstimatedBytes => 96L + WorldGeometry.EstimatedBytes + Exact.EstimatedBytes;
    }

    private sealed class CasterWorldGeometryCache
    {
        public readonly ScpCasterWorldGeometryInputState Input = new();
        public readonly List<CachedContour> Contours = new(4);
        public readonly List<Vector2> Vertices = new(16);
        public bool UsesFallback = true;
        public Box2 Bounds;

        public long EstimatedBytes =>
            80L +
            Input.EstimatedBytes +
            56L + (long) Contours.Capacity * 28L +
            56L + (long) Vertices.Capacity * 8L;
    }

    private sealed class OccluderEntitySnapshot(ScpGeometrySourceIdentity identity)
    {
        public readonly ScpGeometrySourceIdentity Identity = identity;
        public readonly ScpGeometryEntityRevisionState<
            OccluderEntitySnapshotHeader,
            Vector2,
            Vector2SnapshotComparer,
            Vector2,
            Vector2SnapshotComparer> Exact = new();

        public Box2 Bounds;
        public ScpGeometrySnapshotResidency Residency;
        public ulong LastVisibleFrame;
        public bool DeletePending;

        public long EstimatedBytes => 80L + Exact.EstimatedBytes;
    }

    #endregion

    #region Cached geometry types

    private readonly record struct CachedCaster(
        EntityUid Owner,
        NetEntity NetIdentity,
        int ContourStart,
        int ContourCount,
        Box2 Bounds,
        DirectionalFovVisibility FovVisibility);

    private readonly record struct CasterFrameSourceStamp(
        EntityUid Owner,
        NetEntity NetIdentity,
        uint GeometryRevision,
        Box2 Bounds,
        DirectionalFovVisibility FovVisibility);

    private readonly record struct CachedContour(
        int VertexStart,
        int VertexCount,
        float Winding,
        Box2 Bounds);

    private readonly record struct CachedOccluder(
        EntityUid Owner,
        NetEntity NetIdentity,
        int VertexStart,
        Box2 Bounds,
        float Winding);

    private readonly record struct OccluderSelectionCandidate(
        EntityUid Owner,
        NetEntity NetIdentity,
        Box2 Bounds,
        float PriorityDistanceSquared,
        float Winding,
        Vector2 Vertex0,
        Vector2 Vertex1,
        Vector2 Vertex2,
        Vector2 Vertex3);

    private readonly record struct ProtectedSpriteLayer(
        EntityUid Owner,
        int LayerIndex,
        Texture Texture,
        Matrix3x2 WorldMatrix,
        Box2 Quad,
        Color Modulate)
    {
        public ScpProtectionLayerStableKey StableKey => new(Owner, LayerIndex);
    }

    private readonly record struct SpriteMatrices(
        Angle ScreenAngle,
        Matrix3x2 Sprite,
        Matrix3x2 Default,
        Matrix3x2 SnapToCardinals,
        Matrix3x2 NoRotation);

    private readonly struct SpriteQueryState(
        ScpShadowCasterOverlay overlay,
        MapId mapId,
        Box2 viewportBounds,
        EntityUid treeUid,
        Vector2 treePosition,
        Angle treeRotation,
        bool allowInactive,
        GeometrySnapshotState geometrySnapshots)
    {
        public readonly ScpShadowCasterOverlay Overlay = overlay;
        public readonly MapId MapId = mapId;
        public readonly Box2 ViewportBounds = viewportBounds;
        public readonly EntityUid TreeUid = treeUid;
        public readonly Vector2 TreePosition = treePosition;
        public readonly Angle TreeRotation = treeRotation;
        public readonly bool AllowInactive = allowInactive;
        public readonly GeometrySnapshotState GeometrySnapshots = geometrySnapshots;
    }

    private readonly struct OccluderQueryState(
        ScpShadowCasterOverlay overlay,
        EntityUid treeUid,
        Vector2 treePosition,
        Angle treeRotation,
        Vector2 priorityOrigin,
        int maximumOccluders)
    {
        public readonly ScpShadowCasterOverlay Overlay = overlay;
        public readonly EntityUid TreeUid = treeUid;
        public readonly Vector2 TreePosition = treePosition;
        public readonly Angle TreeRotation = treeRotation;
        public readonly Vector2 PriorityOrigin = priorityOrigin;
        public readonly int MaximumOccluders = maximumOccluders;
        public readonly Matrix3x2 TreeMatrix = treeRotation == Angle.Zero
            ? Matrix3x2.CreateTranslation(treePosition)
            : Matrix3Helpers.CreateTransform(treePosition, treeRotation);
    }

    #endregion
}

internal static class ScpSparseSpriteQuery
{
    public static Box2 ToTreeLocalBounds(
        Box2 worldBounds,
        Vector2 treePosition,
        Angle treeRotation)
    {
        return treeRotation == Angle.Zero
            ? worldBounds.Translated(-treePosition)
            : Matrix3Helpers.CreateInverseTransform(treePosition, treeRotation).TransformBox(worldBounds);
    }
}

internal readonly record struct ScpProtectionLayerStableKey(EntityUid Owner, int LayerIndex)
    : IComparable<ScpProtectionLayerStableKey>
{
    public int CompareTo(ScpProtectionLayerStableKey other)
    {
        var comparison = Owner.CompareTo(other.Owner);
        return comparison != 0 ? comparison : LayerIndex.CompareTo(other.LayerIndex);
    }
}
