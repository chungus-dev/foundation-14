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
using Robust.Shared.Map.Components;
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
    private const int ContentZIndex = LightBlurOverlay.ContentZIndex + 1;
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
    private readonly SharedMapSystem _mapSystem;
    private readonly SpriteSystem _spriteSystem;
    private readonly SpriteTreeSystem _spriteTree;
    private readonly SharedTransformSystem _transformSystem;
    private readonly ScpShadowCasterSystem _system;

    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<FieldOfViewOccludableComponent> _fovOccludableQuery;
    private readonly EntityQuery<ScpShadowProtectedTextureVisualsComponent> _protectedTextureQuery;
    private readonly EntityQuery<ScpShadowCasterVisualsComponent> _shadowQuery;
    private readonly EntityQuery<OccluderTreeComponent> _occluderTreeQuery;
    private readonly EntityQuery<SpriteTreeComponent> _spriteTreeQuery;
    private readonly EntityQuery<MapComponent> _mapQuery;

    private BeforeLightTargetOverlay? _beforeLightTargetOverlay;

    #endregion

    #region Render state

    private readonly ShaderPrototype _contributionPrototype;
    private readonly ShaderPrototype _standardContributionPrototype;
    private readonly ShaderInstance _maskShader;
    private readonly ShaderInstance _protectionShader;
    private readonly Texture _blackTexture;
    private readonly Texture _whiteTexture;
    private readonly OverlayResourceCache<CachedResources> _resources = new();
    private readonly ScpShadowContourCache _contourCache;
    private List<Entity<MapGridComponent>> _intersectingTreeGrids = new(4);

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

        _fieldOfViewManagement = _entityManager.System<FieldOfViewOverlayManagementSystem>();
        _fieldOfViewSystem = _entityManager.System<FieldOfViewSystem>();
        _occluderSystem = _entityManager.System<OccluderSystem>();
        _mapSystem = _entityManager.System<SharedMapSystem>();
        _spriteSystem = _entityManager.System<SpriteSystem>();
        _spriteTree = _entityManager.System<SpriteTreeSystem>();
        _transformSystem = _entityManager.System<SharedTransformSystem>();

        _occluderQuery = _entityManager.GetEntityQuery<OccluderComponent>();
        _fovOccludableQuery = _entityManager.GetEntityQuery<FieldOfViewOccludableComponent>();
        _protectedTextureQuery = _entityManager.GetEntityQuery<ScpShadowProtectedTextureVisualsComponent>();
        _shadowQuery = _entityManager.GetEntityQuery<ScpShadowCasterVisualsComponent>();
        _occluderTreeQuery = _entityManager.GetEntityQuery<OccluderTreeComponent>();
        _spriteTreeQuery = _entityManager.GetEntityQuery<SpriteTreeComponent>();
        _mapQuery = _entityManager.GetEntityQuery<MapComponent>();

        _contourCache = system.ContourCache;
        _lightGeometryJob = new LightGeometryJob(this);
        _blackTexture = Texture.Black;
        _whiteTexture = Texture.White;

        _contributionPrototype = _prototypes.Index(ContributionShader);
        _standardContributionPrototype = _prototypes.Index(StandardContributionShader);
        _maskShader = _prototypes.Index(MaskShader).InstanceUnique();
        _protectionShader = _prototypes.Index(ProtectionShader).Instance();
        InitializeLightQuad();

        _drawProtectionMask = DrawProtectionMask;
    }

    #region Main render pass

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (!_system.IsLightingViewport(args.Viewport) || !HasRenderableLights())
            return false;

        var eye = args.Viewport.Eye;
        if (eye == null)
            return false;

        if (_beforeLightTargetOverlay == null)
        {
            if (!_overlayManager.TryGetOverlay(out _beforeLightTargetOverlay))
                return false;
        }

        return true;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var eye = args.Viewport.Eye!;

        using var profile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting")
            : (ProfManager.GroupGuard?) null;

        var drawShadows = _lightManager.DrawShadows && HasShadowCastingLights();

        if (drawShadows)
            PrepareShadows(in args, eye);
        else
            ClearFrameOccluderCache();

        var beforeResources = _beforeLightTargetOverlay!.GetCachedForViewport(args.Viewport);
        var resources = BeginRenderPass(args, eye, beforeResources.EnlargedLightTarget, drawShadows);
        _currentResources = resources;
        _currentDrawShadows = drawShadows;
        _currentHasProtection = false;

        try
        {
            // Keep the enlarged target bound for the complete point-light pass. Only
            // shadow-mask draws temporarily switch away from it.
            _drawHandle!.RenderInRenderTarget(
                beforeResources.EnlargedLightTarget,
                DrawLights,
                null);
        }
        finally
        {
            ClearDrawState();

            var mapComp = _mapQuery.CompOrNull(args.MapUid);
            mapComp?.LightingEnabled = true;
        }
    }

    private void PrepareShadows(in OverlayDrawArgs args, IEye eye)
    {
        var querySprites = _system.MobQuality != ScpShadowQuality.Disabled
                              || _system.ObjectQuality != ScpShadowQuality.Disabled;

        _eyeRotation = eye.Rotation;
        PrepareFovContext(args);
        var worldAabb = _beforeLightTargetOverlay!.EnlargedBounds.CalcBoundingBox();

        if (querySprites)
        {
            using (_prof.IsEnabled || _prof.IsTracyEnabled
                       ? _prof.Group("ScpContentLighting.SpriteCache")
                       : (ProfManager.GroupGuard?) null)
            {
                BuildFrameCache(args.MapId, worldAabb);
                ApplyAlphaProjectionPositions();
            }
        }
        else
        {
            ClearFrameSpriteCache();
        }

        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.OccluderCache")
                   : (ProfManager.GroupGuard?) null)
        {
            BuildFrameOccluderCache(args.MapId, GetFrameOccluderQueryBounds(worldAabb));
        }
    }

    private void DrawLights()
    {
        var resources = _currentResources!;
        var lightCount = _system.ViewportLights.Count;
        BeginStandardLightBatches();

        if (!_currentDrawShadows)
        {
            for (var i = 0; i < lightCount; i++)
            {
                var light = _system.ViewportLights[i];
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
        IRenderTexture lightTarget,
        bool prepareShadowParameters)
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
        if (prepareShadowParameters)
        {
            Matrix3x2.Invert(_targetMatrix, out _inverseTargetMatrix);
            _targetPixelScale = new Vector2(
                MathF.Sqrt(_targetMatrix.M11 * _targetMatrix.M11 + _targetMatrix.M21 * _targetMatrix.M21),
                MathF.Sqrt(_targetMatrix.M12 * _targetMatrix.M12 + _targetMatrix.M22 * _targetMatrix.M22));
            PrepareFovRenderParameters(viewport, lightScale);
        }

        return resources;
    }

    private bool HasShadowCastingLights()
    {
        foreach (var light in _system.ViewportLights)
        {
            if (light.CastShadows &&
                light.Radius > 0f &&
                light.Energy > 0f)
                return true;
        }

        return false;
    }

    private bool HasRenderableLights()
    {
        foreach (var light in _system.ViewportLights)
        {
            if (light.Radius > 0f && light.Energy > 0f)
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
        _maskShader.Dispose();
        _protectionShader.Dispose();

        base.DisposeBehavior();
    }

    #endregion
}
