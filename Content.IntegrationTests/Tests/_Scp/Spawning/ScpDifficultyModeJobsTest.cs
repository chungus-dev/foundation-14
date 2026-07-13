using System.Collections.Generic;
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.Server.GameTicking;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.GameTicking;
using Content.Shared.Maps;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Scp.Spawning;

[TestFixture]
[TestOf(typeof(StationJobsSystem))]
public sealed class ScpDifficultyModeJobsTest : GameTest
{
    public override PoolSettings PoolSettings => new()
    {
        Connected = true,
        Dirty = true,
    };

    private const string StationMapId = "ScpDifficultyModeJobsStation";
    private const string ScpOne = "TScpOne";
    private const string ScpTwo = "TScpTwo";
    private const string Assistant = "TScpAssistant";

    [TestPrototypes]
    private const string Prototypes = $@"
- type: playTimeTracker
  id: PlayTimeDummyScpOne

- type: playTimeTracker
  id: PlayTimeDummyScpTwo

- type: playTimeTracker
  id: PlayTimeDummyScpAssistant

- type: gameMap
  id: {StationMapId}
  minPlayers: 0
  mapName: {StationMapId}
  mapPath: /Maps/Test/empty.yml
  stations:
    Station:
      mapNameTemplate: {StationMapId}
      stationProto: StandardNanotrasenStation
      components:
        - type: StationJobs
          availableJobs:
            {ScpOne}: [1, 1]
            {ScpTwo}: [1, 1]
            {Assistant}: [-1, -1]

- type: job
  id: {ScpOne}
  playTimeTracker: PlayTimeDummyScpOne
  jobEntity: TScpOneMob

- type: job
  id: {ScpTwo}
  playTimeTracker: PlayTimeDummyScpTwo
  jobEntity: TScpTwoMob

- type: job
  id: {Assistant}
  playTimeTracker: PlayTimeDummyScpAssistant

- type: entity
  id: TScpOneMob
  components:
  - type: Scp
    class: Euclid

- type: entity
  id: TScpTwoMob
  components:
  - type: Scp
    class: Euclid

- type: entity
  id: TestScpDifficultyOneEuclid
  parent: BaseGameRule
  components:
  - type: ScpDifficultyModeRule
    scpSlots:
      Euclid:
        min: 1
        max: 1

- type: entity
  id: TestScpDifficultyTwoEuclid
  parent: BaseGameRule
  components:
  - type: ScpDifficultyModeRule
    scpSlots:
      Euclid:
        min: 2
        max: 2
";

