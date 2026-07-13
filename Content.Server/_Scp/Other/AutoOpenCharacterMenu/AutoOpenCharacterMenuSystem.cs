using Content.Server.Players.PlayTimeTracking;
using Content.Shared._Scp.Other.AutoOpenCharacterMenu;
using Content.Shared._Scp.ScpCCVars;
using Content.Shared.GameTicking;
using Content.Shared.Roles;

namespace Content.Server._Scp.Other.AutoOpenCharacterMenu;

public sealed partial class AutoOpenCharacterMenuSystem : SharedAutoOpenCharacterMenuSystem
{
    [Dependency] private PlayTimeTrackingManager _playtime = default!;

    private bool _enabled;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);

        Subs.CVar(Cfg, ScpCCVars.AutoOpenCharacterMenuServerSideEnabled, b => _enabled = b, true);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent ev)
    {
        if (!_enabled)
            return;

        if (ev.JobId == null || !ProtoMan.TryIndex<JobPrototype>(ev.JobId, out var job))
            return;

        var playtime = _playtime.GetPlayTimeForTracker(ev.Player, job.PlayTimeTracker);

        if (playtime != TimeSpan.Zero)
            return;

        var playerEntity = GetNetEntity(ev.Mob);
        RaiseNetworkEvent(new OpenCharacterMenuRequest(playerEntity));
    }
}
