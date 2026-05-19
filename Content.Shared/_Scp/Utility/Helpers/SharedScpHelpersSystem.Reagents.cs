using Content.Shared._Scp.Helpers;
using Content.Shared._Scp.Vision.Proximity;
using Content.Shared._Scp.Vision.Watching;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids.Components;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Utility.Helpers;

public abstract partial class SharedScpHelpersSystem
{
    [Dependency] private readonly EyeWatchingSystem _watching = default!;

    /// <summary>
    /// Получает суммарное количество реагента в зоне видимости сущности.
    /// Возвращает количество реагентов.
    /// </summary>
    public FixedPoint2 GetAroundSolutionVolume(EntityUid uid,
        ProtoId<ReagentPrototype> reagent,
        List<EntityUid> puddleList,
        LineOfSightBlockerLevel lineOfSight = LineOfSightBlockerLevel.Transparent,
        LookupFlags flags = LookupFlags.StaticSundries | LookupFlags.Sensors | LookupFlags.Approximate)
    {
        using var puddles = ListPoolEntity<PuddleComponent>.Rent();
        if (!_watching.TryGetAllEntitiesVisibleTo(uid, puddles.Value, lineOfSight, flags))
            return FixedPoint2.Zero;

        FixedPoint2 total = 0;
        foreach (var puddle in puddles.Value)
        {
            if (!puddle.Comp.Solution.HasValue)
                continue;

            var solution = puddle.Comp.Solution.Value.Comp.Solution;
            foreach (var (reagentId, quantity) in solution.Contents)
            {
                if (reagentId.Prototype != reagent)
                    continue;

                puddleList.Add(puddle);
                total += quantity;
            }
        }

        return total;
    }

    /// <summary>
    /// Получает суммарное количество реагента в зоне видимости сущности.
    /// Возвращает количество реагентов.
    /// </summary>
    public FixedPoint2 GetAroundSolutionVolume(EntityUid uid,
        ProtoId<ReagentPrototype> reagent,
        LineOfSightBlockerLevel lineOfSight = LineOfSightBlockerLevel.Transparent,
        LookupFlags flags = LookupFlags.StaticSundries | LookupFlags.Sensors | LookupFlags.Approximate)
    {
        using var puddles = ListPoolEntity<PuddleComponent>.Rent();
        if (!_watching.TryGetAllEntitiesVisibleTo(uid, puddles.Value, lineOfSight, flags))
            return FixedPoint2.Zero;

        FixedPoint2 total = 0;
        foreach (var puddle in puddles.Value)
        {
            if (!puddle.Comp.Solution.HasValue)
                continue;

            var solution = puddle.Comp.Solution.Value.Comp.Solution;

            foreach (var (reagentId, quantity) in solution.Contents)
            {
                if (reagentId.Prototype != reagent)
                    continue;

                total += quantity;
            }
        }

        return total;
    }

    public bool IsAroundSolutionVolumeGreaterThan(EntityUid uid,
        ProtoId<ReagentPrototype> reagent,
        FixedPoint2 required,
        LineOfSightBlockerLevel lineOfSight = LineOfSightBlockerLevel.Transparent,
        LookupFlags flags = LookupFlags.StaticSundries | LookupFlags.Sensors | LookupFlags.Approximate)
    {
        using var puddles = ListPoolEntity<PuddleComponent>.Rent();
        if (!_watching.TryGetAllEntitiesVisibleTo(uid, puddles.Value, lineOfSight, flags))
            return false;

        FixedPoint2 total = 0;
        foreach (var puddle in puddles.Value)
        {
            if (!puddle.Comp.Solution.HasValue)
                continue;

            var solution = puddle.Comp.Solution.Value.Comp.Solution;

            foreach (var (reagentId, quantity) in solution.Contents)
            {
                if (reagentId.Prototype != reagent)
                    continue;

                total += quantity;

                if (total >= required)
                    return true;

            }
        }

        return false;
    }
}
