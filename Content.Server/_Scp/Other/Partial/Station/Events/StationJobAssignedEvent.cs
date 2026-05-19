using Content.Shared.Roles;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Content.Server.Station.Events;
#pragma warning restore IDE0130

[ByRefEvent]
public readonly record struct StationJobAssignedEvent(
    NetUserId Player,
    EntityUid Station,
    ProtoId<JobPrototype> Job,
    Dictionary<ProtoId<JobPrototype>, int?> StationJobs,
    Dictionary<ProtoId<JobPrototype>, int?> CurrentJobs);
