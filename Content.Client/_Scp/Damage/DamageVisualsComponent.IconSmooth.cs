namespace Content.Client.Damage;

#pragma warning disable IDE0130 // Namespace does not match folder structure

public sealed partial class DamageVisualsComponent
{
    /// <summary>
    /// Enables four-corner damage overlays on entities smoothed in corner mode.
    /// </summary>
    [DataField]
    public bool SupportIconSmooth;

    public readonly List<string> IconSmoothLayerKeys = [];
}
