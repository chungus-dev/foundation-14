using Content.Shared._Scp.Mobs.Interaction.Holding.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Mobs.Interaction.Holding.Components;

/// <summary>
/// Semantic state that marks an active breakout attempt during a full hold.
/// </summary>
[RegisterComponent, NetworkedComponent]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ScpBreakoutAttemptComponent : Component;
