using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.Graphics;
using Robust.Shared.Maths;
using Robust.Shared.Profiling;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterOverlay
{
    #region Batched light state

    private readonly Dictionary<StandardLightBatchKey, int> _standardLightBatchLookup = new(16);
    private readonly List<StandardLightBatch> _standardLightBatches = new(16);
    private int _activeStandardLightBatches;

    private readonly Dictionary<ShadowLightBatchKey, int> _shadowLightBatchLookup = new(16);
    private readonly List<ShadowLightBatch> _shadowLightBatches = new(16);
    private int _activeShadowLightBatches;

    private readonly List<AtlasLight> _atlasLights = new(16);
    private readonly List<AtlasPage> _atlasPages = new(16);
    private static readonly Comparison<AtlasLight> AtlasLightIdentityComparison = static (left, right) =>
    {
        var result = left.Light.Owner.CompareTo(right.Light.Owner);
        return result != 0
            ? result
            : left.Light.CreationTick.CompareTo(right.Light.CreationTick);
    };
    private Vector2i[] _atlasRectangleSizes = new Vector2i[16];
    private Vector2i[] _atlasCandidateRectangleSizes = new Vector2i[16];
    private int[] _atlasCandidateOrder = new int[16];
    private ScpAtlasPlacement[] _atlasPlacements = new ScpAtlasPlacement[16];
    private ScpAtlasPlacement[] _atlasCandidatePlacements = new ScpAtlasPlacement[16];
    private AtlasLight[] _atlasCandidateLights = new AtlasLight[16];
    private readonly List<DrawVertexUV2DColor> _atlasMaskVertices = new(4096);
    private Rgba32[] _lightMetadataPixels = new Rgba32[32];
    private bool[] _pageHasMask = new bool[16];
    private bool[] _pageHasCasterMask = new bool[16];
    private readonly List<WideMaskPageStamp> _wideMaskPageStamps = new(128);
    private readonly DrawVertexUV2DColor[] _wideMaskClearVertices = new DrawVertexUV2DColor[6];
    private readonly Vector2[] _clipPolygonA = new Vector2[8];
    private readonly Vector2[] _clipPolygonB = new Vector2[8];
    private Vector2i _wideAtlasSize;
    private UIBox2i _wideMaskDrawBounds;

    private readonly Dictionary<Texture, int> _protectionBatchLookup = new(16);
    private readonly List<ProtectionSourceBatch> _protectionBatches = new(16);
    private int _activeProtectionBatches;
    private Vector4 _lightCenterDecode;

    #endregion

    #region Geometry batch rendering

    private void DrawGeometryBatch(
        int lightStart,
        int lightCount,
        CachedResources resources)
    {
        _atlasLights.Clear();

        for (var batchIndex = 0; batchIndex < lightCount; batchIndex++)
        {
            var light = _lights[lightStart + batchIndex];
            if (light.Radius <= 0f || light.Energy <= 0f)
                continue;

            var geometry = _lightGeometryBuffers[batchIndex];
            var hasShadowMask = _currentDrawShadows && light.CastShadows && geometry.HasMask;
            if (!hasShadowMask)
            {
                AddStandardLight(light);
                continue;
            }

            var softness = GetLightSoftness(light);
            var source = GetMaskPixelBounds(light, softness, _targetSize);
            if (source.Width == 0 || source.Height == 0)
            {
                AddStandardLight(light);
                continue;
            }

            var screenCenter = Vector2.Transform(light.Position, _targetMatrix);
            var padding = ScpLightingBatchPlanner.GetSoftShadowPaddingPixels(softness);
            var pixelExtent = new Vector2(
                light.Radius * _targetPixelScale.X + padding,
                light.Radius * _targetPixelScale.Y + padding);
            var translationInvariant =
                ScpLightingBatchPlanner.TryGetTranslationInvariantMaskBounds(
                    screenCenter,
                    pixelExtent,
                    _targetSize,
                    out var stableSource,
                    out var localCenter,
                    out var phase);
            if (translationInvariant)
                source = stableSource;

            _atlasLights.Add(new AtlasLight(
                light,
                batchIndex,
                source,
                default,
                softness,
                translationInvariant,
                localCenter,
                phase));
        }

        if (_atlasLights.Count == 0)
            return;

        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.AtlasPacking")
                   : (ProfManager.GroupGuard?) null)
        {
            _wideAtlasSize = GetWideAtlasSize(_targetSize);
            PackAtlasLights();
        }
        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.MetadataUpload")
                   : (ProfManager.GroupGuard?) null)
        {
            PrepareLightMetadata(resources, _targetSize, _wideAtlasSize);
        }
        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.AtlasGeometry")
                   : (ProfManager.GroupGuard?) null)
        {
            PrepareAtlasGeometry();
        }

        var allowWideMaskReuse = _atlasPages.Count == 1;
        if (!allowWideMaskReuse)
            resources.InvalidateWideShadowMask();

        for (var pageIndex = 0; pageIndex < _atlasPages.Count; pageIndex++)
        {
            using var pageProfile = _prof.IsEnabled || _prof.IsTracyEnabled
                ? _prof.Group("ScpContentLighting.ShadowAtlas")
                : (ProfManager.GroupGuard?) null;
            DrawAtlasPage(_atlasPages[pageIndex], resources, allowWideMaskReuse);
        }
    }

    private void PackAtlasLights()
    {
        _atlasPages.Clear();
        EnsureAtlasScratchCapacity(_atlasLights.Count);

        // Light-tree traversal order changes as PVS nodes enter and leave. Keep
        // otherwise identical atlas layouts stable so cached geometry is not
        // translated just because the query returned a different order.
        // List.Sort(IComparer<T>) allocates a comparer helper on current .NET.
        // The cached Comparison overload keeps the steady render path at zero GC.
        _atlasLights.Sort(AtlasLightIdentityComparison);
        for (var i = 0; i < _atlasLights.Count; i++)
            _atlasRectangleSizes[i] = _atlasLights[i].Source.Size;

        var atlasSize = _wideAtlasSize;
        var pageCount = ScpLightingBatchPlanner.PackShelves(
            _atlasRectangleSizes,
            _atlasLights.Count,
            atlasSize,
            _atlasPlacements);

        // A moving viewport and PVS churn can produce an arbitrary light order.
        // Height-first shelves waste less vertical space, but retain the input
        // order unless the candidate actually removes a render-target page.
        if (pageCount > 1 && _atlasLights.Count > 1)
        {
            if (TryPackHeightSortedShelves(
                _atlasRectangleSizes,
                _atlasLights.Count,
                atlasSize,
                pageCount,
                _atlasCandidateRectangleSizes,
                _atlasCandidateOrder,
                _atlasCandidatePlacements,
                out var sortedPageCount))
            {
                for (var i = 0; i < _atlasLights.Count; i++)
                    _atlasCandidateLights[i] = _atlasLights[_atlasCandidateOrder[i]];
                for (var i = 0; i < _atlasLights.Count; i++)
                    _atlasLights[i] = _atlasCandidateLights[i];

                (_atlasRectangleSizes, _atlasCandidateRectangleSizes) =
                    (_atlasCandidateRectangleSizes, _atlasRectangleSizes);
                (_atlasPlacements, _atlasCandidatePlacements) =
                    (_atlasCandidatePlacements, _atlasPlacements);
                pageCount = sortedPageCount;
            }
        }

        for (var i = 0; i < _atlasLights.Count; i++)
            _atlasLights[i] = _atlasLights[i] with { Destination = _atlasPlacements[i].Bounds.TopLeft };

        var pageStart = 0;
        for (var page = 0; page < pageCount; page++)
        {
            var pageEnd = pageStart;
            while (pageEnd < _atlasLights.Count && _atlasPlacements[pageEnd].Page == page)
                pageEnd++;

            var lightCount = pageEnd - pageStart;
            var bounds = ScpLightingBatchPlanner.GetPlacementUnion(
                _atlasPlacements.AsSpan(pageStart, lightCount));
            _atlasPages.Add(new AtlasPage(pageStart, lightCount, bounds));
            pageStart = pageEnd;
        }
    }

    private void EnsureAtlasScratchCapacity(int capacity)
    {
        if (_atlasRectangleSizes.Length >= capacity)
            return;

        var newCapacity = Math.Max(capacity, _atlasRectangleSizes.Length * 2);
        Array.Resize(ref _atlasRectangleSizes, newCapacity);
        Array.Resize(ref _atlasCandidateRectangleSizes, newCapacity);
        Array.Resize(ref _atlasCandidateOrder, newCapacity);
        Array.Resize(ref _atlasPlacements, newCapacity);
        Array.Resize(ref _atlasCandidatePlacements, newCapacity);
        Array.Resize(ref _atlasCandidateLights, newCapacity);
    }

    internal static bool TryPackHeightSortedShelves(
        Vector2i[] rectangleSizes,
        int rectangleCount,
        Vector2i pageSize,
        int currentPageCount,
        Vector2i[] candidateSizes,
        int[] candidateOrder,
        ScpAtlasPlacement[] candidatePlacements,
        out int candidatePageCount)
    {
        if (rectangleCount < 0 || rectangleCount > rectangleSizes.Length)
            throw new ArgumentOutOfRangeException(nameof(rectangleCount));
        if (currentPageCount < 1)
            throw new ArgumentOutOfRangeException(nameof(currentPageCount));
        if (candidateSizes.Length < rectangleCount ||
            candidateOrder.Length < rectangleCount ||
            candidatePlacements.Length < rectangleCount)
        {
            throw new ArgumentException("Candidate scratch buffers are too small.");
        }

        for (var i = 0; i < rectangleCount; i++)
        {
            candidateSizes[i] = rectangleSizes[i];
            candidateOrder[i] = i;
        }

        candidateSizes.AsSpan(0, rectangleCount).Sort(
            candidateOrder.AsSpan(0, rectangleCount),
            AtlasRectangleSizeComparer.Instance);
        candidatePageCount = ScpLightingBatchPlanner.PackShelves(
            candidateSizes,
            rectangleCount,
            pageSize,
            candidatePlacements);
        return candidatePageCount < currentPageCount;
    }

    private void DrawAtlasPage(
        AtlasPage page,
        CachedResources resources,
        bool allowWideMaskReuse)
    {
        var hasMask = PrepareAtlasMaskState(page, allowWideMaskReuse);

        if (!hasMask)
        {
            resources.InvalidateWideShadowMask();
            for (var index = 0; index < page.Count; index++)
                AddStandardLight(_atlasLights[page.Start + index].Light);
            return;
        }

        var recreated = resources.EnsureShadowMask(_clyde, _wideAtlasSize);
        var pageHasCasterMask = PageHasCasterMask(page.Count);
        if (!_currentHasProtection &&
            _protectedSpriteLayers.Count != 0 &&
            pageHasCasterMask)
        {
            EnsureProtectionMask(resources);
        }

        var reuseMask = allowWideMaskReuse &&
            !recreated &&
            resources.IsWideShadowMaskCurrent(
                _currentMapId,
                _targetSize,
                page.Bounds,
                CollectionsMarshal.AsSpan(_wideMaskPageStamps));
        if (!reuseMask)
        {
            BuildAtlasMaskVertices(page);
            _wideMaskDrawBounds = page.Bounds;
            try
            {
                _renderHandle!.RenderInRenderTarget(resources.ShadowMask!, _drawShadowMask, null);
            }
            catch
            {
                resources.InvalidateWideShadowMask();
                throw;
            }

            if (allowWideMaskReuse)
            {
                resources.CommitWideShadowMask(
                    _currentMapId,
                    _targetSize,
                    page.Bounds,
                    CollectionsMarshal.AsSpan(_wideMaskPageStamps));
            }
        }

        BeginShadowLightBatches();

        for (var index = 0; index < page.Count; index++)
        {
            var atlasLight = _atlasLights[page.Start + index];
            var light = atlasLight.Light;
            if (!_pageHasMask[index])
            {
                AddStandardLight(light);
                continue;
            }

            var hasProtection = _currentHasProtection && _pageHasCasterMask[index];
            var key = new ShadowLightBatchKey(
                light.Mask ?? _whiteTexture,
                light.Falloff,
                light.CurveFactor,
                atlasLight.Softness,
                hasProtection);
            var batch = GetShadowLightBatch(key);
            var metadataX = (2f * (page.Start + index) + 0.5f) /
                resources.LightMetadata!.Width;
            AppendLightQuad(batch.Vertices, light, new Vector2(light.Radius, metadataX));
        }

        using var contributionProfile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting.ShadowContributions")
            : (ProfManager.GroupGuard?) null;
        DrawShadowLightBatches(resources, pageHasCasterMask);
    }

    private bool PageHasCasterMask(int lightCount)
    {
        for (var i = 0; i < lightCount; i++)
        {
            if (_pageHasCasterMask[i])
                return true;
        }

        return false;
    }

    private UIBox2i GetMaskPixelBounds(
        in ScpShadowLightData light,
        float softness,
        Vector2i targetSize)
    {
        var padding = ScpLightingBatchPlanner.GetSoftShadowPaddingPixels(softness);
        var center = Vector2.Transform(light.Position, _targetMatrix);
        var extent = new Vector2(
            light.Radius * _targetPixelScale.X + padding,
            light.Radius * _targetPixelScale.Y + padding);
        var pixelBounds = new Box2(center - extent, center + extent);

        var left = Math.Clamp((int) MathF.Floor(pixelBounds.Left), 0, targetSize.X);
        var top = Math.Clamp((int) MathF.Floor(pixelBounds.Bottom), 0, targetSize.Y);
        var right = Math.Clamp((int) MathF.Ceiling(pixelBounds.Right), left, targetSize.X);
        var bottom = Math.Clamp((int) MathF.Ceiling(pixelBounds.Top), top, targetSize.Y);
        return new UIBox2i(left, top, right, bottom);
    }

    internal static Vector2i GetWideAtlasSize(Vector2i targetSize)
    {
        return new Vector2i(
            Math.Max(targetSize.X, ScpShadowAtlasBuddyAllocator.AtlasSize),
            Math.Max(targetSize.Y, ScpShadowAtlasBuddyAllocator.AtlasSize));
    }

    #endregion

    #region Atlas mask geometry

    private void PrepareAtlasGeometry()
    {
        _dirtyAtlasGeometryTasks.Clear();
        var estimatedWork = 0L;

        for (var atlasIndex = 0; atlasIndex < _atlasLights.Count; atlasIndex++)
        {
            var atlasLight = _atlasLights[atlasIndex];
            var geometry = _lightGeometryBuffers[atlasLight.GeometryIndex];
            var sourceRelativeTargetMatrix = atlasLight.TranslationInvariant
                ? ScpLightingBatchPlanner.GetLightRelativeTargetMatrix(
                    _targetMatrix,
                    atlasLight.Light.Position,
                    atlasLight.LocalCenter)
                : ScpLightingBatchPlanner.GetSourceRelativeTargetMatrix(
                    _targetMatrix,
                    atlasLight.Source.TopLeft);
            var atlasOffset = (Vector2) atlasLight.Destination +
                              (atlasLight.TranslationInvariant ? atlasLight.Phase : Vector2.Zero);

            var casterKey = new ScpAtlasGeometryCacheKey(
                atlasLight.Light.Owner,
                geometry.CasterCache.Revision,
                0,
                sourceRelativeTargetMatrix,
                atlasLight.Source.Size);
            var rebuildCaster = geometry.HasRenderableCasterMask &&
                                !geometry.AtlasCasterCache.IsCurrent(casterKey);
            if (geometry.HasRenderableCasterMask && !rebuildCaster)
            {
                if (geometry.AtlasCasterOffset != atlasOffset)
                {
                    if (TranslateAtlasVertices(
                        geometry.AtlasCasterVertices,
                        atlasOffset - geometry.AtlasCasterOffset))
                    {
                        geometry.MarkAtlasContentChanged();
                    }
                }

                geometry.AtlasCasterOffset = atlasOffset;
            }

            var occluderKey = new ScpAtlasGeometryCacheKey(
                atlasLight.Light.Owner,
                0,
                geometry.OccluderCache.Revision,
                sourceRelativeTargetMatrix,
                atlasLight.Source.Size);
            var rebuildOccluder = geometry.HasRenderableOccluderMask &&
                                  !geometry.AtlasOccluderCache.IsCurrent(occluderKey);
            if (geometry.HasRenderableOccluderMask && !rebuildOccluder)
            {
                if (geometry.AtlasOccluderOffset != atlasOffset)
                {
                    if (TranslateAtlasVertices(
                        geometry.AtlasOccluderVertices,
                        atlasOffset - geometry.AtlasOccluderOffset))
                    {
                        geometry.MarkAtlasContentChanged();
                    }
                }

                geometry.AtlasOccluderOffset = atlasOffset;
            }

            if (!rebuildCaster && !rebuildOccluder)
                continue;

            if (rebuildCaster)
                estimatedWork += geometry.CasterVertices.Count;
            if (rebuildOccluder)
                estimatedWork += geometry.OccluderVertices.Count;
            _dirtyAtlasGeometryTasks.Add(new AtlasGeometryTask(
                geometry,
                sourceRelativeTargetMatrix,
                atlasLight.Source.Size,
                atlasOffset,
                casterKey,
                occluderKey,
                rebuildCaster,
                rebuildOccluder));
        }

        ProcessAtlasGeometryBatch(estimatedWork);
    }

    private void RebuildAtlasGeometry(AtlasGeometryTask task)
    {
        var geometry = task.Geometry;
        if (task.RebuildCaster)
        {
            geometry.AtlasCasterVertices.Clear();
            geometry.AtlasHasCasterMask = AppendClippedAtlasGeometry(
                CollectionsMarshal.AsSpan(geometry.CasterVertices),
                task.SourceRelativeTargetMatrix,
                task.SourceSize,
                task.AtlasOffset,
                geometry.AtlasCasterVertices,
                geometry.AtlasClipPolygonA,
                geometry.AtlasClipPolygonB);
            geometry.AtlasCasterCache.Commit(task.CasterKey);
            geometry.AtlasCasterOffset = task.AtlasOffset;
            geometry.MarkAtlasContentChanged();
        }

        if (task.RebuildOccluder)
        {
            geometry.AtlasOccluderVertices.Clear();
            AppendClippedAtlasGeometry(
                CollectionsMarshal.AsSpan(geometry.OccluderVertices),
                task.SourceRelativeTargetMatrix,
                task.SourceSize,
                task.AtlasOffset,
                geometry.AtlasOccluderVertices,
                geometry.AtlasClipPolygonA,
                geometry.AtlasClipPolygonB);
            geometry.AtlasHasOccluderMask = geometry.AtlasOccluderVertices.Count != 0;
            geometry.AtlasOccluderCache.Commit(task.OccluderKey);
            geometry.AtlasOccluderOffset = task.AtlasOffset;
            geometry.MarkAtlasContentChanged();
        }
    }

    private bool PrepareAtlasMaskState(AtlasPage page, bool collectWideMaskStamps)
    {
        if (collectWideMaskStamps)
            _wideMaskPageStamps.Clear();
        EnsurePageScratchCapacity(page.Count);
        Array.Clear(_pageHasMask, 0, page.Count);
        Array.Clear(_pageHasCasterMask, 0, page.Count);
        var hasMask = false;

        for (var index = 0; index < page.Count; index++)
        {
            var atlasLight = _atlasLights[page.Start + index];
            var geometry = _lightGeometryBuffers[atlasLight.GeometryIndex];
            var hasCasterMask = geometry.HasRenderableCasterMask && geometry.AtlasHasCasterMask;
            var hasOccluderMask = geometry.HasRenderableOccluderMask && geometry.AtlasHasOccluderMask;
            _pageHasCasterMask[index] = hasCasterMask;
            _pageHasMask[index] = hasCasterMask || hasOccluderMask;
            hasMask |= hasCasterMask || hasOccluderMask;
            if (collectWideMaskStamps)
            {
                _wideMaskPageStamps.Add(new WideMaskPageStamp(
                    new PersistentLightIdentity(atlasLight.Light.Owner, atlasLight.Light.CreationTick),
                    geometry.Incarnation,
                    geometry.AtlasContentGeneration,
                    hasCasterMask,
                    hasOccluderMask,
                    geometry.AtlasCasterVertices.Count,
                    geometry.AtlasOccluderVertices.Count,
                    geometry.AtlasCasterOffset,
                    geometry.AtlasOccluderOffset));
            }
        }

        return hasMask;
    }

    private void BuildAtlasMaskVertices(AtlasPage page)
    {
        _atlasMaskVertices.Clear();
        for (var index = 0; index < page.Count; index++)
        {
            var geometry = _lightGeometryBuffers[_atlasLights[page.Start + index].GeometryIndex];
            if (_pageHasCasterMask[index])
                _atlasMaskVertices.AddRange(geometry.AtlasCasterVertices);
            if (_pageHasMask[index] && !_pageHasCasterMask[index])
                _atlasMaskVertices.AddRange(geometry.AtlasOccluderVertices);
            else if (_pageHasCasterMask[index] && geometry.HasRenderableOccluderMask && geometry.AtlasHasOccluderMask)
                _atlasMaskVertices.AddRange(geometry.AtlasOccluderVertices);
        }
    }

    private void EnsurePageScratchCapacity(int capacity)
    {
        if (_pageHasMask.Length >= capacity)
            return;

        var newCapacity = Math.Max(capacity, _pageHasMask.Length * 2);
        Array.Resize(ref _pageHasMask, newCapacity);
        Array.Resize(ref _pageHasCasterMask, newCapacity);
    }

    private static bool AppendClippedAtlasGeometry(
        ReadOnlySpan<DrawVertexUV2DColor> vertices,
        in Matrix3x2 sourceRelativeTargetMatrix,
        Vector2i sourceSize,
        Vector2 destination,
        List<DrawVertexUV2DColor> output,
        Vector2[] polygonA,
        Vector2[] polygonB)
    {
        var atlasOffset = destination;
        var bounds = new UIBox2(0f, 0f, sourceSize.X, sourceSize.Y);
        var appended = false;

        for (var vertex = 0; vertex + 2 < vertices.Length; vertex += 3)
        {
            var firstPixel = Vector2.Transform(
                vertices[vertex].Position,
                sourceRelativeTargetMatrix);
            var secondPixel = Vector2.Transform(
                vertices[vertex + 1].Position,
                sourceRelativeTargetMatrix);
            var thirdPixel = Vector2.Transform(
                vertices[vertex + 2].Position,
                sourceRelativeTargetMatrix);
            var relation = ScpLightingBatchPlanner.ClassifyTriangle(
                firstPixel,
                secondPixel,
                thirdPixel,
                bounds);
            if (relation == ScpTriangleBoundsRelation.Outside)
                continue;

            var color = vertices[vertex].Color;
            if (relation == ScpTriangleBoundsRelation.Inside)
            {
                output.Add(new DrawVertexUV2DColor(firstPixel + atlasOffset, color));
                output.Add(new DrawVertexUV2DColor(secondPixel + atlasOffset, color));
                output.Add(new DrawVertexUV2DColor(thirdPixel + atlasOffset, color));
                appended = true;
                continue;
            }

            var count = ScpLightingBatchPlanner.ClipTriangle(
                firstPixel,
                secondPixel,
                thirdPixel,
                bounds,
                polygonA,
                polygonB,
                polygonA);
            if (count < 3)
                continue;

            var first = polygonA[0] + atlasOffset;
            for (var triangle = 1; triangle < count - 1; triangle++)
            {
                output.Add(new DrawVertexUV2DColor(first, color));
                output.Add(new DrawVertexUV2DColor(
                    polygonA[triangle] + atlasOffset,
                    color));
                output.Add(new DrawVertexUV2DColor(
                    polygonA[triangle + 1] + atlasOffset,
                    color));
            }
            appended = true;
        }

        return appended;
    }

    internal static bool TranslateAtlasVertices(
        List<DrawVertexUV2DColor> vertices,
        Vector2 offset)
    {
        if (offset == Vector2.Zero)
            return false;

        var span = CollectionsMarshal.AsSpan(vertices);
        for (var i = 0; i < span.Length; i++)
            span[i].Position += offset;
        return true;
    }

    #endregion

    #region Standard light batching

    private void BeginStandardLightBatches()
    {
        _standardLightBatchLookup.Clear();
        _activeStandardLightBatches = 0;
    }

    private void AddStandardLight(in ScpShadowLightData light)
    {
        var key = new StandardLightBatchKey(light.Mask ?? _whiteTexture, light.CurveFactor);
        if (!_standardLightBatchLookup.TryGetValue(key, out var batchIndex))
        {
            batchIndex = _activeStandardLightBatches++;
            if (batchIndex == _standardLightBatches.Count)
                _standardLightBatches.Add(new StandardLightBatch());

            _standardLightBatches[batchIndex].Reset(key);
            _standardLightBatchLookup.Add(key, batchIndex);
        }

        AppendLightQuad(
            _standardLightBatches[batchIndex].Vertices,
            light,
            new Vector2(light.Radius, light.Falloff));
    }

    private void DrawStandardLightBatches(CachedResources resources)
    {
        var handle = _drawHandle!;
        handle.SetTransform(_targetMatrix);

        for (var batchIndex = 0; batchIndex < _activeStandardLightBatches; batchIndex++)
        {
            var batch = _standardLightBatches[batchIndex];
            handle.UseShader(resources.GetStandardShader(
                _standardContributionPrototype,
                batch.Key.CurveFactor));
            DrawTriangleList(
                handle,
                batch.Key.Mask,
                CollectionsMarshal.AsSpan(batch.Vertices));
        }

        handle.UseShader(null);
    }

    #endregion

    #region Shadow light batching

    private void BeginShadowLightBatches()
    {
        _shadowLightBatchLookup.Clear();
        _activeShadowLightBatches = 0;
    }

    private ShadowLightBatch GetShadowLightBatch(ShadowLightBatchKey key)
    {
        if (_shadowLightBatchLookup.TryGetValue(key, out var batchIndex))
            return _shadowLightBatches[batchIndex];

        batchIndex = _activeShadowLightBatches++;
        if (batchIndex == _shadowLightBatches.Count)
            _shadowLightBatches.Add(new ShadowLightBatch());

        var batch = _shadowLightBatches[batchIndex];
        batch.Reset(key);
        _shadowLightBatchLookup.Add(key, batchIndex);
        return batch;
    }

    private void PrepareLightMetadata(
        CachedResources resources,
        Vector2i targetSize,
        Vector2i atlasSize)
    {
        resources.EnsureLightMetadata(_clyde, _lights.Count);
        var metadata = resources.LightMetadata!;
        var pixelCount = _atlasLights.Count * 2;
        if (_lightMetadataPixels.Length < pixelCount)
            Array.Resize(ref _lightMetadataPixels, Math.Max(pixelCount, _lightMetadataPixels.Length * 2));
        var size = (Vector2) targetSize;
        _lightCenterDecode = ScpLightingBatchPlanner.GetStableLightCenterDecode(
            targetSize,
            _targetPixelScale,
            _system.MaxLightRadius,
            4f);
        var minimumCenter = new Vector2(_lightCenterDecode.X, _lightCenterDecode.Y);
        var centerExtent = new Vector2(_lightCenterDecode.Z, _lightCenterDecode.W);

        for (var index = 0; index < _atlasLights.Count; index++)
        {
            var atlasLight = _atlasLights[index];
            var centerUv = Vector2.Transform(atlasLight.Light.Position, _targetMatrix) / size;
            centerUv.Y = 1f - centerUv.Y;
            var pixelOffset = atlasLight.Destination - atlasLight.Source.TopLeft;
            // FRAGCOORD and texture UVs use a bottom-left origin, while the
            // shelf packer and UIBox2i use a top-left origin. When the atlas is
            // taller than the viewport, preserve that change of origin in the
            // encoded Y offset.
            var atlasBottomOriginOffset = atlasSize.Y - targetSize.Y - pixelOffset.Y;
            var centerValues = new Vector2(
                ScpShadowMetadataCodec.NormalizeAffine(
                    centerUv.X,
                    minimumCenter.X,
                    centerExtent.X),
                ScpShadowMetadataCodec.NormalizeAffine(
                    centerUv.Y,
                    minimumCenter.Y,
                    centerExtent.Y));
            ScpShadowMetadataCodec.EncodeWithSignedPixelOffsets(
                centerValues,
                new Vector2i(pixelOffset.X, atlasBottomOriginOffset),
                out _lightMetadataPixels[index * 2],
                out _lightMetadataPixels[index * 2 + 1]);
        }

        var encoded = _lightMetadataPixels.AsSpan(0, pixelCount);
        if (resources.IsWideMetadataCurrent(encoded))
            return;

        metadata.SetSubImage(
            Vector2i.Zero,
            new Vector2i(pixelCount, 1),
            encoded);
        resources.CommitWideMetadata(encoded);
    }

    private void DrawShadowLightBatches(CachedResources resources, bool pageHasCasterMask)
    {
        var handle = _drawHandle!;
        var shadowMask = resources.ShadowMask!.Texture;
        var protectionMask = resources.ProtectionMask?.Texture ?? shadowMask;
        var lightMetadata = resources.LightMetadata!;
        var metadataPixelSize = 1f / lightMetadata.Width;
        var shadowUvScale = (Vector2) _targetSize / (Vector2) _wideAtlasSize;
        handle.SetTransform(_targetMatrix);

        for (var batchIndex = 0; batchIndex < _activeShadowLightBatches; batchIndex++)
        {
            var batch = _shadowLightBatches[batchIndex];
            var key = batch.Key;
            handle.UseShader(resources.GetShadowShader(
                _contributionPrototype,
                shadowMask,
                protectionMask,
                lightMetadata,
                metadataPixelSize,
                shadowUvScale,
                _lightCenterDecode,
                key.Softness,
                key.Falloff,
                key.CurveFactor,
                key.HasProtection,
                _directionalFovActive && pageHasCasterMask,
                _directionalFovOffset,
                _directionalViewDirection,
                _directionalRadialParameters,
                _directionalConeThresholds));
            DrawTriangleList(
                handle,
                key.Mask,
                CollectionsMarshal.AsSpan(batch.Vertices));
        }

        handle.UseShader(null);
    }

    #endregion

    #region Protection mask batching

    private void EnsureProtectionMask(CachedResources resources)
    {
        using var protectionProfile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting.ProtectionMask")
            : (ProfManager.GroupGuard?) null;
        var recreated = resources.EnsureProtectionMask(_clyde);
        if (!recreated && resources.IsProtectionMaskCurrent(_targetMatrix, _protectedSpriteLayers))
        {
            _currentHasProtection = true;
            return;
        }

        _drawHandle!.RenderInRenderTarget(
            resources.ProtectionMask!,
            _drawProtectionMask,
            Color.Black);
        resources.CommitProtectionMask(_targetMatrix, _protectedSpriteLayers);
        _currentHasProtection = true;
    }

    private void PrepareProtectionBatches()
    {
        _protectionBatchLookup.Clear();
        _activeProtectionBatches = 0;

        for (var layerIndex = 0; layerIndex < _protectedSpriteLayers.Count; layerIndex++)
        {
            var layer = _protectedSpriteLayers[layerIndex];
            var source = layer.Texture is AtlasTexture atlas
                ? atlas.SourceTexture
                : layer.Texture;
            if (!_protectionBatchLookup.TryGetValue(source, out var batchIndex))
            {
                batchIndex = _activeProtectionBatches++;
                if (batchIndex == _protectionBatches.Count)
                    _protectionBatches.Add(new ProtectionSourceBatch());

                _protectionBatches[batchIndex].Reset(source);
                _protectionBatchLookup.Add(source, batchIndex);
            }

            AppendProtectionQuad(_protectionBatches[batchIndex].Vertices, layer, source);
        }
    }

    private static void AppendProtectionQuad(
        List<DrawVertexUV2DColor> vertices,
        in ProtectedSpriteLayer layer,
        Texture source)
    {
        var region = layer.Texture is AtlasTexture atlas
            ? atlas.SubRegion
            : new UIBox2(0, 0, source.Width, source.Height);
        var uv = new Box2(
            region.Left / source.Width,
            (source.Height - region.Bottom) / source.Height,
            region.Right / source.Width,
            (source.Height - region.Top) / source.Height);
        var bottomLeft = Vector2.Transform(layer.Quad.BottomLeft, layer.WorldMatrix);
        var bottomRight = Vector2.Transform(layer.Quad.BottomRight, layer.WorldMatrix);
        var topRight = Vector2.Transform(layer.Quad.TopRight, layer.WorldMatrix);
        var topLeft = Vector2.Transform(layer.Quad.TopLeft, layer.WorldMatrix);

        vertices.Add(new DrawVertexUV2DColor(bottomLeft, uv.BottomLeft, layer.Modulate));
        vertices.Add(new DrawVertexUV2DColor(bottomRight, uv.BottomRight, layer.Modulate));
        vertices.Add(new DrawVertexUV2DColor(topRight, uv.TopRight, layer.Modulate));
        vertices.Add(new DrawVertexUV2DColor(bottomLeft, uv.BottomLeft, layer.Modulate));
        vertices.Add(new DrawVertexUV2DColor(topRight, uv.TopRight, layer.Modulate));
        vertices.Add(new DrawVertexUV2DColor(topLeft, uv.TopLeft, layer.Modulate));
    }

    #endregion

    #region Cached batching types

    private readonly record struct StandardLightBatchKey(Texture Mask, float CurveFactor);

    private sealed class StandardLightBatch
    {
        public StandardLightBatchKey Key;
        public readonly List<DrawVertexUV2DColor> Vertices = new(256);

        public void Reset(StandardLightBatchKey key)
        {
            Key = key;
            Vertices.Clear();
        }
    }

    private readonly record struct ShadowLightBatchKey(
        Texture Mask,
        float Falloff,
        float CurveFactor,
        float Softness,
        bool HasProtection);

    private sealed class ShadowLightBatch
    {
        public ShadowLightBatchKey Key;
        public readonly List<DrawVertexUV2DColor> Vertices = new(96);

        public void Reset(ShadowLightBatchKey key)
        {
            Key = key;
            Vertices.Clear();
        }
    }

    private readonly record struct AtlasLight(
        ScpShadowLightData Light,
        int GeometryIndex,
        UIBox2i Source,
        Vector2i Destination,
        float Softness,
        bool TranslationInvariant,
        Vector2 LocalCenter,
        Vector2 Phase);

    private readonly record struct AtlasPage(int Start, int Count, UIBox2i Bounds);

    private readonly record struct WideMaskPageStamp(
        PersistentLightIdentity Identity,
        uint GeometryIncarnation,
        ulong ContentGeneration,
        bool HasCasterMask,
        bool HasOccluderMask,
        int CasterVertexCount,
        int OccluderVertexCount,
        Vector2 CasterOffset,
        Vector2 OccluderOffset);

    private sealed class AtlasRectangleSizeComparer : IComparer<Vector2i>
    {
        public static readonly AtlasRectangleSizeComparer Instance = new();

        public int Compare(Vector2i left, Vector2i right)
        {
            var result = right.Y.CompareTo(left.Y);
            return result != 0 ? result : right.X.CompareTo(left.X);
        }
    }

    private sealed class ProtectionSourceBatch
    {
        public Texture Source = default!;
        public readonly List<DrawVertexUV2DColor> Vertices = new(384);

        public void Reset(Texture source)
        {
            Source = source;
            Vertices.Clear();
        }
    }

    #endregion
}
