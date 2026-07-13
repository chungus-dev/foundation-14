using Content.Shared._Scp.Anomaly.Scp096.Main.Components;
using Content.Shared._Scp.Graphics.Shaders.SinCity;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs.Systems;
using Content.Shared.Prying.Components;
using Content.Shared.Tag;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Components;

namespace Content.Shared._Scp.Anomaly.Scp096.Main.Systems;

public abstract partial class SharedScp096System
{
    /*
     * Часть системы, отвечающая за состояние без лица скромника.
     */

    [Dependency] private TagSystem _tag = default!;
    [Dependency] private MobThresholdSystem _mobThreshold = default!;
    [Dependency] private MeleeThrowOnHitSystem _meleeThrowOnHit = default!;

    private void InitializeWithoutFace()
    {
        SubscribeLocalEvent<ActiveScp096WithoutFaceComponent, ComponentStartup>(OnWithoutFaceStartup);
        SubscribeLocalEvent<ActiveScp096WithoutFaceComponent, ComponentShutdown>(OnWithoutFaceShutdown);
    }

    /// <summary>
    /// Переводит скромника в состояние содранного лица, выдавая нужные модификаторы и компоненты.
    /// </summary>
    private void OnWithoutFaceStartup(Entity<ActiveScp096WithoutFaceComponent> ent, ref ComponentStartup args)
    {
        _holding.TryForceBreakOut(ent.Owner);

        var message = Loc.GetString("scp096-face-skin-rip-full", ("name", Identity.Name(ent, EntityManager)));
        _popup.PopupPredicted(message, ent, ent);

        _audio.PlayPredicted(ent.Comp.StartSound, ent, ent);
        UpdateAudio(ent.Owner, ent.Comp.AmbientSound);

        RemComp<SinCityOverlayComponent>(ent);
        EnsureComp<Scp096ShaderWithoutFaceComponent>(ent);

        RefreshSpeedModifiers(ent.Owner);
        TryToggleTearsReagent(ent.Owner, false);

        // Устанавливаем параметры атаки для режима содранного лица
        if (TryComp<MeleeWeaponComponent>(ent, out var melee))
        {
            ent.Comp.CachedDamage = melee.Damage;
            ent.Comp.CachedAttackRate = melee.AttackRate;
            ent.Comp.CachedStaminaDamageFactor = melee.BluntStaminaDamageFactor;

            melee.AttackRate = ent.Comp.AttackRate;
            melee.Damage = ent.Comp.Damage;
            melee.BluntStaminaDamageFactor = ent.Comp.StaminaDamageFactor;
            Dirty(ent, melee);
        }

        if (TryComp<MeleeThrowOnHitComponent>(ent, out var throwOnHit))
        {
            ent.Comp.CachedThrowSpeed = throwOnHit.Speed;
            ent.Comp.CachedThrowDistance = throwOnHit.Distance;

            _meleeThrowOnHit.TrySetThrowParameters(
                (ent.Owner, throwOnHit),
                speed: ent.Comp.ThrowSpeed,
                distance: ent.Comp.ThrowDistance);
        }

        var prying = EnsureComp<PryingComponent>(ent);
        prying.Enabled = true;
        Dirty(ent, prying);

        Dirty(ent);
        _tag.AddTags(ent, ent.Comp.TagsToAdd);
    }

    /// <summary>
    /// Возвращает скромника в стандартное состояние из состояния содранного лица
    /// </summary>
    private void OnWithoutFaceShutdown(Entity<ActiveScp096WithoutFaceComponent> ent, ref ComponentShutdown args)
    {
        var message = Loc.GetString("scp096-face-healed", ("name", Identity.Name(ent, EntityManager)));
        _popup.PopupPredicted(message, ent, ent);

        _audio.PlayPredicted(ent.Comp.ShutdownSound, ent, ent);
        UpdateAudio(ent.Owner);

        EnsureComp<SinCityOverlayComponent>(ent);
        RemComp<Scp096ShaderWithoutFaceComponent>(ent);

        TryToggleTearsReagent(ent.Owner, true);
        RefreshSpeedModifiers(ent.Owner, true);
        TryHealFace(ent);
        ActualizeAlert(ent);

        // Возвращаем стандартные параметры атаки
        if (TryComp<MeleeWeaponComponent>(ent, out var melee))
        {
            if (ent.Comp.CachedAttackRate != null)
                melee.AttackRate = ent.Comp.CachedAttackRate.Value;

            if (ent.Comp.CachedDamage != null)
                melee.Damage = ent.Comp.CachedDamage;

            if (ent.Comp.CachedStaminaDamageFactor != null)
                melee.BluntStaminaDamageFactor = ent.Comp.CachedStaminaDamageFactor.Value;

            Dirty(ent, melee);
        }

        if (TryComp<MeleeThrowOnHitComponent>(ent, out var throwOnHit))
        {
            if (ent.Comp.CachedThrowSpeed != null)
                _meleeThrowOnHit.TrySetThrowParameters((ent.Owner, throwOnHit), speed: ent.Comp.CachedThrowSpeed.Value);

            if (ent.Comp.CachedThrowDistance != null)
                _meleeThrowOnHit.TrySetThrowParameters((ent.Owner, throwOnHit), distance: ent.Comp.CachedThrowDistance.Value);
        }

        var prying = EnsureComp<PryingComponent>(ent);
        prying.Enabled = false;
        Dirty(ent, prying);

        _tag.RemoveTags(ent, ent.Comp.TagsToAdd);
    }
}
