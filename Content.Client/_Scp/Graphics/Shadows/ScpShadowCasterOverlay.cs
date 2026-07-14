using System.Numerics;
using Content.Client._Scp.Graphics.Shaders.FieldOfView;
using Content.Client.Graphics;
using Content.Shared._Scp.Vision.FOV;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Profiling;
using Robust.Shared.Prototypes;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Draws visual-only sprite and furniture shadows into the already rendered light target.
/// </summary>
public sealed partial class ScpShadowCasterOverlay : Overlay
{
    #region Shader prototypes

    private static readonly ProtoId<ShaderPrototype> ContributionShader = "ScpShadowLightContribution";
    private static readonly ProtoId<ShaderPrototype> MaskShader = "ScpShadowMask";
    private static readonly ProtoId<ShaderPrototype> SubtractShader = "ScpShadowSubtract";
    private static readonly ProtoId<ShaderPrototype> StencilShader = "ScpShadowStencil";
    private static readonly ProtoId<ShaderPrototype> UnshadedShader = "unshaded";
    private static readonly Action EmptyDraw = static () => { };

    #endregion

    #region Overlay configuration

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowWorld;
    public const int ContentZIndex = int.MinValue;

    #endregion

    #region Dependencies

    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IEyeManager _eyeManager = default!;
    [Dependency] private ILightManager _lightManager = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resourceCache = default!;
    [Dependency] private ProfManager _prof = default!;

    private readonly FieldOfViewOverlayManagementSystem _fieldOfViewManagement;
    private readonly FieldOfViewSystem _fieldOfViewSystem;
    private readonly LightTreeSystem _lightTree;
    private readonly OccluderSystem _occluderSystem;
    private readonly SpriteSystem _spriteSystem;
    private readonly SpriteTreeSystem _spriteTree;
    private readonly SharedTransformSystem _transformSystem;
    private readonly ScpShadowCasterSystem _system;

    private readonly EntityQuery<MapComponent> _mapQuery;
    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<FieldOfViewOccludableComponent> _fovOccludableQuery;
    private readonly EntityQuery<ScpShadowForegroundVisualsComponent> _foregroundQuery;
    private readonly EntityQuery<ScpShadowCasterVisualsComponent> _shadowQuery;

    #endregion

    #region Render state

    private readonly ShaderPrototype _contributionPrototype;
    private readonly ShaderInstance _outsideSubtractShader;
    private readonly ShaderInstance _maskShader;
    private readonly ShaderInstance _stencilShader;
    private readonly ShaderInstance _subtractShader;
    private readonly ShaderInstance _unshadedShader;
    private readonly Texture _whiteTexture;
    private readonly OverlayResourceCache<CachedResources> _resources = new();
    private readonly ScpShadowContourCache _contourCache;

    private readonly Action _drawShadowMask;
    private readonly Action _drawContribution;
    private readonly Action _drawComposite;
    private readonly Action _drawOutsideComposite;
    private readonly Action _drawProtectedSprites;

    private DrawingHandleWorld? _drawHandle;
    private List<DrawVertexUV2DColor>? _currentShadowMaskVertices;
    private ShaderInstance? _currentContributionShader;
    private Texture? _currentCompositeTexture;
    private Texture _currentLightMask;
    private Box2 _currentMaskBounds;
    private float _worldUnitsPerMaskPixel;
    private Matrix3x2 _targetMatrix;
    private Box2Rotated _worldBounds;
    private Angle _eyeRotation;

    #endregion

