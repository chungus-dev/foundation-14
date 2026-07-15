using System.Numerics;
using Content.Client._Scp.Graphics.Shaders.FieldOfView;
using Content.Client.Graphics;
using Content.Client.Light;
using Content.Shared._Scp.Vision.FOV;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Graphics;
using Robust.Shared.Profiling;
using Robust.Shared.Prototypes;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Replaces the native point-light pass and adds visual-only sprite shadows.
/// </summary>
public sealed partial class ScpShadowCasterOverlay : Overlay
{
    #region Shader prototypes

    private static readonly ProtoId<ShaderPrototype> ContributionShader = "ScpShadowLightContribution";
    private static readonly ProtoId<ShaderPrototype> MaskShader = "ScpShadowMask";
    private static readonly ProtoId<ShaderPrototype> ProtectionShader = "ScpShadowProtection";

    #endregion

    #region Overlay configuration

    public override OverlaySpace Space => OverlaySpace.BeforeLighting;
    public const int ContentZIndex = LightBlurOverlay.ContentZIndex + 1;

    #endregion

    #region Dependencies

    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private ILightManager _lightManager = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ProfManager _prof = default!;

    private readonly FieldOfViewOverlayManagementSystem _fieldOfViewManagement;
    private readonly FieldOfViewSystem _fieldOfViewSystem;
    private readonly OccluderSystem _occluderSystem;
    private readonly SpriteSystem _spriteSystem;
    private readonly SpriteTreeSystem _spriteTree;
    private readonly SharedTransformSystem _transformSystem;
    private readonly ScpShadowCasterSystem _system;

    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<FieldOfViewOccludableComponent> _fovOccludableQuery;
    private readonly EntityQuery<ScpShadowForegroundVisualsComponent> _foregroundQuery;
    private readonly EntityQuery<ScpShadowCasterVisualsComponent> _shadowQuery;

    #endregion

    #region Render state

    private readonly ShaderPrototype _contributionPrototype;
    private readonly ShaderInstance _maskShader;
    private readonly ShaderInstance _protectionShader;
    private readonly Texture _whiteTexture;
    private readonly OverlayResourceCache<CachedResources> _resources = new();
    private readonly ScpShadowContourCache _contourCache;

    private readonly Action _drawShadowMask;
    private readonly Action _drawLights;
    private readonly Action _drawProtectionMask;

    private DrawingHandleWorld? _drawHandle;
    private IRenderHandle? _renderHandle;
    private List<DrawVertexUV2DColor>? _currentShadowMaskVertices;
    private ShaderInstance? _currentContributionShader;
    private CachedResources? _currentResources;
    private Texture _currentLightMask;
    private Box2 _currentMaskBounds;
    private float _worldUnitsPerMaskPixel;
    private Matrix3x2 _targetMatrix;
    private Angle _eyeRotation;
    private bool _currentDrawShadows;
    private bool _currentHasProtection;

    #endregion

    public ScpShadowCasterOverlay(ScpShadowCasterSystem system)
    {
        ZIndex = ContentZIndex;
        IoCManager.InjectDependencies(this);

        _system = system;
        _lights = system.ViewportLights;

        _fieldOfViewManagement = _entityManager.System<FieldOfViewOverlayManagementSystem>();
        _fieldOfViewSystem = _entityManager.System<FieldOfViewSystem>();
        _occluderSystem = _entityManager.System<OccluderSystem>();
        _spriteSystem = _entityManager.System<SpriteSystem>();
        _spriteTree = _entityManager.System<SpriteTreeSystem>();
        _transformSystem = _entityManager.System<SharedTransformSystem>();

        _occluderQuery = _entityManager.GetEntityQuery<OccluderComponent>();
        _fovOccludableQuery = _entityManager.GetEntityQuery<FieldOfViewOccludableComponent>();
        _foregroundQuery = _entityManager.GetEntityQuery<ScpShadowForegroundVisualsComponent>();
        _shadowQuery = _entityManager.GetEntityQuery<ScpShadowCasterVisualsComponent>();

        _contourCache = system.ContourCache;
        _lightGeometryJob = new LightGeometryJob(this);
        _whiteTexture = Texture.White;
        _currentLightMask = _whiteTexture;

        _contributionPrototype = _prototypes.Index(ContributionShader);
        _maskShader = _prototypes.Index(MaskShader).Instance();
        _protectionShader = _prototypes.Index(ProtectionShader).Instance();
        InitializeLightQuad();

        _drawShadowMask = DrawShadowMask;
        _drawLights = DrawLights;
        _drawProtectionMask = DrawProtectionMask;
    }

