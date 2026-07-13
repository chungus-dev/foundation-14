using Content.Shared.Damage.Components;
using Content.Shared.Rejuvenate;

namespace Content.Shared.Damage.Systems;

public abstract partial class SharedStaminaSystem
{
    /// <summary>
    /// Copy-paste of <see cref="OnRejuvenate"/> but without requirement to <see cref="RejuvenateEvent"/>.
    /// </summary>
    public bool TryHealAllStaminaDamage(Entity<StaminaComponent?> entity)
    {
        if (!Resolve(entity, ref entity.Comp, false))
            return false;

        if (entity.Comp.StaminaDamage >= entity.Comp.CritThreshold)
        {
            ExitStamCrit(entity, entity.Comp);
        }

        entity.Comp.StaminaDamage = 0;
        AdjustStatus(entity.Owner);
        RemComp<ActiveStaminaComponent>(entity);
        _status.TryRemoveStatusEffect(entity, StaminaLow);
        UpdateStaminaVisuals(entity!);
        Dirty(entity);

        return true;
    }
}
