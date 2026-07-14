using System.Numerics;
using Content.Client.Clickable;
using Content.Client._Scp.Graphics.Shaders.FieldOfView;
using Content.Client.Graphics;
using Content.Shared._Scp.ScpCCVars;
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
    private const float MaskClearMarginPixels = 13f; // Three 4 px softness taps plus linear filtering.

    #endregion

    #region Overlay configuration

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowWorld;
    public const int ContentZIndex = int.MinValue;

    private ScpShadowQuality _mobQuality;
    private ScpShadowQuality _objectQuality;
    private bool _localPlayerShadowOutsideFov;

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
    private readonly LightTreeSystem _lightTree;
    private readonly OccluderSystem _occluderSystem;
    private readonly SpriteSystem _spriteSystem;
    private readonly SpriteTreeSystem _spriteTree;
    private readonly SharedTransformSystem _transformSystem;

    private readonly EntityQuery<MapComponent> _mapQuery;
    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<ScpShadowForegroundVisualsComponent> _foregroundQuery;
    private readonly EntityQuery<ScpShadowCasterVisualsComponent> _shadowQuery;

    #endregion

    #region Render state

    private readonly ShaderPrototype _contributionPrototype;
    private readonly ShaderInstance _localStencilShader;
    private readonly ShaderInstance _localSubtractShader;
    private readonly ShaderInstance _stencilShader;
    private readonly ShaderInstance _subtractShader;
    private readonly ShaderInstance _unshadedShader;
    private readonly Texture _whiteTexture;
    private readonly OverlayResourceCache<CachedResources> _resources = new();
    private readonly ScpShadowContourCache _contourCache;

    private readonly Action _drawCasterMask;
    private readonly Action _drawOccluderMask;
    private readonly Action _drawContribution;
    private readonly Action _drawComposite;
    private readonly Action _drawLocalComposite;
    private readonly Action _drawLocalShadowStencil;
    private readonly Action _drawProtectedSprites;

    private DrawingHandleWorld? _drawHandle;
    private CachedResources? _currentResources;
    private ShaderInstance? _currentContributionShader;
    private Texture _currentLightMask;
    private Box2 _currentMaskBounds;
    private float _maskClearPadding;
    private Matrix3x2 _targetMatrix;
    private Box2Rotated _worldBounds;
    private Angle _eyeRotation;

    #endregion

    public ScpShadowCasterOverlay()
    {
        ZIndex = ContentZIndex;
        IoCManager.InjectDependencies(this);

        _fieldOfViewManagement = _entityManager.System<FieldOfViewOverlayManagementSystem>();
        _lightTree = _entityManager.System<LightTreeSystem>();
        _occluderSystem = _entityManager.System<OccluderSystem>();
        _spriteSystem = _entityManager.System<SpriteSystem>();
        _spriteTree = _entityManager.System<SpriteTreeSystem>();
        _transformSystem = _entityManager.System<SharedTransformSystem>();

        _mapQuery = _entityManager.GetEntityQuery<MapComponent>();
        _occluderQuery = _entityManager.GetEntityQuery<OccluderComponent>();
        _foregroundQuery = _entityManager.GetEntityQuery<ScpShadowForegroundVisualsComponent>();
        _shadowQuery = _entityManager.GetEntityQuery<ScpShadowCasterVisualsComponent>();

        _contourCache = new ScpShadowContourCache(_clickMaps);
        _whiteTexture = Texture.White;
        _currentLightMask = _whiteTexture;

        _contributionPrototype = _prototypes.Index(ContributionShader);
        _subtractShader = _prototypes.Index(SubtractShader).InstanceUnique();
        _localSubtractShader = _prototypes.Index(SubtractShader).InstanceUnique();
        _stencilShader = _prototypes.Index(StencilShader).InstanceUnique();
        _localStencilShader = _prototypes.Index(StencilShader).InstanceUnique();
        _unshadedShader = _prototypes.Index(UnshadedShader).Instance();

        ConfigureStencilShaders();

        _drawCasterMask = DrawCasterMask;
        _drawOccluderMask = DrawOccluderMask;
        _drawContribution = DrawContribution;
        _drawComposite = DrawComposite;
        _drawLocalComposite = DrawLocalComposite;
        _drawLocalShadowStencil = DrawLocalShadowStencil;
        _drawProtectedSprites = DrawProtectedSprites;
    }

    #region Main render pass

    protected override void Draw(in OverlayDrawArgs args)
    {
        ReadQualitySettings();
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

        BuildFrameOccluderCache(args.MapId, GetFrameOccluderQueryBounds(args.WorldAABB));
        lightCount = FilterVisibleLights();
        if (lightCount == 0)
            return;

        var frameQueryBounds = GetFrameQueryBounds(args.WorldAABB);

        var resources = _resources.GetForViewport(viewport, static _ => new CachedResources());
        resources.EnsureSize(_clyde, viewport.LightRenderTarget.Size);
        resources.BeginFrame();

        _drawHandle = args.WorldHandle;
        _currentResources = resources;
        _worldBounds = args.WorldBounds;
        _eyeRotation = eye.Rotation;

        var lightScale = viewport.LightRenderTarget.Size / (Vector2) viewport.Size;
        var scale = viewport.RenderScale / (Vector2.One / lightScale);
        var minimumLightScale = MathF.Max(MathF.Min(lightScale.X, lightScale.Y), GeometryEpsilon);
        _maskClearPadding = MaskClearMarginPixels * MathF.Max(eye.Zoom.X, eye.Zoom.Y) /
            (EyeManager.PixelsPerMeter * minimumLightScale);
        _targetMatrix = resources.Contribution!.GetWorldToLocalMatrix(eye, scale);
        PrepareFovRenderParameters(viewport, lightScale);

        BuildFrameCache(args.MapId, frameQueryBounds);
        if (_frameCasters.Count == 0)
        {
            ClearDrawState();
            return;
        }

        _drawHandle.RenderInRenderTarget(resources.Contribution, EmptyDraw, Color.Black);
        _localShadowVertices.Clear();

        var anyShadows = false;
        for (var i = 0; i < lightCount; i++)
        {
            var light = _lights[i];
            if (light.Component.Radius <= 0f || light.Component.Energy <= 0f)
                continue;

            SetLightQuad(light);
            _currentLightMask = light.Component.MaskPath == null
                ? _whiteTexture
                : _resourceCache.GetResource<TextureResource>(light.Component.MaskPath);
            var radius = new Vector2(light.Component.Radius);
            _currentMaskBounds = new Box2(light.Position - radius, light.Position + radius)
                .Enlarged(_maskClearPadding);

            var occluderMaskReady = false;
            var excludedCaster = _renderLocalFovException ? _localPlayerCaster : null;

            if (light.DirectionalVisibility > 0f)
            {
                BuildCasterMask(light, excludedCaster, null);
                if (_casterVertices.Count != 0)
                {
                    RenderCasterContribution(
                        light,
                        resources,
                        light.DirectionalVisibility,
                        false,
                        ref occluderMaskReady);
                    anyShadows = true;
                }
            }

            if (_renderLocalFovException && _localPlayerCaster is { } localPlayer)
            {
                BuildCasterMask(light, null, localPlayer);
                if (_casterVertices.Count != 0)
                {
                    _localShadowVertices.AddRange(_casterVertices);
                    RenderCasterContribution(light, resources, 1f, true, ref occluderMaskReady);
                    anyShadows = true;
                }
            }
        }


        if (!anyShadows)
        {
            ClearDrawState();
            return;
        }

        if (_configuration.GetCVar(CVars.LightBlur))
            _clyde.BlurRenderTarget(viewport, resources.Contribution, resources.Blur!, eye, 14f);

        SetCompositeFovParameters();

        if (_renderLocalFovException && _localShadowVertices.Count != 0)
            _drawHandle.RenderInRenderTarget(viewport.LightRenderTarget, _drawLocalShadowStencil, null);

        if (_protectedSpriteLayers.Count != 0)
            _drawHandle.RenderInRenderTarget(viewport.LightRenderTarget, _drawProtectedSprites, null);

        _drawHandle.RenderInRenderTarget(viewport.LightRenderTarget, _drawComposite, null);

        if (_renderLocalFovException && _localShadowVertices.Count != 0)
            _drawHandle.RenderInRenderTarget(viewport.LightRenderTarget, _drawLocalComposite, null);

        ClearDrawState();
    }

    private void RenderCasterContribution(
        in LightData light,
        CachedResources resources,
        float visibility,
        bool localContribution,
        ref bool occluderMaskReady)
    {
        _drawHandle!.RenderInRenderTarget(resources.CasterMask!, _drawCasterMask, null);

        if (!occluderMaskReady)
        {
            BuildOccluderMask(light);
            _drawHandle.RenderInRenderTarget(resources.OccluderMask!, _drawOccluderMask, null);
            occluderMaskReady = true;
        }

        _currentContributionShader = GetContributionShader(
            light,
            resources,
            visibility,
            localContribution);
        _drawHandle.RenderInRenderTarget(resources.Contribution!, _drawContribution, null);
    }

    private void ReadQualitySettings()
    {
        _mobQuality = ClampQuality(_configuration.GetCVar(ScpCCVars.MobShadowQuality));
        _objectQuality = ClampQuality(_configuration.GetCVar(ScpCCVars.ObjectShadowQuality));
        _localPlayerShadowOutsideFov = _configuration.GetCVar(ScpCCVars.LocalPlayerShadowOutsideFov);
    }

    private static ScpShadowQuality ClampQuality(int value)
    {
        return (ScpShadowQuality) Math.Clamp(
            value,
            (int) ScpShadowQuality.Disabled,
            (int) ScpShadowQuality.Sprite);
    }

    #endregion

    #region Cleanup

    private void ClearDrawState()
    {
        _drawHandle = null;
        _currentResources = null;
        _currentContributionShader = null;
        _currentLightMask = _whiteTexture;
        _localPlayerCaster = null;
        _renderLocalFovException = false;
    }

    protected override void DisposeBehavior()
    {
        _resources.Dispose();
        _localStencilShader.Dispose();
        _localSubtractShader.Dispose();
        _stencilShader.Dispose();
        _subtractShader.Dispose();

        base.DisposeBehavior();
    }

    #endregion
}
