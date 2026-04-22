using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.SubFloor;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class TrayScannerComponent : Component
{
    /// <summary>
    ///     Radius in which the scanner will reveal entities. Centered on the <see cref="LastLocation"/>.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float Range = 4f;
}