    #region Main render pass

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_system.IsLightingViewport(args.Viewport) || _lights.Count == 0)
            return;

        var eye = args.Viewport.Eye;
        if (eye == null)
            return;

        using var profile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting")
            : (ProfManager.GroupGuard?) null;

        _eyeRotation = eye.Rotation;
        PrepareFovContext(args, eye);

        var beforeOverlay = _overlayManager.GetOverlay<BeforeLightTargetOverlay>();
        var beforeResources = beforeOverlay.GetCachedForViewport(args.Viewport);
        var worldBounds = beforeOverlay.EnlargedBounds;
        var worldAabb = worldBounds.CalcBoundingBox();

        var drawShadows = _lightManager.DrawShadows && HasShadowCastingLights();
        var querySprites = drawShadows &&
            (_system.MobQuality != ScpShadowQuality.Disabled ||
             _system.ObjectQuality != ScpShadowQuality.Disabled);

        if (querySprites)
        {
            BuildFrameCache(args.MapId, worldAabb);
            ApplyProjectionPositions();
        }
        else
        {
            ClearFrameSpriteCache();
        }

        if (drawShadows)
            BuildFrameOccluderCache(args.MapId, GetFrameOccluderQueryBounds(worldAabb));
        else
            ClearFrameOccluderCache();

        var resources = BeginRenderPass(args, eye, beforeResources.EnlargedLightTarget);
        var hasProtection = drawShadows &&
            _frameCasters.Count != 0 &&
            _protectedSpriteLayers.Count != 0;

        if (hasProtection)
        {
            _drawHandle!.RenderInRenderTarget(
                resources.ProtectionMask!,
                _drawProtectionMask,
                Color.Black);
        }

        _currentResources = resources;
        _currentDrawShadows = drawShadows;
        _currentHasProtection = hasProtection;

        try
        {
            // Keep the enlarged target bound for the complete point-light pass. Only
            // shadow-mask draws temporarily switch away from it.
            _drawHandle!.RenderInRenderTarget(
                beforeResources.EnlargedLightTarget,
                _drawLights,
                null);
        }
        finally
        {
            ClearDrawState();
        }
    }

    private void DrawLights()
    {
        var resources = _currentResources!;
        var lightCount = _lights.Count;
        var geometryBatchSize = _system.GeometryBatchSize;
        for (var batchStart = 0; batchStart < lightCount; batchStart += geometryBatchSize)
        {
            var batchCount = Math.Min(geometryBatchSize, lightCount - batchStart);
            PrepareGeometryBatch(batchStart, batchCount, _currentDrawShadows);

            for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                var light = _lights[batchStart + batchIndex];
                if (light.Radius <= 0f || light.Energy <= 0f)
                    continue;

                var geometry = _lightGeometryBuffers[batchIndex];
                var hasShadowMask = _currentDrawShadows && light.CastShadows && geometry.HasMask;
                var softness = hasShadowMask ? GetLightSoftness(light) : 0f;

                PrepareLightRenderState(light, softness);
                SetLightQuad(light);

                if (hasShadowMask)
                {
                    _currentShadowMaskVertices = geometry.Vertices;
                    ClearAndDrawShadowMask(resources);
                }

                _currentContributionShader = GetContributionShader(
                    light,
                    resources,
                    softness,
                    hasShadowMask,
                    _currentHasProtection && geometry.HasCasterMask);
                DrawContribution();
            }
        }
    }

    private CachedResources BeginRenderPass(
        in OverlayDrawArgs args,
        IEye eye,
        IRenderTexture lightTarget)
    {
        var viewport = args.Viewport;
        var resources = _resources.GetForViewport(viewport, static _ => new CachedResources());
        resources.EnsureSize(_clyde, lightTarget.Size);
        resources.BeginFrame();

        _drawHandle = args.WorldHandle;
        _renderHandle = args.RenderHandle;

        var lightScale = viewport.LightRenderTarget.Size / (Vector2) viewport.Size;
        var scale = viewport.RenderScale / (Vector2.One / lightScale);
        var minimumLightScale = MathF.Max(MathF.Min(lightScale.X, lightScale.Y), GeometryEpsilon);
        _worldUnitsPerMaskPixel = MathF.Max(eye.Zoom.X, eye.Zoom.Y) /
            (EyeManager.PixelsPerMeter * minimumLightScale);
        _targetMatrix = lightTarget.GetWorldToLocalMatrix(eye, scale);
        PrepareFovRenderParameters(viewport, lightScale);

        return resources;
    }

    private void PrepareLightRenderState(in ScpShadowLightData light, float softness)
    {
        _currentLightMask = light.Mask ?? _whiteTexture;

        var padding = (1f + 3f * softness) * _worldUnitsPerMaskPixel;
        var radius = new Vector2(light.Radius);
        _currentMaskBounds = new Box2(
            light.Position - radius,
            light.Position + radius).Enlarged(padding);
    }

    private bool HasShadowCastingLights()
    {
        for (var i = 0; i < _lights.Count; i++)
        {
            if (_lights[i].CastShadows &&
                _lights[i].Radius > 0f &&
                _lights[i].Energy > 0f)
                return true;
        }

        return false;
    }

    #endregion

    #region Cleanup

    private void ClearDrawState()
    {
        _drawHandle = null;
        _renderHandle = null;
        _currentContributionShader = null;
        _currentResources = null;
        _currentShadowMaskVertices = null;
        _currentLightMask = _whiteTexture;
        _currentDrawShadows = false;
        _currentHasProtection = false;
        _localPlayerCaster = null;
        _renderLocalFovException = false;
    }

    protected override void DisposeBehavior()
    {
        _resources.Dispose();

        base.DisposeBehavior();
    }

    #endregion
}
