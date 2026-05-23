namespace Content.Server._Scp.Graphics.LightFlicking;

[RegisterComponent]
public sealed partial class LightFlickingComponent : Component
{
    [ViewVariables]
    public TimeSpan? NextFlickStartChanceTime = null;
}
