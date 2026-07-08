using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Robust.Shared.Physics.Events;

namespace Content.Shared._Scp.Other.OpenDoorsOnCollide;

public sealed partial class OpenDoorsOnCollideSystem : EntitySystem
{
    [Dependency] private SharedDoorSystem _door = default!;

    [Dependency] private EntityQuery<DoorComponent> _doorQuery;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<OpenDoorsOnCollideComponent, StartCollideEvent>(OnStartCollide);
    }

    private void OnStartCollide(Entity<OpenDoorsOnCollideComponent> ent, ref StartCollideEvent args)
    {
        if (!_doorQuery.TryComp(args.OtherEntity, out var door))
            return;

        _door.StartOpening(args.OtherEntity, door, ent, true);
    }
}
