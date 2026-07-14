using System.Numerics;
using Content.Client.Clickable;
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
    private static readonly ProtoId<ShaderPrototype> UnshadedShader = "unshaded";
    private static readonly Action EmptyDraw = static () => { };

    #endregion

    #region Overlay configuration

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowWorld;

    private ScpShadowQuality _mobQuality;
    private ScpShadowQuality _objectQuality;

    #endregion

    #region Dependencies

    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IClickMapManager _clickMaps = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private ILightManager _lightManager = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IResourceCache _resourceCache = default!;

    private readonly LightTreeSystem _lightTree;
    private readonly OccluderSystem _occluderSystem;
    private readonly SpriteSystem _spriteSystem;
    private readonly SpriteTreeSystem _spriteTree;
    private readonly SharedTransformSystem _transformSystem;

    private readonly EntityQuery<MapComponent> _mapQuery;
    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<ScpShadowCasterVisualsComponent> _shadowQuery;

    #endregion

    #region Render state

    private readonly ShaderInstance _contributionShader;
    private readonly ShaderInstance _subtractShader;
    private readonly ShaderInstance _unshadedShader;
    private readonly OverlayResourceCache<CachedResources> _resources = new();
    private readonly ScpShadowContourCache _contourCache;

    private readonly Action _drawCasterMask;
    private readonly Action _drawOccluderMask;
    private readonly Action _drawContribution;
    private readonly Action _drawComposite;

    private DrawingHandleWorld? _drawHandle;
    private CachedResources? _currentResources;
    private Texture _currentLightMask = Texture.White;
    private Matrix3x2 _targetMatrix;
    private Box2Rotated _worldBounds;
    private Angle _eyeRotation;

    #endregion

    public ScpShadowCasterOverlay()
    {
        IoCManager.InjectDependencies(this);

        _lightTree = _entityManager.System<LightTreeSystem>();
        _occluderSystem = _entityManager.System<OccluderSystem>();
        _spriteSystem = _entityManager.System<SpriteSystem>();
        _spriteTree = _entityManager.System<SpriteTreeSystem>();
        _transformSystem = _entityManager.System<SharedTransformSystem>();

        _mapQuery = _entityManager.GetEntityQuery<MapComponent>();
        _occluderQuery = _entityManager.GetEntityQuery<OccluderComponent>();
        _shadowQuery = _entityManager.GetEntityQuery<ScpShadowCasterVisualsComponent>();

        _contourCache = new ScpShadowContourCache(_clickMaps);

        _contributionShader = _prototypes.Index(ContributionShader).InstanceUnique();
        _subtractShader = _prototypes.Index(SubtractShader).InstanceUnique();
        _unshadedShader = _prototypes.Index(UnshadedShader).Instance();

        _drawCasterMask = DrawCasterMask;
        _drawOccluderMask = DrawOccluderMask;
        _drawContribution = DrawContribution;
        _drawComposite = DrawComposite;
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

        var lightCount = GatherLights(args.MapId, args.WorldBounds, args.WorldAABB);
        if (lightCount == 0)
            return;

        var resources = _resources.GetForViewport(viewport, static _ => new CachedResources());
        resources.EnsureSize(_clyde, viewport.LightRenderTarget.Size);

        _drawHandle = args.WorldHandle;
        _currentResources = resources;
        _worldBounds = args.WorldBounds;
        _eyeRotation = eye.Rotation;

        var lightScale = viewport.LightRenderTarget.Size / (Vector2) viewport.Size;
        var scale = viewport.RenderScale / (Vector2.One / lightScale);
        _targetMatrix = resources.Contribution!.GetWorldToLocalMatrix(eye, scale);

        _drawHandle.RenderInRenderTarget(resources.Contribution, EmptyDraw, Color.Black);

        var anyShadows = false;
        for (var i = 0; i < lightCount; i++)
        {
            var light = _lights[i];
            if (light.Component.Radius <= 0f || light.Component.Energy <= 0f)
                continue;

            BuildCasterMask(light, args.MapId);
            if (_casterVertices.Count == 0)
                continue;

            _drawHandle.RenderInRenderTarget(resources.CasterMask!, _drawCasterMask, Color.Black);

            BuildOccluderMask(light, args.MapId);
            _drawHandle.RenderInRenderTarget(resources.OccluderMask!, _drawOccluderMask, Color.Black);

            SetContributionParameters(light, resources);
            SetLightQuad(light);
            _currentLightMask = light.Component.MaskPath == null
                ? Texture.White
                : _resourceCache.GetResource<TextureResource>(light.Component.MaskPath);

            _drawHandle.RenderInRenderTarget(resources.Contribution, _drawContribution, null);
            anyShadows = true;
        }

        if (!anyShadows)
        {
            ClearDrawState();
            return;
        }

        if (_configuration.GetCVar(CVars.LightBlur))
            _clyde.BlurRenderTarget(viewport, resources.Contribution, resources.Blur!, eye, 14f);

        _drawHandle.RenderInRenderTarget(viewport.LightRenderTarget, _drawComposite, null);
        ClearDrawState();
    }

    private void ReadQualitySettings()
    {
        _mobQuality = ClampQuality(_configuration.GetCVar(ScpCCVars.MobShadowQuality));
        _objectQuality = ClampQuality(_configuration.GetCVar(ScpCCVars.ObjectShadowQuality));
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
        _currentLightMask = Texture.White;
    }

    protected override void DisposeBehavior()
    {
        _resources.Dispose();
        _contributionShader.Dispose();
        _subtractShader.Dispose();

        base.DisposeBehavior();
    }

    #endregion
}
