using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Anomaly.Scp939;

[RegisterComponent, NetworkedComponent]
public sealed partial class Scp939VisibilityComponent : Component
{
    public const float InitialVisibilityAcc = 0.001f;
    public const float DefaultHideTime = 2.5f;
    public const int DefaultMinValue = 40;
    public const int DefaultMaxValue = 400;

    [DataField]
    public float HideTime = DefaultHideTime;

    [DataField]
    public int MinValue = DefaultMinValue;

    [DataField]
    public int MaxValue = DefaultMaxValue;
}
