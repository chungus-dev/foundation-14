using Content.Shared.Eye;
using Content.Shared.Hands;
using Content.Shared.Interaction;
using Content.Shared.Inventory.Events;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared.SubFloor;

public abstract class SharedTrayScannerSystem : EntitySystem
{
    [Dependency] private readonly INetManager _netMan = default!;
    [Dependency] private readonly SharedEyeSystem _eye = default!;

    public const float SubfloorRevealAlpha = 0.8f;

    public override void Initialize()
    {
        base.Initialize();
        
        SubscribeLocalEvent<TrayScannerComponent, GotEquippedHandEvent>(OnTrayHandEquipped);
        SubscribeLocalEvent<TrayScannerComponent, GotUnequippedHandEvent>(OnTrayHandUnequipped);
        SubscribeLocalEvent<TrayScannerComponent, GotEquippedEvent>(OnTrayEquipped);
        SubscribeLocalEvent<TrayScannerComponent, GotUnequippedEvent>(OnTrayUnequipped);

        SubscribeLocalEvent<TrayScannerUserComponent, GetVisMaskEvent>(OnUserGetVis);
    }

    private void OnUserGetVis(Entity<TrayScannerUserComponent> ent, ref GetVisMaskEvent args)
    {
        args.VisibilityMask |= (int)VisibilityFlags.Subfloor;
    }

    private void OnEquip(EntityUid user)
    {
        if (_netMan.IsClient)
            return;

        var comp = EnsureComp<TrayScannerUserComponent>(user);
        comp.Count++;

        if (comp.Count > 1)
            return;

        _eye.RefreshVisibilityMask(user);
    }

    private void OnUnequip(EntityUid user)
    {
        if (_netMan.IsClient)
            return;

        if (!TryComp(user, out TrayScannerUserComponent? comp))
            return;

        comp.Count--;

        if (comp.Count > 0)
            return;

        RemComp<TrayScannerUserComponent>(user);
        _eye.RefreshVisibilityMask(user);
    }

    private void OnTrayHandUnequipped(Entity<TrayScannerComponent> ent, ref GotUnequippedHandEvent args)
    {
        OnUnequip(args.User);
    }

    private void OnTrayHandEquipped(Entity<TrayScannerComponent> ent, ref GotEquippedHandEvent args)
    {
        OnEquip(args.User);
    }

    private void OnTrayUnequipped(Entity<TrayScannerComponent> ent, ref GotUnequippedEvent args)
    {
        OnUnequip(args.EquipTarget);
    }

    private void OnTrayEquipped(Entity<TrayScannerComponent> ent, ref GotEquippedEvent args)
    {
        OnEquip(args.EquipTarget);
    }
}