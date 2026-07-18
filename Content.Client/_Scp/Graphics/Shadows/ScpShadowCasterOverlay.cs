using System.Numerics;
using Content.Client._Scp.Graphics.Shaders.FieldOfView;
using Content.Client.Graphics;
using Content.Client.Light;
using Content.Shared._Scp.Vision.FOV;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Profiling;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

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
    private static readonly ProtoId<ShaderPrototype> AtlasClearShader = "ScpShadowAtlasClear";
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
    private readonly SharedMapSystem _mapSystem;
    private readonly SpriteSystem _spriteSystem;
    private readonly SpriteTreeSystem _spriteTree;
    private readonly SharedTransformSystem _transformSystem;
    private readonly ScpShadowCasterSystem _system;

    private readonly EntityQuery<OccluderComponent> _occluderQuery;
    private readonly EntityQuery<FieldOfViewOccludableComponent> _fovOccludableQuery;
    private readonly EntityQuery<ScpShadowForegroundVisualsComponent> _foregroundQuery;
    private readonly EntityQuery<ScpShadowCasterVisualsComponent> _shadowQuery;
    private readonly EntityQuery<MetaDataComponent> _metadataQuery;
    private readonly EntityQuery<SpriteComponent> _spriteQuery;
    private readonly EntityQuery<TransformComponent> _transformQuery;

    #endregion

    #region Render state

    private readonly ShaderPrototype _contributionPrototype;
    private readonly ShaderPrototype _standardContributionPrototype;
    private readonly ShaderInstance _maskShader;
    private readonly ShaderInstance _atlasClearShader;
    private readonly ShaderInstance _protectionShader;
    private readonly Texture _whiteTexture;
    private readonly OverlayResourceCache<CachedResources> _resources = new();
    private readonly HashSet<CachedResources> _cachedResources = new();
    private readonly Func<IClydeViewport, CachedResources> _createCachedResources;
    private readonly ScpShadowContourCache _contourCache;
    private List<Entity<MapGridComponent>> _intersectingTreeGrids = new(4);

    private readonly Action _drawShadowMask;
    private readonly Action _drawLights;
    private readonly Action _drawProtectionMask;

    private DrawingHandleWorld? _drawHandle;
    private IRenderHandle? _renderHandle;
    private CachedResources? _currentResources;
    private MapId _currentMapId = MapId.Nullspace;
    private Vector2i _targetSize;
    private Matrix3x2 _targetMatrix;
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
        _mapSystem = _entityManager.System<SharedMapSystem>();
        _spriteSystem = _entityManager.System<SpriteSystem>();
        _spriteTree = _entityManager.System<SpriteTreeSystem>();
        _transformSystem = _entityManager.System<SharedTransformSystem>();

        _occluderQuery = _entityManager.GetEntityQuery<OccluderComponent>();
        _fovOccludableQuery = _entityManager.GetEntityQuery<FieldOfViewOccludableComponent>();
        _foregroundQuery = _entityManager.GetEntityQuery<ScpShadowForegroundVisualsComponent>();
        _shadowQuery = _entityManager.GetEntityQuery<ScpShadowCasterVisualsComponent>();
        _metadataQuery = _entityManager.GetEntityQuery<MetaDataComponent>();
        _spriteQuery = _entityManager.GetEntityQuery<SpriteComponent>();
        _transformQuery = _entityManager.GetEntityQuery<TransformComponent>();

        _contourCache = system.ContourCache;
        _createCachedResources = CreateCachedResources;
        _lightGeometryJob = new LightGeometryJob(this);
        _atlasGeometryJob = new AtlasGeometryJob(this);
        _whiteTexture = Texture.White;

        _contributionPrototype = _prototypes.Index(ContributionShader);
        _standardContributionPrototype = _prototypes.Index(StandardContributionShader);
        _maskShader = _prototypes.Index(MaskShader).Instance();
        _atlasClearShader = _prototypes.Index(AtlasClearShader).Instance();
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

        var beforeOverlay = _overlayManager.GetOverlay<BeforeLightTargetOverlay>();
        var beforeResources = beforeOverlay.GetCachedForViewport(args.Viewport);
        var resources = _resources.GetForViewport(args.Viewport, _createCachedResources);
        var geometrySnapshots = resources.GetGeometrySnapshots(args.MapId);

        var drawShadows = _lightManager.DrawShadows && HasShadowCastingLights();
        var querySprites = drawShadows &&
            (_system.MobQuality != ScpShadowQuality.Disabled ||
             _system.ObjectQuality != ScpShadowQuality.Disabled);

        // The standard-light shader does not use caster/FOV state. Keep the
        // shadows-disabled menu path out of all shadow-only viewport work.
        Box2 worldAabb = default;
        if (drawShadows)
        {
            _eyeRotation = eye.Rotation;
            PrepareFovContext(args, eye);
            worldAabb = beforeOverlay.EnlargedBounds.CalcBoundingBox();
        }

        if (querySprites)
        {
            using (_prof.IsEnabled || _prof.IsTracyEnabled
                       ? _prof.Group("ScpContentLighting.SpriteCache")
                       : (ProfManager.GroupGuard?) null)
            {
                BuildFrameCache(args.MapId, worldAabb, resources, geometrySnapshots);
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

        BeginRenderPass(
            args,
            eye,
            beforeResources.EnlargedLightTarget,
            drawShadows,
            resources);
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

        using (_prof.IsEnabled || _prof.IsTracyEnabled
                   ? _prof.Group("ScpContentLighting.Geometry")
                   : (ProfManager.GroupGuard?) null)
        {
            BindGeometryBuffers(resources, lightCount);
            PrepareGeometryBatch(0, lightCount, _currentDrawShadows);
        }
        resources.PruneGeometryCache(_system.MaxShadowLights);
        DrawGeometryBatch(0, lightCount, resources);

        DrawProfiledStandardLightBatches(resources);
    }

    private void DrawProfiledStandardLightBatches(CachedResources resources)
    {
        using var contributionProfile = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpContentLighting.StandardLights")
            : (ProfManager.GroupGuard?) null;
        DrawStandardLightBatches(resources);
    }

    internal void RemovePointLight(EntityUid owner, GameTick creationTick)
    {
        var identity = new PersistentLightIdentity(owner, creationTick);
        foreach (var resources in _cachedResources)
            resources.RemovePointLight(identity);
    }

    internal void RemoveGeometrySource(EntityUid owner, NetEntity netIdentity)
    {
        var key = new ScpGeometryEntityKey(owner, netIdentity);
        foreach (var resources in _cachedResources)
            resources.RemoveGeometrySource(key);
    }

    private CachedResources CreateCachedResources(IClydeViewport viewport)
    {
        var resources = new CachedResources(OnCachedResourcesDisposed);
        _cachedResources.Add(resources);
        return resources;
    }

    private void OnCachedResourcesDisposed(CachedResources resources)
    {
        _cachedResources.Remove(resources);
    }

    private void BeginRenderPass(
        in OverlayDrawArgs args,
        IEye eye,
        IRenderTexture lightTarget,
        bool prepareShadowParameters,
        CachedResources resources)
    {
        var viewport = args.Viewport;
        resources.SetSize(lightTarget.Size);
        resources.BeginFrame();

        _drawHandle = args.WorldHandle;
        _renderHandle = args.RenderHandle;
        _currentMapId = args.MapId;
        _targetSize = lightTarget.Size;

        var lightScale = viewport.LightRenderTarget.Size / (Vector2) viewport.Size;
        var scale = viewport.RenderScale / (Vector2.One / lightScale);
        _targetMatrix = lightTarget.GetWorldToLocalMatrix(eye, scale);
        if (prepareShadowParameters)
        {
            _targetPixelScale = new Vector2(
                MathF.Sqrt(_targetMatrix.M11 * _targetMatrix.M11 + _targetMatrix.M21 * _targetMatrix.M21),
                MathF.Sqrt(_targetMatrix.M12 * _targetMatrix.M12 + _targetMatrix.M22 * _targetMatrix.M22));
            PrepareFovRenderParameters(viewport, lightScale);
        }

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
        // These are frame aliases into viewport-owned cache entries. Keeping them
        // here would retain a disposed viewport's CPU cache until another draw.
        _lightGeometryBuffers.Clear();
        _atlasLights.Clear();
        _atlasPages.Clear();

        _drawHandle = null;
        _renderHandle = null;
        _currentResources = null;
        _targetSize = default;
        _targetPixelScale = default;
        _currentDrawShadows = false;
        _currentHasProtection = false;
        _localPlayerCaster = null;
        _directionalEye = null;
        _directionalFov = null;
        _directionalTransform = null;
        _directionalFovActive = false;
        _renderLocalFovException = false;
    }

    protected override void DisposeBehavior()
    {
        _resources.Dispose();
        _cachedResources.Clear();

        base.DisposeBehavior();
    }

    #endregion
}

internal readonly record struct PersistentLightIdentity(EntityUid Owner, GameTick CreationTick)
    : IComparable<PersistentLightIdentity>
{
    public int CompareTo(PersistentLightIdentity other)
    {
        var comparison = Owner.CompareTo(other.Owner);
        return comparison != 0 ? comparison : CreationTick.CompareTo(other.CreationTick);
    }
}
