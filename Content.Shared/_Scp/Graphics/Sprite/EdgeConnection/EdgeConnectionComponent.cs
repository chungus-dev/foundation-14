using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Graphics.Sprite.EdgeConnection;

/// <summary>
/// Enables visual edge connections between adjacent entities with the same connection key.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class EdgeConnectionComponent : Component
{
    /// <summary>
    /// Key used to decide which entities may connect to each other.
    /// </summary>
    [DataField]
    public string ConnectionKey = "default";

    /// <summary>
    /// Local directions where this entity may visually connect.
    /// </summary>
    [DataField]
    public EdgeConnectionFlags AllowedDirections = EdgeConnectionFlags.None;
}