    public ScpShadowCasterOverlay(ScpShadowCasterSystem system)
    {
        ZIndex = ContentZIndex;
        IoCManager.InjectDependencies(this);

        _system = system;

        _fieldOfViewManagement = _entityManager.System<FieldOfViewOverlayManagementSystem>();
        _fieldOfViewSystem = _entityManager.System<FieldOfViewSystem>();
        _lightTree = _entityManager.System<LightTreeSystem>();
        _occluderSystem = _entityManager.System<OccluderSystem>();
        _spriteSystem = _entityManager.System<SpriteSystem>();
        _spriteTree = _entityManager.System<SpriteTreeSystem>();
        _transformSystem = _entityManager.System<SharedTransformSystem>();

        _mapQuery = _entityManager.GetEntityQuery<MapComponent>();
        _occluderQuery = _entityManager.GetEntityQuery<OccluderComponent>();
        _fovOccludableQuery = _entityManager.GetEntityQuery<FieldOfViewOccludableComponent>();
        _foregroundQuery = _entityManager.GetEntityQuery<ScpShadowForegroundVisualsComponent>();
        _shadowQuery = _entityManager.GetEntityQuery<ScpShadowCasterVisualsComponent>();

        _contourCache = system.ContourCache;
        _lightGeometryJob = new LightGeometryJob(this);
        _whiteTexture = Texture.White;
        _currentLightMask = _whiteTexture;

        _contributionPrototype = _prototypes.Index(ContributionShader);
        _subtractShader = _prototypes.Index(SubtractShader).InstanceUnique();
        _outsideSubtractShader = _prototypes.Index(SubtractShader).InstanceUnique();
        _maskShader = _prototypes.Index(MaskShader).Instance();
        _stencilShader = _prototypes.Index(StencilShader).InstanceUnique();
        _unshadedShader = _prototypes.Index(UnshadedShader).Instance();

        ConfigureStencilShaders();
        InitializeLightQuad();

        _drawShadowMask = DrawShadowMask;
        _drawContribution = DrawContribution;
        _drawComposite = DrawComposite;
        _drawOutsideComposite = DrawOutsideComposite;
        _drawProtectedSprites = DrawProtectedSprites;
    }

    #region Main render pass

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (_system.MobQuality == ScpShadowQuality.Disabled &&
            _system.ObjectQuality == ScpShadowQuality.Disabled)
            return false;

        var eye = args.Viewport.Eye;
        if (eye == null || args.MapId == MapId.Nullspace)
            return false;

        if (!eye.DrawLight)
            return false;

        if (!_lightManager.Enabled || !_lightManager.DrawLighting || !_lightManager.DrawShadows)
            return false;

        if (!_mapQuery.TryGetComponent(args.MapUid, out var map) || !map.LightingEnabled)
            return false;

        return true;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var viewport = args.Viewport;
        var eye = viewport.Eye!;

        using var profile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpVisualShadows")
            : (ProfManager.GroupGuard?) null;

        PrepareFovContext(args, eye);
        var lightCount = GatherLights(args.MapId, args.WorldBounds, args.WorldAABB);
        if (lightCount == 0)
            return;

        _eyeRotation = eye.Rotation;

        BuildFrameCache(args.MapId, args.WorldAABB);
        if (_frameCasters.Count == 0)
        {
            ClearDrawState();
            return;
        }

        ApplyProjectionPositions();
        var needsOutsideContribution = _directionalFovActive && _hasOutsideFovCasters;
        var reuseInsideContributionOutside = needsOutsideContribution && _outsideMaskMatchesInside;
        var buildSeparateOutsideContribution =
            needsOutsideContribution && !reuseInsideContributionOutside;
        BuildFrameOccluderCache(args.MapId, GetFrameOccluderQueryBounds(args.WorldAABB));
        CachedResources? resources = null;
        var hasNormalShadows = false;
        var hasOutsideShadows = false;
        var outsidePassInitialized = false;

