namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Marks sprite visuals that must be rendered into the owner-aware shadow protection texture.
/// </summary>
[RegisterComponent]
public sealed partial class ScpShadowProtectedTextureVisualsComponent : Component
{
    /// <summary>
    /// Defines which caster shadows the protected visuals reject.
    /// </summary>
    [DataField]
    public ScpShadowProtectionMode Mode = ScpShadowProtectionMode.Self;
}

/// <summary>
/// Defines how protected visuals interact with caster shadows.
/// </summary>
public enum ScpShadowProtectionMode : byte
{
    /// <summary>
    /// Rejects only shadows cast by the same entity.
    /// </summary>
    Self,

    /// <summary>
    /// Rejects shadows cast by every entity.
    /// </summary>
    Always,
}
