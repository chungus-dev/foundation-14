using Content.Shared.Clothing.Components;
using Content.Shared.Inventory;
using Content.Shared.Movement.Events;

namespace Content.Shared.Clothing.EntitySystems;

public sealed class SpeedModifierContactCapClothingSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SpeedModifierContactCapClothingComponent, InventoryRelayedEvent<GetSpeedModifierContactCapEvent>>(OnGetMaxSlow);

        // Scp added start
        SubscribeLocalEvent<SpeedModifierContactCapClothingComponent, GetSpeedModifierContactCapEvent>(OnGetMaxSlowDirect);
        // Scp added end
    }

    private void OnGetMaxSlow(Entity<SpeedModifierContactCapClothingComponent> ent, ref InventoryRelayedEvent<GetSpeedModifierContactCapEvent> args)
    {
        args.Args.SetIfMax(ent.Comp.MaxContactSprintSlowdown, ent.Comp.MaxContactWalkSlowdown);
    }

    // Scp added start
    private void OnGetMaxSlowDirect(Entity<SpeedModifierContactCapClothingComponent> ent, ref GetSpeedModifierContactCapEvent args)
    {
        args.SetIfMax(ent.Comp.MaxContactSprintSlowdown, ent.Comp.MaxContactWalkSlowdown);
    }
    // Scp added end
}
