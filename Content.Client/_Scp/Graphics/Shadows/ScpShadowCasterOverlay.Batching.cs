using System.Numerics;
using System.Runtime.InteropServices;
using Robust.Client.Graphics;
using Robust.Shared.Maths;
using Robust.Shared.Profiling;

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

    private readonly List<AtlasLight> _atlasLights = new(GeometryBatchSize);
    private readonly List<AtlasPage> _atlasPages = new(GeometryBatchSize);
    private readonly Vector2i[] _atlasRectangleSizes = new Vector2i[GeometryBatchSize];
    private readonly ScpAtlasPlacement[] _atlasPlacements = new ScpAtlasPlacement[GeometryBatchSize];
    private readonly List<DrawVertexUV2DColor> _atlasMaskVertices = new(4096);
    private readonly Color[] _pageLightAtlasData = new Color[GeometryBatchSize];
    private readonly bool[] _pageHasMask = new bool[GeometryBatchSize];
    private readonly bool[] _pageHasCasterMask = new bool[GeometryBatchSize];
    private readonly Vector2[] _clipPolygonA = new Vector2[8];
    private readonly Vector2[] _clipPolygonB = new Vector2[8];

    private readonly List<ProtectionSourceBatch> _protectionBatches = new(16);
    private int _activeProtectionBatches;

    #endregion

    #region Geometry batch rendering

    private void DrawGeometryBatch(int lightStart, int lightCount, CachedResources resources)
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

            _atlasLights.Add(new AtlasLight(
                light,
                batchIndex,
                source,
                default,
                softness));
        }

        if (_atlasLights.Count == 0)
            return;

        PackAtlasLights(_targetSize);

        for (var pageIndex = 0; pageIndex < _atlasPages.Count; pageIndex++)
        {
            using var pageProfile = _prof.IsEnabled || _prof.IsTracyEnabled
                ? _prof.Group("ScpContentLighting.ShadowAtlas")
                : (ProfManager.GroupGuard?) null;
            DrawAtlasPage(_atlasPages[pageIndex], resources);
        }
    }

    private void PackAtlasLights(Vector2i targetSize)
    {
        _atlasPages.Clear();
        for (var i = 0; i < _atlasLights.Count; i++)
            _atlasRectangleSizes[i] = _atlasLights[i].Source.Size;

        var pageCount = ScpLightingBatchPlanner.PackShelves(
            _atlasRectangleSizes,
            _atlasLights.Count,
            targetSize,
            _atlasPlacements);

        for (var i = 0; i < _atlasLights.Count; i++)
            _atlasLights[i] = _atlasLights[i] with { Destination = _atlasPlacements[i].Bounds.TopLeft };

        var pageStart = 0;
        for (var page = 0; page < pageCount; page++)
        {
            var pageEnd = pageStart;
            while (pageEnd < _atlasLights.Count && _atlasPlacements[pageEnd].Page == page)
                pageEnd++;

            _atlasPages.Add(new AtlasPage(pageStart, pageEnd - pageStart));
            pageStart = pageEnd;
        }
    }

    private void DrawAtlasPage(AtlasPage page, CachedResources resources)
    {
        BuildAtlasMask(page);
        if (_atlasMaskVertices.Count == 0)
        {
            for (var index = 0; index < page.Count; index++)
                AddStandardLight(_atlasLights[page.Start + index].Light);
            return;
        }

        resources.EnsureShadowMask(_clyde);
        if (!_currentHasProtection &&
            _protectedSpriteLayers.Count != 0 &&
            PageHasCasterMask(page.Count))
        {
            EnsureProtectionMask(resources);
        }

        _drawHandle!.RenderInRenderTarget(resources.ShadowMask!, _drawShadowMask, Color.Black);

        PreparePageLightAtlas(page, _targetSize);
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
            AppendLightQuad(batch.Vertices, light, new Vector2(light.Radius, index));
        }

        using var contributionProfile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting.ShadowContributions")
            : (ProfManager.GroupGuard?) null;
        DrawShadowLightBatches(resources);
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
            light.Radius * MathF.Sqrt(
                _targetMatrix.M11 * _targetMatrix.M11 +
                _targetMatrix.M21 * _targetMatrix.M21) + padding,
            light.Radius * MathF.Sqrt(
                _targetMatrix.M12 * _targetMatrix.M12 +
                _targetMatrix.M22 * _targetMatrix.M22) + padding);
        var pixelBounds = new Box2(center - extent, center + extent);

        var left = Math.Clamp((int) MathF.Floor(pixelBounds.Left), 0, targetSize.X);
        var top = Math.Clamp((int) MathF.Floor(pixelBounds.Bottom), 0, targetSize.Y);
        var right = Math.Clamp((int) MathF.Ceiling(pixelBounds.Right), left, targetSize.X);
        var bottom = Math.Clamp((int) MathF.Ceiling(pixelBounds.Top), top, targetSize.Y);
        return new UIBox2i(left, top, right, bottom);
    }

    #endregion

    #region Atlas mask geometry

    private void BuildAtlasMask(AtlasPage page)
    {
        _atlasMaskVertices.Clear();
        Array.Clear(_pageHasMask);
        Array.Clear(_pageHasCasterMask);

        for (var index = 0; index < page.Count; index++)
        {
            var atlasLight = _atlasLights[page.Start + index];
            var geometry = _lightGeometryBuffers[atlasLight.GeometryIndex];
            var vertexStart = _atlasMaskVertices.Count;
            _pageHasCasterMask[index] = AppendClippedAtlasGeometry(
                CollectionsMarshal.AsSpan(geometry.Vertices),
                atlasLight.Source,
                atlasLight.Destination);
            _pageHasMask[index] = _atlasMaskVertices.Count != vertexStart;
        }
    }

    private bool AppendClippedAtlasGeometry(
        ReadOnlySpan<DrawVertexUV2DColor> vertices,
        UIBox2i source,
        Vector2i destination)
    {
        var polygonA = _clipPolygonA;
        var polygonB = _clipPolygonB;
        var offset = destination - source.TopLeft;
        var bounds = new UIBox2(source.Left, source.Top, source.Right, source.Bottom);
        var hasCasterMask = false;

        for (var vertex = 0; vertex + 2 < vertices.Length; vertex += 3)
        {
            var count = ScpLightingBatchPlanner.ClipTriangle(
                Vector2.Transform(vertices[vertex].Position, _targetMatrix),
                Vector2.Transform(vertices[vertex + 1].Position, _targetMatrix),
                Vector2.Transform(vertices[vertex + 2].Position, _targetMatrix),
                bounds,
                polygonA,
                polygonB,
                polygonA);
            if (count < 3)
                continue;

            var color = vertices[vertex].Color;
            hasCasterMask |= color.R > 0f || color.G > 0f;
            var first = AtlasPixelToWorld(polygonA[0], offset);
            for (var triangle = 1; triangle < count - 1; triangle++)
            {
                _atlasMaskVertices.Add(new DrawVertexUV2DColor(first, color));
                _atlasMaskVertices.Add(new DrawVertexUV2DColor(
                    AtlasPixelToWorld(polygonA[triangle], offset),
                    color));
                _atlasMaskVertices.Add(new DrawVertexUV2DColor(
                    AtlasPixelToWorld(polygonA[triangle + 1], offset),
                    color));
            }
        }

        return hasCasterMask;
    }

    private Vector2 AtlasPixelToWorld(Vector2 position, Vector2 offset)
    {
        return ScpLightingBatchPlanner.RelocatePixelPoint(
            position,
            _inverseTargetMatrix,
            offset);
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

    private void PreparePageLightAtlas(AtlasPage page, Vector2i targetSize)
    {
        Array.Clear(_pageLightAtlasData);
        var size = (Vector2) targetSize;

        for (var index = 0; index < page.Count; index++)
        {
            var atlasLight = _atlasLights[page.Start + index];
            var localCenter = Vector2.Transform(atlasLight.Light.Position, _targetMatrix);
            var centerUv = localCenter / size;
            centerUv.Y = 1f - centerUv.Y;

            var pixelOffset = atlasLight.Destination - atlasLight.Source.TopLeft;
            var sampleOffset = new Vector2(
                pixelOffset.X / size.X,
                -pixelOffset.Y / size.Y);
            // ShaderInstance has no Vector4[] parameter overload. Color[] uses the
            // same vec4 transport; pre-encoding cancels Clyde's automatic sRGB conversion.
            _pageLightAtlasData[index] = Color.ToSrgb(new Color(
                centerUv.X,
                centerUv.Y,
                sampleOffset.X,
                sampleOffset.Y));
        }
    }

    private void DrawShadowLightBatches(CachedResources resources)
    {
        var handle = _drawHandle!;
        var shadowMask = resources.ShadowMask!.Texture;
        var protectionMask = resources.ProtectionMask?.Texture ?? shadowMask;
        handle.SetTransform(_targetMatrix);

        for (var batchIndex = 0; batchIndex < _activeShadowLightBatches; batchIndex++)
        {
            var batch = _shadowLightBatches[batchIndex];
            var key = batch.Key;
            handle.UseShader(resources.GetShadowShader(
                _contributionPrototype,
                shadowMask,
                protectionMask,
                _pageLightAtlasData,
                key.Softness,
                key.Falloff,
                key.CurveFactor,
                key.HasProtection,
                _directionalFovActive,
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
        resources.EnsureProtectionMask(_clyde);
        _drawHandle!.RenderInRenderTarget(
            resources.ProtectionMask!,
            _drawProtectionMask,
            Color.Black);
        _currentHasProtection = true;
    }

    private void PrepareProtectionBatches()
    {
        _activeProtectionBatches = 0;

        for (var layerIndex = 0; layerIndex < _protectedSpriteLayers.Count; layerIndex++)
        {
            var layer = _protectedSpriteLayers[layerIndex];
            var source = layer.Texture is AtlasTexture atlas
                ? atlas.SourceTexture
                : layer.Texture;
            var batchIndex = -1;

            for (var i = 0; i < _activeProtectionBatches; i++)
            {
                if (!ReferenceEquals(_protectionBatches[i].Source, source))
                    continue;

                batchIndex = i;
                break;
            }

            if (batchIndex == -1)
            {
                batchIndex = _activeProtectionBatches++;
                if (batchIndex == _protectionBatches.Count)
                    _protectionBatches.Add(new ProtectionSourceBatch());

                _protectionBatches[batchIndex].Reset(source);
            }

            _protectionBatches[batchIndex].Layers.Add(layer);
        }
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
        float Softness);

    private readonly record struct AtlasPage(int Start, int Count);

    private sealed class ProtectionSourceBatch
    {
        public Texture Source = default!;
        public readonly List<ProtectedSpriteLayer> Layers = new(64);

        public void Reset(Texture source)
        {
            Source = source;
            Layers.Clear();
        }
    }

    #endregion
}
