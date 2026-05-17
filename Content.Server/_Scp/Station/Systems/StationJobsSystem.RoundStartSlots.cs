using Content.Server.Station.Components;
using Content.Shared.Roles;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Content.Server.Station.Systems;
#pragma warning restore IDE0130

public sealed partial class StationJobsSystem
{
    /// <inheritdoc cref="TrySetRoundStartJobSlot(EntityUid,string,int,bool,StationJobsComponent?)"/>
    public bool TrySetRoundStartJobSlot(
        EntityUid station,
        JobPrototype jobPrototype,
        int amount,
        bool createSlot = false,
        StationJobsComponent? stationJobs = null)
    {
        return TrySetRoundStartJobSlot(station, jobPrototype.ID, amount, createSlot, stationJobs);
    }

    /// <summary>
    /// Sets the round-start slot count for a station job without changing the midround slot directly.
    /// </summary>
    /// <remarks>
    /// The station stores two slot pools in <see cref="StationJobsComponent.SetupAvailableJobs"/>:
    /// index 0 is round-start, index 1 is midround. Negative values mean unlimited.
    /// </remarks>
    public bool TrySetRoundStartJobSlot(
        EntityUid station,
        string jobPrototypeId,
        int amount,
        bool createSlot = false,
        StationJobsComponent? stationJobs = null)
    {
        if (!Resolve(station, ref stationJobs))
            throw new ArgumentException("Tried to use a non-station entity as a station!", nameof(station));

        var normalized = amount < 0 ? -1 : amount;

        switch (stationJobs.SetupAvailableJobs.TryGetValue(jobPrototypeId, out var slots))
        {
            case false:
                if (!createSlot)
                    return false;

                stationJobs.SetupAvailableJobs[jobPrototypeId] = [normalized, normalized];
                return true;
            case true:
                slots[0] = normalized;
                return true;
        }
    }
}
