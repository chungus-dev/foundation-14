using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

public enum ScpShadowCasterKind : byte
{
    Object,
    Mob,
}

public enum ScpShadowQuality : byte
{
    Disabled,
    Bounds,
    Hull,
    Sprite,
}

/// <summary>
/// Marks a client sprite as a visual shadow caster without affecting FOV or gameplay visibility.
/// </summary>
[RegisterComponent]
public sealed partial class ScpShadowCasterVisualsComponent : Component
{
    #region Prototype data

    /// <summary>
    /// Local fallback contour used by the low-quality mode and near-light rejection.
    /// </summary>
    [DataField(required: true)]
    public Box2 Bounds;

    /// <summary>
    /// Selects which independent client quality setting controls this caster.
    /// </summary>
    [DataField]
    public ScpShadowCasterKind Kind = ScpShadowCasterKind.Object;

    #endregion
}