        var geometryBatchSize = _system.GeometryBatchSize;
        for (var batchStart = 0; batchStart < lightCount; batchStart += geometryBatchSize)
        {
            var batchCount = Math.Min(geometryBatchSize, lightCount - batchStart);
            PrepareGeometryBatch(batchStart, batchCount, buildSeparateOutsideContribution);

            for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                var light = _lights[batchStart + batchIndex];
                if (light.Radius <= 0f || light.Energy <= 0f)
                    continue;

                var geometry = _lightGeometryBuffers[batchIndex];
                if (!geometry.HasInsideMask && !geometry.HasOutsideMask)
                    continue;

                var softness = GetLightSoftness(light);
                var activeResources = resources ??= BeginRenderPass(args, eye);
                PrepareLightRenderState(light, softness, geometry.CombinedCasterBounds());
                _currentShadowMaskVertices = geometry.Vertices;
                _drawHandle!.RenderInRenderTarget(
                    activeResources.ShadowMask!,
                    _drawShadowMask,
                    null);

                if (geometry.HasInsideMask)
                {
                    SetLightQuad(light, geometry.InsideBounds, softness);
                    _currentContributionShader = GetContributionShader(
                        light,
                        activeResources,
                        softness,
                        false,
                        geometry.HasOccluderMask);
                    _drawHandle.RenderInRenderTarget(
                        activeResources.Contribution!,
                        _drawContribution,
                        null);
                    hasNormalShadows = true;
                }

                if (geometry.HasOutsideMask)
                {
                    if (!outsidePassInitialized)
                    {
                        activeResources.EnsureOutsideSize(_clyde, viewport.LightRenderTarget.Size);
                        _drawHandle.RenderInRenderTarget(
                            activeResources.OutsideContribution!,
                            EmptyDraw,
                            Color.Black);
                        outsidePassInitialized = true;
                    }

                    SetLightQuad(light, geometry.OutsideBounds, softness);
                    _currentContributionShader = GetContributionShader(
                        light,
                        activeResources,
                        softness,
                        true,
                        geometry.HasOccluderMask);
                    _drawHandle.RenderInRenderTarget(
                        activeResources.OutsideContribution!,
                        _drawContribution,
                        null);
                    hasOutsideShadows = true;
                }
            }
        }

        if (reuseInsideContributionOutside)
            hasOutsideShadows = hasNormalShadows;

        if (resources == null)
        {
            ClearDrawState();
            return;
        }

        if (_system.LightBlur)
        {
            if (hasNormalShadows)
                _clyde.BlurRenderTarget(viewport, resources.Contribution!, resources.Blur!, eye, 14f);

            if (hasOutsideShadows && !reuseInsideContributionOutside)
                _clyde.BlurRenderTarget(viewport, resources.OutsideContribution!, resources.Blur!, eye, 14f);
        }

        SetCompositeFovParameters();

        if ((hasNormalShadows || hasOutsideShadows) && _protectedSpriteLayers.Count != 0)
            _drawHandle!.RenderInRenderTarget(viewport.LightRenderTarget, _drawProtectedSprites, null);

        if (hasNormalShadows)
        {
            _currentCompositeTexture = resources.Contribution!.Texture;
            _drawHandle!.RenderInRenderTarget(viewport.LightRenderTarget, _drawComposite, null);
        }

        if (hasOutsideShadows)
        {
            _currentCompositeTexture = reuseInsideContributionOutside
                ? resources.Contribution!.Texture
                : resources.OutsideContribution!.Texture;
            _drawHandle!.RenderInRenderTarget(viewport.LightRenderTarget, _drawOutsideComposite, null);
        }

        ClearDrawState();
    }

    private CachedResources BeginRenderPass(in OverlayDrawArgs args, IEye eye)
    {
        var viewport = args.Viewport;
        var resources = _resources.GetForViewport(viewport, static _ => new CachedResources());
        resources.EnsureSize(_clyde, viewport.LightRenderTarget.Size);
        resources.BeginFrame();

        _drawHandle = args.WorldHandle;
        _worldBounds = args.WorldBounds;

        var lightScale = viewport.LightRenderTarget.Size / (Vector2) viewport.Size;
        var scale = viewport.RenderScale / (Vector2.One / lightScale);
        var minimumLightScale = MathF.Max(MathF.Min(lightScale.X, lightScale.Y), GeometryEpsilon);
        _worldUnitsPerMaskPixel = MathF.Max(eye.Zoom.X, eye.Zoom.Y) /
            (EyeManager.PixelsPerMeter * minimumLightScale);
        _targetMatrix = resources.Contribution!.GetWorldToLocalMatrix(eye, scale);
        PrepareFovRenderParameters(lightScale);

        _drawHandle.RenderInRenderTarget(resources.Contribution, EmptyDraw, Color.Black);
        return resources;
    }

    private void PrepareLightRenderState(
        in LightData light,
        float softness,
        Box2 casterBounds)
    {
        _currentLightMask = light.MaskPath == null
            ? _whiteTexture
            : _resourceCache.GetResource<TextureResource>(light.MaskPath);

        var maskPadding = (1f + 3f * softness) * _worldUnitsPerMaskPixel;
        var radius = new Vector2(light.Radius);
        var lightBounds = new Box2(light.Position - radius, light.Position + radius);
        _currentMaskBounds = light.MaskPath == null
            ? casterBounds.Enlarged(maskPadding).Intersect(lightBounds).Enlarged(maskPadding)
            : lightBounds.Enlarged(maskPadding);
    }

    #endregion

    #region Cleanup

    private void ClearDrawState()
    {
        _drawHandle = null;
        _currentContributionShader = null;
        _currentCompositeTexture = null;
        _currentShadowMaskVertices = null;
        _currentLightMask = _whiteTexture;
        _localPlayerCaster = null;
        _renderLocalFovException = false;
    }

    protected override void DisposeBehavior()
    {
        _resources.Dispose();
        _outsideSubtractShader.Dispose();
        _stencilShader.Dispose();
        _subtractShader.Dispose();

        base.DisposeBehavior();
    }

    #endregion
}
