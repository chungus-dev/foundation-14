using Robust.Shared.Prototypes;

namespace Content.Server._Scp.Anomaly.Scp106.Components;

[RegisterComponent]
public sealed partial class Scp106PortalSpawnerComponent : Component
{
    [DataField]
    public EntProtoId Monster = "MobScp106Monster";

    [DataField]
    public EntProtoId BigMonster = "MobScp106BigMonster";

    [ViewVariables]
    public TimeSpan NextSpawnTime;

    /// <summary>
    /// Подсчет количества заспавненных монстров.
    /// Обнуляется, когда достигает <inheritdoc cref="MonsterAccumulatorBound"/>
    /// </summary>
    [ViewVariables]
    public int MonsterAccumulator = 0;

    /// <summary>
    /// Предел монстров, после которого должен появиться большой монстр
    /// </summary>
    [ViewVariables]
    public int MonsterAccumulatorBound = 5;
}
