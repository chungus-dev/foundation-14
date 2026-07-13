using Content.Shared._Scp.Graphics.Sprite.EdgeConnection;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Scp.Graphics.Sprite.EdgeConnection;

/// <summary>
/// Calculates appearance masks for edge-connected sprites.
/// </summary>
public sealed partial class EdgeConnectionSystem : EntitySystem
{
    [Dependency] private AppearanceSystem _appearance = default!;
    [Dependency] private SharedMapSystem _map = default!;

    [Dependency] private EntityQuery<EdgeConnectionComponent> _edgeQuery;

    private const float MinimumMovementDistance = 0.005f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<EdgeConnectionComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<EdgeConnectionComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<EdgeConnectionComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        SubscribeLocalEvent<EdgeConnectionComponent, MoveEvent>(OnMove);
    }

    private void OnInit(Entity<EdgeConnectionComponent> ent, ref ComponentInit args)
    {
        UpdateConnections(ent);
        UpdateNeighbors(ent);
    }

    private void OnShutdown(Entity<EdgeConnectionComponent> ent, ref ComponentShutdown args)
    {
        _appearance.SetData(ent, EdgeConnectionVisuals.ConnectionMask, EdgeConnectionFlags.None);
        UpdateNeighbors(ent);
    }

    private void OnAnchorChanged(Entity<EdgeConnectionComponent> ent, ref AnchorStateChangedEvent args)
    {
        UpdateConnections(ent);
        UpdateNeighbors(ent);
    }

    private void OnMove(Entity<EdgeConnectionComponent> ent, ref MoveEvent args)
    {
        var rotationChanged = !args.OldRotation.EqualsApprox(args.NewRotation);
        var positionChanged = args.ParentChanged ||
                              (args.NewPosition.Position - args.OldPosition.Position).LengthSquared() >=
                              MinimumMovementDistance * MinimumMovementDistance;

        if (!rotationChanged && !positionChanged)
            return;

        if (positionChanged)
            UpdateNeighbors(args.OldPosition);

        UpdateConnections(ent);
        UpdateNeighbors(ent);
    }

    private void UpdateConnections(Entity<EdgeConnectionComponent> ent)
    {
        var xform = Transform(ent);

        if (!xform.Anchored || !TryComp<MapGridComponent>(xform.GridUid, out var grid))
        {
            _appearance.SetData(ent, EdgeConnectionVisuals.ConnectionMask, EdgeConnectionFlags.None);
            return;
        }

        var mask = EdgeConnectionFlags.None;
        var tile = _map.TileIndicesFor(xform.GridUid.Value, grid, xform.Coordinates);
        var worldAllowed = RotateDirections(ent.Comp.AllowedDirections, xform.LocalRotation);

        if ((worldAllowed & EdgeConnectionFlags.East) != 0 &&
            HasMatchingNeighbor(ent, xform.GridUid.Value, grid, tile + new Vector2i(1, 0), EdgeConnectionFlags.West))
        {
            mask |= EdgeConnectionFlags.East;
        }

        if ((worldAllowed & EdgeConnectionFlags.West) != 0 &&
            HasMatchingNeighbor(ent, xform.GridUid.Value, grid, tile + new Vector2i(-1, 0), EdgeConnectionFlags.East))
        {
            mask |= EdgeConnectionFlags.West;
        }

        if ((worldAllowed & EdgeConnectionFlags.North) != 0 &&
            HasMatchingNeighbor(ent, xform.GridUid.Value, grid, tile + new Vector2i(0, 1), EdgeConnectionFlags.South))
        {
            mask |= EdgeConnectionFlags.North;
        }

        if ((worldAllowed & EdgeConnectionFlags.South) != 0 &&
            HasMatchingNeighbor(ent, xform.GridUid.Value, grid, tile + new Vector2i(0, -1), EdgeConnectionFlags.North))
        {
            mask |= EdgeConnectionFlags.South;
        }

        _appearance.SetData(ent, EdgeConnectionVisuals.ConnectionMask, RotateDirectionsInverse(mask, xform.LocalRotation));
    }

    private bool HasMatchingNeighbor(
        Entity<EdgeConnectionComponent> entity,
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i tile,
        EdgeConnectionFlags requiredDirection)
    {
        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        var entityRotation = NormalizeToCardinalDegrees(Transform(entity).LocalRotation);

        while (anchored.MoveNext(out var other))
        {
            if (other == entity.Owner)
                continue;

            if (!_edgeQuery.TryComp(other.Value, out var otherComp) ||
                otherComp.ConnectionKey != entity.Comp.ConnectionKey)
            {
                continue;
            }

            var otherXform = Transform(other.Value);
            if (!otherXform.Anchored)
                continue;

            if (otherXform.GridUid != gridUid ||
                _map.TileIndicesFor(gridUid, grid, otherXform.Coordinates) != tile)
            {
                continue;
            }

            var otherWorldAllowed = RotateDirections(otherComp.AllowedDirections, otherXform.LocalRotation);
            if ((otherWorldAllowed & requiredDirection) == 0)
                continue;

            if (NormalizeToCardinalDegrees(otherXform.LocalRotation) == entityRotation)
                return true;
        }

        return false;
    }

    private void UpdateNeighbors(Entity<EdgeConnectionComponent> ent)
    {
        var xform = Transform(ent);

        if (!TryComp<MapGridComponent>(xform.GridUid, out var grid))
            return;

        var tile = _map.TileIndicesFor(xform.GridUid.Value, grid, xform.Coordinates);

        UpdateNeighborsAtTile(xform.GridUid.Value, grid, tile + new Vector2i(1, 0));
        UpdateNeighborsAtTile(xform.GridUid.Value, grid, tile + new Vector2i(-1, 0));
        UpdateNeighborsAtTile(xform.GridUid.Value, grid, tile + new Vector2i(0, 1));
        UpdateNeighborsAtTile(xform.GridUid.Value, grid, tile + new Vector2i(0, -1));
    }

    private void UpdateNeighbors(EntityCoordinates coordinates)
    {
        if (!TryComp<MapGridComponent>(coordinates.EntityId, out var grid))
            return;

        var tile = _map.TileIndicesFor(coordinates.EntityId, grid, coordinates);

        UpdateNeighborsAtTile(coordinates.EntityId, grid, tile + new Vector2i(1, 0));
        UpdateNeighborsAtTile(coordinates.EntityId, grid, tile + new Vector2i(-1, 0));
        UpdateNeighborsAtTile(coordinates.EntityId, grid, tile + new Vector2i(0, 1));
        UpdateNeighborsAtTile(coordinates.EntityId, grid, tile + new Vector2i(0, -1));
    }

    private void UpdateNeighborsAtTile(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var other))
        {
            if (!_edgeQuery.TryComp(other.Value, out var comp))
                continue;

            UpdateConnections((other.Value, comp));
        }
    }

    private static EdgeConnectionFlags RotateDirections(EdgeConnectionFlags flags, Angle rotation)
    {
        return RotateDirections(flags, rotation, clockwise: false);
    }

    private static EdgeConnectionFlags RotateDirectionsInverse(EdgeConnectionFlags flags, Angle rotation)
    {
        return RotateDirections(flags, rotation, clockwise: true);
    }

    private static EdgeConnectionFlags RotateDirections(EdgeConnectionFlags flags, Angle rotation, bool clockwise)
    {
        var quarterTurns = NormalizeToCardinalDegrees(rotation) / 90;

        if (!clockwise)
            quarterTurns = (4 - quarterTurns) % 4;

        for (var i = 0; i < quarterTurns; i++)
        {
            flags = RotateClockwise(flags);
        }

        return flags;
    }

    private static EdgeConnectionFlags RotateClockwise(EdgeConnectionFlags flags)
    {
        var rotated = EdgeConnectionFlags.None;

        if ((flags & EdgeConnectionFlags.North) != 0)
            rotated |= EdgeConnectionFlags.East;
        if ((flags & EdgeConnectionFlags.East) != 0)
            rotated |= EdgeConnectionFlags.South;
        if ((flags & EdgeConnectionFlags.South) != 0)
            rotated |= EdgeConnectionFlags.West;
        if ((flags & EdgeConnectionFlags.West) != 0)
            rotated |= EdgeConnectionFlags.North;

        return rotated;
    }

    private static int NormalizeToCardinalDegrees(Angle rotation)
    {
        var degrees = (int) Math.Round(rotation.Degrees) % 360;
        if (degrees < 0)
            degrees += 360;

        return (int) Math.Round(degrees / 90.0) % 4 * 90;
    }
}