    /// <summary>
    /// Verifies that round-start job assignment spends a shared SCP class slot immediately:
    /// when two players prefer different Euclid SCP jobs and the difficulty mode allows only one Euclid,
    /// exactly one player receives an SCP job and the other falls back to their non-SCP job.
    /// </summary>
    [Test]
    public async Task RoundStart_WhenDifficultyAllowsOneScp_AssignsOneScpAndOneFallbackJob()
    {
        var pair = Pair;
        var server = pair.Server;
        var stationJobs = server.System<StationJobsSystem>();

        var station = await CreateStationAndStartRule("TestScpDifficultyOneEuclid");
        var players = (await server.AddDummySessions(2)).Select(session => session.UserId).ToArray();

        await server.WaitAssertion(() =>
        {
            var profiles = CreateTwoScpProfiles(players);
            var assigned = stationJobs.AssignJobs(profiles, [station]);
            var jobs = assigned.Values.Select(x => x.Item1).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(jobs.Count(IsScpJob), Is.EqualTo(1));
                Assert.That(jobs, Does.Contain(Assistant));
            });
        });
    }

    /// <summary>
    /// Verifies that round-start job assignment does not over-close SCP jobs when the difficulty mode
    /// allows enough slots for the whole class: two players preferring different Euclid SCP jobs both
    /// receive their selected SCP jobs when two Euclid slots are available.
    /// </summary>
    [Test]
    public async Task RoundStart_WhenDifficultyAllowsTwoScps_AssignsBothScpJobs()
    {
        var pair = Pair;
        var server = pair.Server;
        var stationJobs = server.System<StationJobsSystem>();

        var station = await CreateStationAndStartRule("TestScpDifficultyTwoEuclid");
        var players = (await server.AddDummySessions(2)).Select(session => session.UserId).ToArray();

        await server.WaitAssertion(() =>
        {
            var profiles = CreateTwoScpProfiles(players);
            var assigned = stationJobs.AssignJobs(profiles, [station]);
            var jobs = assigned.Values.Select(x => x.Item1).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(jobs.Count(IsScpJob), Is.EqualTo(2));
                Assert.That(jobs, Does.Contain(ScpOne));
                Assert.That(jobs, Does.Contain(ScpTwo));
            });
        });
    }

    /// <summary>
    /// Verifies the midround slot update path: after a player late-joins as one Euclid SCP,
    /// all other Euclid SCP jobs lose a slot, so another player cannot pick a different SCP
    /// from the same class and falls back to an available non-SCP job.
    /// </summary>
    [Test]
    public async Task MidRound_WhenPlayerJoinsAsScp_ClosesSameClassScpJobs()
    {
        var pair = Pair;
        var server = pair.Server;
        var stationJobs = server.System<StationJobsSystem>();

        var station = await CreateStationAndStartRule("TestScpDifficultyOneEuclid");
        var assignedFirstScp = false;

        await server.WaitPost(() =>
        {
            assignedFirstScp = stationJobs.TryAssignJob(station, ScpOne, new NetUserId(Guid.NewGuid()));

            var mob = server.EntMan.SpawnEntity("TScpOneMob", MapCoordinates.Nullspace);
            var ev = new PlayerSpawnCompleteEvent(
                mob,
                pair.Player!,
                ScpOne,
                true,
                false,
                1,
                station,
                HumanoidCharacterProfile.Random());

            server.EntMan.EventBus.RaiseLocalEvent(mob, ev, true);
        });

        await server.WaitAssertion(() =>
        {
            var lateJoinProfile = HumanoidCharacterProfile.Random()
                .WithJobPriority(ScpTwo, JobPriority.High)
                .WithJobPriority(Assistant, JobPriority.Medium);

            var picked = stationJobs.PickBestAvailableJobWithPriority(
                station,
                lateJoinProfile.JobPriorities,
                true);

            Assert.Multiple(() =>
            {
                Assert.That(assignedFirstScp, Is.True);
                Assert.That(stationJobs.TryGetJobSlot(station, ScpOne, out var scpOneSlots), Is.True);
                Assert.That(stationJobs.TryGetJobSlot(station, ScpTwo, out var scpTwoSlots), Is.True);
                Assert.That(scpOneSlots, Is.EqualTo(0));
                Assert.That(scpTwoSlots, Is.EqualTo(0));
                Assert.That(picked, Is.EqualTo(Assistant));
            });
        });
    }

    private async Task<EntityUid> CreateStationAndStartRule(string rule)
    {
        var server = Pair.Server;
        var prototypeManager = server.ResolveDependency<IPrototypeManager>();
        var stationSystem = server.System<StationSystem>();
        var ticker = server.System<GameTicker>();
        var stationProto = prototypeManager.Index<GameMapPrototype>(StationMapId);
        var station = EntityUid.Invalid;
        var ruleStarted = false;

        await server.WaitPost(() =>
        {
            station = stationSystem.InitializeNewStation(stationProto.Stations["Station"], null, "Scp Difficulty Test Station");
            server.EntMan.EnsureComponent<StationEventEligibleComponent>(station);
            ruleStarted = ticker.StartGameRule(rule);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(station, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(ruleStarted, Is.True);
        });

        return station;
    }

    private static Dictionary<NetUserId, HumanoidCharacterProfile> CreateTwoScpProfiles(NetUserId[] players)
    {
        return new Dictionary<NetUserId, HumanoidCharacterProfile>
        {
            [players[0]] = HumanoidCharacterProfile.Random()
                .WithJobPriority(ScpOne, JobPriority.High)
                .WithJobPriority(Assistant, JobPriority.Medium),
            [players[1]] = HumanoidCharacterProfile.Random()
                .WithJobPriority(ScpTwo, JobPriority.High)
                .WithJobPriority(Assistant, JobPriority.Medium),
        };
    }

    private static bool IsScpJob(ProtoId<JobPrototype>? job)
    {
        return job == ScpOne || job == ScpTwo;
    }
}
