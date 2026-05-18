using Robust.Shared.Prototypes;

namespace Content.Server._Scp.Other.SwapEntityOnSpawn;

[RegisterComponent]
public sealed partial class SwapEntityOnSpawnComponent : Component
{
    [DataField(required: true)]
    public HashSet<EntProtoId> Replace = [];

    /// <summary>
    /// Шанс замены ентити с заменяемого на заменяющий
    /// </summary>
    [DataField]
    public float Chance
    {
        get;
        set => field = Math.Clamp(value, 0f, 1f);
    } = 1f;
}
