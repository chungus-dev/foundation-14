using Content.Shared.CombatMode;
using Content.Shared.Input;
using Content.Shared.MouseRotator;
using Content.Shared.Movement.Components;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;

namespace Content.Shared._Scp.Mobs.Interaction.HoldToFace;

public sealed class HoldToFaceSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        CommandBinds.Builder
            .Bind(ContentKeyFunctions.HoldToFace,
                InputCmdHandler.FromDelegate(args => ToggleRotator(args, true), args => ToggleRotator(args, false), false, false))
            .Register<HoldToFaceSystem>();
    }

    private void ToggleRotator(ICommonSession? session, bool value)
    {
        if (session?.AttachedEntity is not { } ent || !HasComp<HoldToFaceComponent>(ent))
            return;

        // Don't try and override combat mode doing the same thing
        if (TryComp<CombatModeComponent>(ent, out var combat) && combat is { ToggleMouseRotator: true, IsInCombatMode: true })
            return;

        SetMouseRotatorComponents(ent, value);
    }

    private void SetMouseRotatorComponents(EntityUid uid, bool value)
    {
        if (value)
        {
            EnsureComp<MouseRotatorComponent>(uid);
            EnsureComp<NoRotateOnMoveComponent>(uid);
        }
        else
        {
            RemComp<MouseRotatorComponent>(uid);
            RemComp<NoRotateOnMoveComponent>(uid);
        }
    }
}
