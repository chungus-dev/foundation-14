using System.Diagnostics.CodeAnalysis;
using Content.Shared.Construction;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using JetBrains.Annotations;

namespace Content.Server._Scp.Construction.Completions;

[UsedImplicitly, DataDefinition]
public sealed partial class DamageEntityAllTypes : IGraphAction
{
    [DataField]
    public FixedPoint2 Amount;

    public void PerformAction(EntityUid uid, EntityUid? userUid, IEntityManager entityManager)
    {
        if (!TryGetDamage(uid, entityManager, out var damage))
            return;

        entityManager.System<DamageableSystem>().TryChangeDamage(uid, damage, ignoreResistances: true, origin: userUid);
    }

    private bool TryGetDamage(EntityUid uid, IEntityManager entityManager, [NotNullWhen(true)] out DamageSpecifier? damageSpecifier)
    {
        damageSpecifier = null;

        if (!entityManager.TryGetComponent<DamageableComponent>(uid, out var damageable))
            return false;

        var damageableSystem = entityManager.System<DamageableSystem>();
        var currentDamage = damageableSystem.GetAllDamage((uid, damageable));

        damageSpecifier = new DamageSpecifier();
        foreach (var key in currentDamage.DamageDict.Keys)
        {
            damageSpecifier.DamageDict[key] = Amount;
        }

        return true;
    }
}
