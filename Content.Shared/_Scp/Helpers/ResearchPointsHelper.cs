using Content.Shared.Research.Prototypes;
using JetBrains.Annotations;

namespace Content.Shared._Scp.Helpers;

/// <summary>
/// Helper for research cost checks on the single-point research model used by foundation-14.
/// </summary>
public sealed class ResearchPointsHelper : EntitySystem
{
    [PublicAPI]
    public static string PointsToString(int points)
    {
        return points.ToString();
    }

    [PublicAPI]
    public static bool CanBuy(TechnologyPrototype tech, int totalPoints)
    {
        return IsEnoughPoints(totalPoints, GetPoints(tech));
    }

    [PublicAPI]
    public static bool IsEnoughPoints(int pointWeHave, int requiredPoints)
    {
        return pointWeHave >= requiredPoints;
    }

    [PublicAPI]
    public static int GetPoints(TechnologyPrototype tech)
    {
        return tech.Cost;
    }
}
