using Robust.Client.GameStates;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterSystem
{
    [Dependency] private IClientGameStateManager _gameStateManager = default!;

    private readonly HashSet<EntityUid> _activeShadowCasterEntities = new(256);
    private readonly HashSet<EntityUid> _activeShadowForegroundEntities = new(64);
    private readonly Dictionary<NetEntity, DetachedSpriteKinds> _detachedSpriteEntities = new(320);
    private ushort _metadataNetId;

    internal HashSet<EntityUid> ActiveShadowCasterEntities => _activeShadowCasterEntities;
    internal HashSet<EntityUid> ActiveShadowForegroundEntities => _activeShadowForegroundEntities;

    private void InitializeSpriteIndex()
    {
        _metadataNetId = _componentFactory.GetRegistration(typeof(MetaDataComponent)).NetID ??
            throw new InvalidOperationException("MetaDataComponent must be networked.");

        SubscribeLocalEvent<ScpShadowCasterVisualsComponent, ComponentStartup>(OnShadowCasterStartup);
        SubscribeLocalEvent<ScpShadowCasterVisualsComponent, ComponentShutdown>(OnShadowCasterShutdown);
        SubscribeLocalEvent<ScpShadowCasterVisualsComponent, EntityPausedEvent>(OnShadowCasterPaused);
        SubscribeLocalEvent<ScpShadowCasterVisualsComponent, EntityUnpausedEvent>(OnShadowCasterUnpaused);

        SubscribeLocalEvent<ScpShadowForegroundVisualsComponent, ComponentStartup>(OnShadowForegroundStartup);
        SubscribeLocalEvent<ScpShadowForegroundVisualsComponent, ComponentShutdown>(OnShadowForegroundShutdown);
        SubscribeLocalEvent<ScpShadowForegroundVisualsComponent, EntityPausedEvent>(OnShadowForegroundPaused);
        SubscribeLocalEvent<ScpShadowForegroundVisualsComponent, EntityUnpausedEvent>(OnShadowForegroundUnpaused);
        _gameStateManager.GameStateApplied += OnSpriteIndexGameStateApplied;

        var casterQuery = EntityQueryEnumerator<ScpShadowCasterVisualsComponent>();
        while (casterQuery.MoveNext(out var uid, out _))
        {
            if (!IsPaused(uid))
                _activeShadowCasterEntities.Add(uid);
            else
                AddDetachedSpriteKind(uid, DetachedSpriteKinds.Caster);
        }

        var foregroundQuery = EntityQueryEnumerator<ScpShadowForegroundVisualsComponent>();
        while (foregroundQuery.MoveNext(out var uid, out _))
        {
            if (!IsPaused(uid))
                _activeShadowForegroundEntities.Add(uid);
            else
                AddDetachedSpriteKind(uid, DetachedSpriteKinds.Foreground);
        }
    }

    private void ShutdownSpriteIndex()
    {
        _gameStateManager.GameStateApplied -= OnSpriteIndexGameStateApplied;
        _activeShadowCasterEntities.Clear();
        _activeShadowForegroundEntities.Clear();
        _detachedSpriteEntities.Clear();
    }

    private void OnSpriteIndexGameStateApplied(GameStateAppliedArgs args)
    {
        // PVS detach deliberately bypasses EntityPausedEvent. Remove those
        // entities explicitly so the active sets do not grow with session history.
        for (var index = 0; index < args.Detached.Count; index++)
        {
            var netEntity = args.Detached[index];
            if (!TryGetEntity(netEntity, out var detached))
                continue;

            var kinds = DetachedSpriteKinds.None;
            if (_activeShadowCasterEntities.Remove(detached.Value))
                kinds |= DetachedSpriteKinds.Caster;
            if (_activeShadowForegroundEntities.Remove(detached.Value))
                kinds |= DetachedSpriteKinds.Foreground;

            if (kinds != DetachedSpriteKinds.None)
                _detachedSpriteEntities[netEntity] = kinds;
        }

        if (args.AppliedState.EntityStates.Value is not IReadOnlyList<EntityState> entityStates)
        {
            return;
        }

        // Re-entry is always represented by an EntityState, even when none of
        // the client-only marker components changed. One combined lookup keeps
        // this bounded by the applied state list and avoids scanning the whole
        // retained PVS history or issuing component queries for unrelated data.
        for (var index = 0; index < entityStates.Count; index++)
        {
            var entityState = entityStates[index];
            var netEntity = entityState.NetEntity;
            if (HasMetadataChange(entityState))
                ReconcileMetadataState(netEntity);

            if (!_detachedSpriteEntities.TryGetValue(netEntity, out var kinds) ||
                !TryGetEntity(netEntity, out var applied) ||
                IsPaused(applied.Value))
            {
                continue;
            }

            _detachedSpriteEntities.Remove(netEntity);
            if ((kinds & DetachedSpriteKinds.Caster) != 0)
            {
                UpdateSpriteIndexMembership<ScpShadowCasterVisualsComponent>(
                    applied.Value,
                    _activeShadowCasterEntities);
            }

            if ((kinds & DetachedSpriteKinds.Foreground) != 0)
            {
                UpdateSpriteIndexMembership<ScpShadowForegroundVisualsComponent>(
                    applied.Value,
                    _activeShadowForegroundEntities);
            }
        }
    }

    private bool HasMetadataChange(EntityState state)
    {
        if (state.ComponentChanges.Value is ComponentChange[] array)
        {
            for (var index = 0; index < array.Length; index++)
            {
                if (array[index].NetID == _metadataNetId)
                    return true;
            }
        }
        else if (state.ComponentChanges.Value is List<ComponentChange> list)
        {
            for (var index = 0; index < list.Count; index++)
            {
                if (list[index].NetID == _metadataNetId)
                    return true;
            }
        }

        return false;
    }

    private void ReconcileMetadataState(NetEntity netEntity)
    {
        if (!TryGetEntity(netEntity, out var entity) ||
            !TryComp(entity.Value, out MetaDataComponent? metadata))
        {
            return;
        }

        var kinds = GetSpriteKinds(entity.Value);
        var inactive = metadata.EntityPaused ||
            (metadata.Flags & MetaDataFlags.Detached) != 0;
        if (inactive)
        {
            _activeShadowCasterEntities.Remove(entity.Value);
            _activeShadowForegroundEntities.Remove(entity.Value);

            if (kinds == DetachedSpriteKinds.None)
                _detachedSpriteEntities.Remove(netEntity);
            else
                _detachedSpriteEntities[netEntity] = kinds;

            return;
        }

        _detachedSpriteEntities.Remove(netEntity);
        UpdateSpriteIndexMembership<ScpShadowCasterVisualsComponent>(
            entity.Value,
            _activeShadowCasterEntities);
        UpdateSpriteIndexMembership<ScpShadowForegroundVisualsComponent>(
            entity.Value,
            _activeShadowForegroundEntities);
    }

    private DetachedSpriteKinds GetSpriteKinds(EntityUid uid)
    {
        var kinds = DetachedSpriteKinds.None;
        if (HasComp<ScpShadowCasterVisualsComponent>(uid))
            kinds |= DetachedSpriteKinds.Caster;
        if (HasComp<ScpShadowForegroundVisualsComponent>(uid))
            kinds |= DetachedSpriteKinds.Foreground;
        return kinds;
    }

    internal void RemoveDeletedSpriteIndexEntity(EntityUid uid, NetEntity netEntity)
    {
        _activeShadowCasterEntities.Remove(uid);
        _activeShadowForegroundEntities.Remove(uid);
        _detachedSpriteEntities.Remove(netEntity);
    }

    private void UpdateSpriteIndexMembership<TComponent>(EntityUid uid, HashSet<EntityUid> active)
        where TComponent : Component
    {
        if (HasComp<TComponent>(uid))
            active.Add(uid);
        else
            active.Remove(uid);
    }

    private void AddDetachedSpriteKind(EntityUid uid, DetachedSpriteKinds kind)
    {
        if (!TryComp(uid, out MetaDataComponent? metadata) ||
            (metadata.Flags & MetaDataFlags.Detached) == 0)
        {
            return;
        }

        _detachedSpriteEntities.TryGetValue(metadata.NetEntity, out var kinds);
        _detachedSpriteEntities[metadata.NetEntity] = kinds | kind;
    }

    private void RemoveDetachedSpriteKind(EntityUid uid, DetachedSpriteKinds kind)
    {
        if (!TryComp(uid, out MetaDataComponent? metadata) ||
            !_detachedSpriteEntities.TryGetValue(metadata.NetEntity, out var kinds))
        {
            return;
        }

        kinds &= ~kind;
        if (kinds == DetachedSpriteKinds.None)
            _detachedSpriteEntities.Remove(metadata.NetEntity);
        else
            _detachedSpriteEntities[metadata.NetEntity] = kinds;
    }

    private void OnShadowCasterStartup(
        EntityUid uid,
        ScpShadowCasterVisualsComponent component,
        ComponentStartup args)
    {
        if (!IsPaused(uid))
            _activeShadowCasterEntities.Add(uid);
        else
            AddDetachedSpriteKind(uid, DetachedSpriteKinds.Caster);
    }

    private void OnShadowCasterShutdown(
        EntityUid uid,
        ScpShadowCasterVisualsComponent component,
        ComponentShutdown args)
    {
        _activeShadowCasterEntities.Remove(uid);
        RemoveDetachedSpriteKind(uid, DetachedSpriteKinds.Caster);
    }

    private void OnShadowCasterPaused(
        EntityUid uid,
        ScpShadowCasterVisualsComponent component,
        ref EntityPausedEvent args)
    {
        _activeShadowCasterEntities.Remove(uid);
    }

    private void OnShadowCasterUnpaused(
        EntityUid uid,
        ScpShadowCasterVisualsComponent component,
        ref EntityUnpausedEvent args)
    {
        _activeShadowCasterEntities.Add(uid);
    }

    private void OnShadowForegroundStartup(
        EntityUid uid,
        ScpShadowForegroundVisualsComponent component,
        ComponentStartup args)
    {
        if (!IsPaused(uid))
            _activeShadowForegroundEntities.Add(uid);
        else
            AddDetachedSpriteKind(uid, DetachedSpriteKinds.Foreground);
    }

    private void OnShadowForegroundShutdown(
        EntityUid uid,
        ScpShadowForegroundVisualsComponent component,
        ComponentShutdown args)
    {
        _activeShadowForegroundEntities.Remove(uid);
        RemoveDetachedSpriteKind(uid, DetachedSpriteKinds.Foreground);
    }

    private void OnShadowForegroundPaused(
        EntityUid uid,
        ScpShadowForegroundVisualsComponent component,
        ref EntityPausedEvent args)
    {
        _activeShadowForegroundEntities.Remove(uid);
    }

    private void OnShadowForegroundUnpaused(
        EntityUid uid,
        ScpShadowForegroundVisualsComponent component,
        ref EntityUnpausedEvent args)
    {
        _activeShadowForegroundEntities.Add(uid);
    }

    [Flags]
    private enum DetachedSpriteKinds : byte
    {
        None = 0,
        Caster = 1 << 0,
        Foreground = 1 << 1,
    }
}
