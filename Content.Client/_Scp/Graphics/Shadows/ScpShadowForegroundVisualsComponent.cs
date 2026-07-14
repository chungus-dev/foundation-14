namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Marks sprite visuals that should be drawn above client-only entity shadows.
/// This does not affect FOV or gameplay visibility.
/// </summary>
[RegisterComponent]
public sealed partial class ScpShadowForegroundVisualsComponent : Component;
