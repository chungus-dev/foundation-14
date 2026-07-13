using System.Text;
using Content.Shared.Research;
using Content.Shared.Research.Prototypes;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Utility;

/// <summary>
/// Calculates and formats research costs that may use multiple research point types.
/// </summary>
public sealed class ResearchPointsHelper : EntitySystem
{
    public static readonly ProtoId<ResearchPointPrototype> DefaultPoint = "Default";
    public static readonly ProtoId<ResearchPointPrototype> ScpPoint = "Scp";

    private static readonly Dictionary<ProtoId<TechnologyPrototype>, Dictionary<ProtoId<ResearchPointPrototype>, int>> CachedCost = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(_ => CachedCost.Clear());
    }

    [PublicAPI]
    public static string PointsToString(int points)
    {
        return points.ToString();
    }

    [PublicAPI]
    public static string PointsToString(
        Dictionary<ProtoId<ResearchPointPrototype>, int> points,
        string separator = "\n",
        IPrototypeManager? proto = null,
        ILocalizationManager? loc = null)
    {
        proto ??= IoCManager.Resolve<IPrototypeManager>();
        loc ??= IoCManager.Resolve<ILocalizationManager>();

        var sb = new StringBuilder();
        var first = true;

        foreach (var (pointType, value) in points)
        {
            if (!first)
                sb.Append(separator);

            first = false;

            var pointName = proto.TryIndex(pointType, out ResearchPointPrototype? pointPrototype)
                ? loc.GetString(pointPrototype.Name)
                : pointType.ToString();

            sb.Append(pointName)
                .Append(": ")
                .Append(value);
        }

        return sb.ToString();
    }

    [PublicAPI]
    public static bool CanBuy(TechnologyPrototype tech, int totalPoints)
    {
        return CanBuy(tech, new Dictionary<ProtoId<ResearchPointPrototype>, int>
        {
            [DefaultPoint] = totalPoints,
        });
    }

    [PublicAPI]
    public static bool CanBuy(TechnologyPrototype tech, Dictionary<ProtoId<ResearchPointPrototype>, int> totalPoints)
    {
        return IsEnoughPoints(totalPoints, GetPoints(tech));
    }

    [PublicAPI]
    public static bool IsEnoughPoints(int pointWeHave, int requiredPoints)
    {
        return pointWeHave >= requiredPoints;
    }

    [PublicAPI]
    public static bool IsEnoughPoints(
        Dictionary<ProtoId<ResearchPointPrototype>, int> pointWeHave,
        Dictionary<ProtoId<ResearchPointPrototype>, int> requiredPoints)
    {
        foreach (var (researchPointType, requiredAmount) in requiredPoints)
        {
            if (!pointWeHave.TryGetValue(researchPointType, out var point))
                return false;

            if (point < requiredAmount)
                return false;
        }

        return true;
    }

    [PublicAPI]
    public static Dictionary<ProtoId<ResearchPointPrototype>, int> GetPoints(TechnologyPrototype tech)
    {
        if (CachedCost.TryGetValue(tech.ID, out var cost))
            return cost;

        var computedCost = new Dictionary<ProtoId<ResearchPointPrototype>, int>(tech.CostList);

        if (!computedCost.ContainsKey(DefaultPoint) && tech.Cost != 0)
            computedCost[DefaultPoint] = tech.Cost;

        if (!computedCost.ContainsKey(ScpPoint) && tech.DefaultToScpScale != 0)
        {
            if (!computedCost.TryGetValue(DefaultPoint, out var defaultCost))
                return computedCost;

            computedCost[ScpPoint] = (int) Math.Ceiling(defaultCost * tech.DefaultToScpScale);
        }

        CachedCost[tech.ID] = computedCost;
        return computedCost;
    }
}
