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
    private static readonly ProtoId<ShaderPrototype> StandardContributionShader = "ScpLightBatch";
    private static readonly ProtoId<ShaderPrototype> MaskShader = "ScpShadowMask";
    private static readonly ProtoId<ShaderPrototype> ProtectionShader = "ScpShadowProtection";

    #endregion

    #region Overlay configuration

    public override OverlaySpace Space => OverlaySpace.BeforeLighting;
    public const int ContentZIndex = LightBlurOverlay.ContentZIndex + 1;
    private const int GeometryBatchSize = ScpLightingBatchPlanner.GeometryBatchSize;

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
    private readonly ShaderPrototype _standardContributionPrototype;
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
    private CachedResources? _currentResources;
    private Vector2i _targetSize;
    private Matrix3x2 _targetMatrix;
    private Matrix3x2 _inverseTargetMatrix;
    private Vector2 _targetPixelScale;
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

        _contributionPrototype = _prototypes.Index(ContributionShader);
        _standardContributionPrototype = _prototypes.Index(StandardContributionShader);
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
        if (!_system.IsLightingViewport(args.Viewport) || !HasRenderableLights())
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
            using (_prof.IsEnabled || _prof.IsTracyEnabled
                       ? _prof.Group("ScpContentLighting.SpriteCache")
                       : (ProfManager.GroupGuard?) null)
            {
                BuildFrameCache(args.MapId, worldAabb);
                ApplyProjectionPositions();
            }
        }
        else
        {
            ClearFrameSpriteCache();
        }

        if (drawShadows)
        {
            using (_prof.IsEnabled || _prof.IsTracyEnabled
                       ? _prof.Group("ScpContentLighting.OccluderCache")
                       : (ProfManager.GroupGuard?) null)
            {
                BuildFrameOccluderCache(args.MapId, GetFrameOccluderQueryBounds(worldAabb));
            }
        }
        else
        {
            ClearFrameOccluderCache();
        }

        var resources = BeginRenderPass(args, eye, beforeResources.EnlargedLightTarget);
        _currentResources = resources;
        _currentDrawShadows = drawShadows;
        _currentHasProtection = false;

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
        BeginStandardLightBatches();

        if (!_currentDrawShadows)
        {
            for (var i = 0; i < lightCount; i++)
            {
                var light = _lights[i];
                if (light.Radius > 0f && light.Energy > 0f)
                    AddStandardLight(light);
            }

            DrawProfiledStandardLightBatches(resources);
            return;
        }

        for (var batchStart = 0; batchStart < lightCount; batchStart += GeometryBatchSize)
        {
            var batchCount = Math.Min(GeometryBatchSize, lightCount - batchStart);
            using (_prof.IsEnabled || _prof.IsTracyEnabled
                       ? _prof.Group("ScpContentLighting.Geometry")
                       : (ProfManager.GroupGuard?) null)
            {
                PrepareGeometryBatch(batchStart, batchCount, _currentDrawShadows);
            }

            DrawGeometryBatch(batchStart, batchCount, resources);
        }

        DrawProfiledStandardLightBatches(resources);
    }

    private void DrawProfiledStandardLightBatches(CachedResources resources)
    {
        using var contributionProfile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting.StandardLights")
            : (ProfManager.GroupGuard?) null;
        DrawStandardLightBatches(resources);
    }

    private CachedResources BeginRenderPass(
        in OverlayDrawArgs args,
        IEye eye,
        IRenderTexture lightTarget)
    {
        var viewport = args.Viewport;
        var resources = _resources.GetForViewport(viewport, static _ => new CachedResources());
        resources.SetSize(lightTarget.Size);
        resources.BeginFrame();

        _drawHandle = args.WorldHandle;
        _renderHandle = args.RenderHandle;
        _targetSize = lightTarget.Size;

        var lightScale = viewport.LightRenderTarget.Size / (Vector2) viewport.Size;
        var scale = viewport.RenderScale / (Vector2.One / lightScale);
        _targetMatrix = lightTarget.GetWorldToLocalMatrix(eye, scale);
        Matrix3x2.Invert(_targetMatrix, out _inverseTargetMatrix);
        _targetPixelScale = new Vector2(
            MathF.Sqrt(_targetMatrix.M11 * _targetMatrix.M11 + _targetMatrix.M21 * _targetMatrix.M21),
            MathF.Sqrt(_targetMatrix.M12 * _targetMatrix.M12 + _targetMatrix.M22 * _targetMatrix.M22));
        PrepareFovRenderParameters(viewport, lightScale);

        return resources;
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

    private bool HasRenderableLights()
    {
        for (var i = 0; i < _lights.Count; i++)
        {
            if (_lights[i].Radius > 0f && _lights[i].Energy > 0f)
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
        _currentResources = null;
        _targetSize = default;
        _targetPixelScale = default;
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
