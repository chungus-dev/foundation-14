namespace Content.Server._Scp.Research.SwapReagentOnSpawn;

[RegisterComponent]
public sealed partial class SwapReagentOnSpawnComponent : Component
{
    [DataField(required: true)]
    public Dictionary<string, string> Replace = new ();

    /// <summary>
    /// Шанс замены реагента с заменяемого на заменяющий
    /// </summary>
    [DataField]
    public float Chance
    {
        get;
        set => field = Math.Clamp(value, 0f, 1f);
    }
}
