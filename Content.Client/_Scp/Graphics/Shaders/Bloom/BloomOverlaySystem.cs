using Content.Shared._Scp.Vision.Proximity;
using Content.Shared._Scp.ScpCCVars;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Maths;
using Robust.Shared.Profiling;
using Robust.Shared.Prototypes;

namespace Content.Client._Scp.Graphics.Shaders.Bloom;

/// <summary>
/// Система, управляющая эффектом свечения.
/// Высчитывает, какие сущности будут иметь эффект и передает в оверлеи.
/// </summary>
public sealed partial class LightingOverlaySystem : EntitySystem
{
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private ProximitySystem _proximity = default!;
    [Dependency] private SpriteSystem _sprite = default!;
    [Dependency] private ProfManager _prof = default!;

    [Dependency] private EntityQuery<EyeComponent> _eyeQuery;
    [Dependency] private EntityQuery<TransformComponent> _transformQuery;

    private ConeLightingOverlay _cone = default!;
    private PointLightingOverlay _point = default!;
    private readonly List<BloomOverlayEntry> _entities = [];

    private static readonly ProtoId<ShaderPrototype> Shader = "LightingOverlay";

    private bool _allEnabled;
    private bool _coneEnabled;
    private bool _optimizationsEnabled;

    private float _strength;

    private ConfigurationMultiSubscriptionBuilder _configSub = default!;

    public override void Initialize()
    {
        base.Initialize();

        _cone = new ConeLightingOverlay(ProtoMan, _sprite, _prof, Shader, _entities);
        _point = new PointLightingOverlay(ProtoMan, _sprite, _prof, Shader, _entities);

        _configSub = _cfg.SubscribeMultiple()
            .OnValueChanged(ScpCCVars.LightBloomEnable, OnAllEnabledChanged, true)
            .OnValueChanged(ScpCCVars.LightBloomConeEnable, OnConeEnabledChanged, true)
            .OnValueChanged(ScpCCVars.LightBloomConeOpacity, x => _cone.Opacity = x, true)
            .OnValueChanged(ScpCCVars.LightBloomOptimizations, b => _optimizationsEnabled = b, true)
            .OnValueChanged(ScpCCVars.LightBloomStrength, OnStrengthChanged, true);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_player.LocalEntity == null)
            return;

        if (!_allEnabled)
            return;

        using var gatherGroup = _prof.IsEnabled || _prof.IsTracyEnabled
            ? _prof.Group("ScpLightBloom.Gather")
            : (ProfManager.GroupGuard?) null;

        _entities.Clear();

        var drawFov = _eyeQuery.TryComp(_player.LocalEntity.Value, out var eye) && eye.DrawFov;

        // Просчитываем, какие сущности будут иметь эффект свечения и будет ли это видно игроку.
        // Если сущность проходит проверки -> добавляем ее в список и отправляем список в оверлеи.
        var query = AllEntityQuery<BloomOverlayVisualsComponent, PointLightComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var pointLight, out var xform))
        {
            if (!pointLight.Enabled)
                continue;

            // Оптимизации слабых видеокарт. Если источник не виден игроку -> не добавляем в список рендера.
            // Опционально, так как приводит к резкому падению FPS из-за сложности проверок на видимость.
            if (_optimizationsEnabled
                && drawFov
                && !_proximity.IsRightType(_player.LocalEntity.Value, uid, LineOfSightBlockerLevel.Transparent, out _))
                continue;

            var (worldPos, worldRotation) = _transform.GetWorldPositionRotation(xform, _transformQuery);
            _entities.Add(new BloomOverlayEntry(xform, worldPos, worldRotation, pointLight.Color));
        }
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _overlayManager.RemoveOverlay(_cone);
        _cone.Dispose();

        _overlayManager.RemoveOverlay(_point);
        _point.Dispose();

        _configSub.Dispose();
    }

    private void OnAllEnabledChanged(bool value)
    {
        _allEnabled = value;
        _cone.Enabled = value && _coneEnabled;
        _point.Enabled = value;

        ToggleOverlay(_cone.Enabled, _cone);
        ToggleOverlay(_point.Enabled, _point);
    }

    private void OnConeEnabledChanged(bool value)
    {
        _coneEnabled = value;
        _cone.Enabled = value && _allEnabled;

        ToggleOverlay(_cone.Enabled, _cone);
    }

    private void OnStrengthChanged(float value)
    {
        _strength = Math.Clamp(value, 0.1f, 1f);

        _cone.Strength = _strength;
        _point.Strength = _strength;
    }

    private void ToggleOverlay(bool value, Overlay overlay)
    {
        var hasOverlay = _overlayManager.HasOverlay(overlay.GetType());

        if (value && !hasOverlay)
            _overlayManager.AddOverlay(overlay);
        else if (!value && hasOverlay)
            _overlayManager.RemoveOverlay(overlay);
    }
}

internal readonly record struct BloomOverlayEntry(
    TransformComponent Transform,
    System.Numerics.Vector2 WorldPosition,
    Angle WorldRotation,
    Color Color);
