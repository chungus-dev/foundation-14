using System.Numerics;
using Content.Client.Clickable;
using Content.Client._Scp.Graphics.Shaders.FieldOfView;
using Content.Client.Graphics;
using Content.Shared._Scp.Vision.FOV;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
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
    [Dependency] private IClickMapManager _clickMaps = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
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

    private readonly EntityQuery<MapComponent> _mapQuery;
    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<FieldOfViewOccludableComponent> _fovOccludableQuery;
    private readonly EntityQuery<ScpShadowForegroundVisualsComponent> _foregroundQuery;
    private readonly EntityQuery<ScpShadowCasterVisualsComponent> _shadowQuery;

    #endregion

    #region Render state

    private readonly ShaderPrototype _contributionPrototype;
    private readonly ShaderInstance _outsideSubtractShader;
    private readonly ShaderInstance _stencilShader;
    private readonly ShaderInstance _subtractShader;
    private readonly ShaderInstance _unshadedShader;
    private readonly Texture _whiteTexture;
    private readonly OverlayResourceCache<CachedResources> _resources = new();
    private readonly ScpShadowContourCache _contourCache;

    private readonly Action _drawCasterMask;
    private readonly Action _drawOutsideCasterMask;
    private readonly Action _drawOccluderMask;
    private readonly Action _drawContribution;
    private readonly Action _drawComposite;
    private readonly Action _drawOutsideComposite;
    private readonly Action _drawProtectedSprites;

    private DrawingHandleWorld? _drawHandle;
    private ShaderInstance? _currentContributionShader;
    private Texture? _currentCompositeTexture;
    private Texture _currentLightMask;
    private Box2 _currentMaskBounds;
    private float _worldUnitsPerMaskPixel;
    private Matrix3x2 _targetMatrix;
    private Box2Rotated _worldBounds;
    private Angle _eyeRotation;

    #endregion

    public ScpShadowCasterOverlay()
    {
        ZIndex = ContentZIndex;
        IoCManager.InjectDependencies(this);

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

        _contourCache = new ScpShadowContourCache(_clickMaps);
        _whiteTexture = Texture.White;
        _currentLightMask = _whiteTexture;

        _contributionPrototype = _prototypes.Index(ContributionShader);
        _subtractShader = _prototypes.Index(SubtractShader).InstanceUnique();
        _outsideSubtractShader = _prototypes.Index(SubtractShader).InstanceUnique();
        _stencilShader = _prototypes.Index(StencilShader).InstanceUnique();
        _unshadedShader = _prototypes.Index(UnshadedShader).Instance();

        ConfigureStencilShaders();
        SubscribeConfiguration();
        InitializeLightQuad();

        _drawCasterMask = DrawCasterMask;
        _drawOutsideCasterMask = DrawOutsideCasterMask;
        _drawOccluderMask = DrawOccluderMask;
        _drawContribution = DrawContribution;
        _drawComposite = DrawComposite;
        _drawOutsideComposite = DrawOutsideComposite;
        _drawProtectedSprites = DrawProtectedSprites;
    }

    #region Main render pass

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_mobQuality == ScpShadowQuality.Disabled && _objectQuality == ScpShadowQuality.Disabled)
            return;

        var viewport = args.Viewport;
        var eye = viewport.Eye;

        if (eye == null ||
            args.MapId == MapId.Nullspace ||
            !_lightManager.Enabled ||
            !_lightManager.DrawLighting ||
            !_lightManager.DrawShadows ||
            !eye.DrawLight ||
            !_mapQuery.TryGetComponent(args.MapUid, out var map) ||
            !map.LightingEnabled)
        {
            return;
        }

        using var profile = _prof.Group("ScpVisualShadows");

        PrepareFovContext(args, eye);
        var lightCount = GatherLights(args.MapId, args.WorldBounds, args.WorldAABB);
        if (lightCount == 0)
            return;

        var frameQueryBounds = GetFrameQueryBounds(args.WorldAABB);
        _eyeRotation = eye.Rotation;

        BuildFrameCache(args.MapId, frameQueryBounds, args.WorldAABB);
        if (_frameCasters.Count == 0)
        {
            ClearDrawState();
            return;
        }

        ApplyProjectionPositions();
        var needsOutsideContribution = _directionalFovActive && _hasOutsideFovCasters;
        CachedResources? resources = null;
        var hasNormalShadows = false;
        var hasOutsideShadows = false;
        var outsidePassInitialized = false;

        for (var i = 0; i < lightCount; i++)
        {
            var light = _lights[i];
            if (light.Component.Radius <= 0f || light.Component.Energy <= 0f)
                continue;

            var softness = GetLightSoftness(light);
            var occluderMaskReady = false;
            var lightRenderStateReady = false;

            BuildCasterMasks(light, needsOutsideContribution);
            if (_casterVertices.Count != 0)
            {
                var activeResources = resources ??= BeginRenderPass(args, eye);
                PrepareLightRenderState(light, softness);
                lightRenderStateReady = true;
                RenderCasterContribution(
                    light,
                    activeResources,
                    activeResources.Contribution!,
                    softness,
                    false,
                    ref occluderMaskReady);
                hasNormalShadows = true;
            }

            if (needsOutsideContribution)
            {
                if (_outsideCasterVertices.Count != 0)
                {
                    var activeResources = resources ??= BeginRenderPass(args, eye);
                    if (!lightRenderStateReady)
                    {
                        PrepareLightRenderState(light, softness);
                        lightRenderStateReady = true;
                    }

                    if (!outsidePassInitialized)
                    {
                        activeResources.EnsureOutsideSize(_clyde, viewport.LightRenderTarget.Size);
                        _drawHandle!.RenderInRenderTarget(
                            activeResources.OutsideContribution!,
                            EmptyDraw,
                            Color.Black);
                        outsidePassInitialized = true;
                    }

                    RenderCasterContribution(
                        light,
                        activeResources,
                        activeResources.OutsideContribution!,
                        softness,
                        true,
                        ref occluderMaskReady);
                    hasOutsideShadows = true;
                }
            }
        }

        if (resources == null)
        {
            ClearDrawState();
            return;
        }

        if (_lightBlur)
        {
            if (hasNormalShadows)
                _clyde.BlurRenderTarget(viewport, resources.Contribution!, resources.Blur!, eye, 14f);

            if (hasOutsideShadows)
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
            _currentCompositeTexture = resources.OutsideContribution!.Texture;
            _drawHandle!.RenderInRenderTarget(viewport.LightRenderTarget, _drawOutsideComposite, null);
        }

        ClearDrawState();
    }

    private CachedResources BeginRenderPass(in OverlayDrawArgs args, IEye eye)
    {
        BuildFrameOccluderCache(args.MapId, GetFrameOccluderQueryBounds(args.WorldAABB));

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

    private void PrepareLightRenderState(in LightData light, float softness)
    {
        SetLightQuad(light);
        _currentLightMask = light.Component.MaskPath == null
            ? _whiteTexture
            : _resourceCache.GetResource<TextureResource>(light.Component.MaskPath);

        var maskPadding = (1f + 3f * softness) * _worldUnitsPerMaskPixel;
        var radius = new Vector2(light.Component.Radius);
        _currentMaskBounds = new Box2(light.Position - radius, light.Position + radius)
            .Enlarged(maskPadding);
    }

    private void RenderCasterContribution(
        in LightData light,
        CachedResources resources,
        IRenderTexture contributionTarget,
        float softness,
        bool outsideFovContribution,
        ref bool occluderMaskReady)
    {
        var drawMask = outsideFovContribution ? _drawOutsideCasterMask : _drawCasterMask;
        _drawHandle!.RenderInRenderTarget(resources.CasterMask!, drawMask, null);

        if (!occluderMaskReady)
        {
            BuildOccluderMask(light);
            _drawHandle.RenderInRenderTarget(resources.OccluderMask!, _drawOccluderMask, null);
            occluderMaskReady = true;
        }

        _currentContributionShader = GetContributionShader(
            light,
            resources,
            softness);
        _drawHandle.RenderInRenderTarget(contributionTarget, _drawContribution, null);
    }

    #endregion

    #region Cleanup

    private void ClearDrawState()
    {
        _drawHandle = null;
        _currentContributionShader = null;
        _currentCompositeTexture = null;
        _currentLightMask = _whiteTexture;
        _localPlayerCaster = null;
        _renderLocalFovException = false;
    }

    protected override void DisposeBehavior()
    {
        _configurationSubscription.Dispose();
        _resources.Dispose();
        _outsideSubtractShader.Dispose();
        _stencilShader.Dispose();
        _subtractShader.Dispose();

        base.DisposeBehavior();
    }

    #endregion
}
