using Content.Shared.GameTicking;
using Content.Shared.StatusEffectNew;
using Content.Shared.Weather;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;

namespace Content.Shared._Scp.Facility.Weather;

/// <summary>
/// Prevents map-owned weather status effects from surviving inside a status-effect container while their map is being deleted.
/// </summary>
/// <remarks>
/// <para>
/// SCP facility maps can receive vanilla weather through the status-effect system. The weather effect is a separate
/// entity, but it is owned by the map as a status effect and is also present in the map's status-effect container.
/// </para>
/// <para>
/// During <c>restartroundnow</c> or a rapid forced map switch, Robust recursively deletes the map hierarchy. If the
/// weather effect is still registered in the status-effect container while recursive deletion detaches transforms, the
/// engine can observe an impossible intermediate state: the child has been detached to null-space, but the container
/// still reports it as contained. This used to surface as recursive delete exceptions, PVS attempts to send deleted
/// entities, or client dirty-system warnings about predicted deletion of networked weather entities.
/// </para>
/// <para>
/// This system handles only that narrow lifecycle problem. It tracks maps that currently have
/// <see cref="WeatherStatusEffectComponent"/> through status-effect application and removal events, then removes the
/// container link before map recursive deletion reaches the weather effect. It does not delete the weather entity
/// directly. Actual entity lifetime is left to the normal map deletion path.
/// </para>
/// <para>
/// The system intentionally does not subscribe to container insertion/removal events. Weather membership is tracked from
/// the status-effect API, because the gameplay-level contract is "this map has a weather status effect", not "some
/// entity was inserted into a container".
/// </para>
/// </remarks>
public abstract partial class SharedScpWeatherSystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;

    /// <summary>
    /// Maps that are known to currently or recently have SCP-relevant weather status effects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately a map set rather than a weather-entity set. The cleanup operation needs to ask the
    /// status-effect system for the current weather effects on the map at the moment cleanup happens, because weather
    /// can be replaced or removed before the round restart path runs.
    /// </para>
    /// <para>
    /// Entries are pruned when the last weather effect is removed, when the map terminates, and during explicit tracked
    /// cleanup if the map has already been deleted or no longer has weather effects. This prevents stale map references
    /// from surviving across round restarts.
    /// </para>
    /// </remarks>
    private readonly HashSet<EntityUid> _weatherMaps = [];

    /// <summary>
    /// Reused buffer for iterating <see cref="_weatherMaps"/> while allowing stale entries to be removed.
    /// </summary>
    private readonly List<EntityUid> _mapsToDetach = [];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<WeatherStatusEffectComponent, StatusEffectAppliedEvent>(OnWeatherApplied);
        SubscribeLocalEvent<WeatherStatusEffectComponent, StatusEffectRemovedEvent>(OnWeatherRemoved);
        SubscribeLocalEvent<MapComponent, EntityTerminatingEvent>(OnMapTerminating);
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
    }

    private void OnWeatherApplied(Entity<WeatherStatusEffectComponent> ent, ref StatusEffectAppliedEvent args)
    {
        if (HasComp<MapComponent>(args.Target))
            _weatherMaps.Add(args.Target);
    }

    private void OnWeatherRemoved(Entity<WeatherStatusEffectComponent> ent, ref StatusEffectRemovedEvent args)
    {
        if (!_statusEffects.TryEffectsWithComp<WeatherStatusEffectComponent>(args.Target, out _))
            _weatherMaps.Remove(args.Target);
    }

    private void OnMapTerminating(Entity<MapComponent> ent, ref EntityTerminatingEvent args)
    {
        DetachWeatherStatusEffects(ent);
        _weatherMaps.Remove(ent);
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        if (!_net.IsClient)
            return;

        DetachTrackedWeatherStatusEffects();
    }

    /// <summary>
    /// Detaches weather status effects from every tracked map and prunes maps that no longer need cleanup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client calls this from the round restart cleanup network event. That event arrives before the client applies
    /// the full restart state, which gives us a chance to break the status-effect container relationship without
    /// deleting networked entities locally.
    /// </para>
    /// <para>
    /// Deleted maps and maps that have already lost their weather effects are removed from the tracking set to avoid
    /// keeping stale entity references across restarts.
    /// </para>
    /// </remarks>
    private void DetachTrackedWeatherStatusEffects()
    {
        _mapsToDetach.Clear();

        foreach (var mapUid in _weatherMaps)
        {
            _mapsToDetach.Add(mapUid);
        }

        foreach (var mapUid in _mapsToDetach)
        {
            if (Deleted(mapUid) || !HasComp<MapComponent>(mapUid))
            {
                _weatherMaps.Remove(mapUid);
                continue;
            }

            DetachWeatherStatusEffects(mapUid);

            if (!_statusEffects.TryEffectsWithComp<WeatherStatusEffectComponent>(mapUid, out _))
                _weatherMaps.Remove(mapUid);
        }

        _mapsToDetach.Clear();
    }

    /// <summary>
    /// Removes the container relationship between a map and its weather status-effect entities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method intentionally calls <see cref="SharedContainerSystem.RemoveEntity"/> with <c>reparent: false</c>.
    /// We only need to clear container membership before recursive map deletion continues; reparenting or deleting the
    /// weather entity here would create extra lifecycle side effects and can trip client prediction checks.
    /// </para>
    /// <para>
    /// The map still owns the weather effect through normal entity hierarchy deletion after this point. In other words,
    /// this method fixes container bookkeeping only; it is not an alternate weather lifetime system.
    /// </para>
    /// </remarks>
    private void DetachWeatherStatusEffects(EntityUid mapUid)
    {
        if (!_statusEffects.TryEffectsWithComp<WeatherStatusEffectComponent>(mapUid, out var effects))
            return;

        foreach (var effect in effects)
        {
            // Weather status effects are transform children of the map through this container.
            // Removing only the container link lets map recursive deletion handle the entity itself.
            _container.RemoveEntity(mapUid, effect.Owner, reparent: false, force: true);
        }
    }
}
