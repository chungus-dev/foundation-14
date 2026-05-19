using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Graphics.LightFlicking;

[RegisterComponent, NetworkedComponent]
public sealed partial class ActiveLightFlickingComponent : Component
{
    [ViewVariables]
    public TimeSpan NextFlickTime;

    [ViewVariables]
    public float CachedRadius;

    [ViewVariables]
    public float CachedEnergy;

    [ViewVariables]
    public Color CachedColor;
}
