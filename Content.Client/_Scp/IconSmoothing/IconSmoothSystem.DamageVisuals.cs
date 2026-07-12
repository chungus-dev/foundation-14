using Robust.Client.GameObjects;

namespace Content.Client.IconSmoothing;

#pragma warning disable IDE0130 // Namespace does not match folder structure

public sealed partial class IconSmoothSystem
{
    internal (byte Se, byte Ne, byte Nw, byte Sw) GetCornerStateIds(Entity<SpriteComponent> sprite)
    {
        return (
            GetCornerStateId(sprite, CornerLayers.SE),
            GetCornerStateId(sprite, CornerLayers.NE),
            GetCornerStateId(sprite, CornerLayers.NW),
            GetCornerStateId(sprite, CornerLayers.SW));
    }

    private byte GetCornerStateId(Entity<SpriteComponent> sprite, CornerLayers corner)
    {
        if (!_sprite.TryGetLayer(sprite.AsNullable(), corner, out var layer, false))
            return 0;

        return (byte) (layer.State.Name![^1] - '0');
    }
}
