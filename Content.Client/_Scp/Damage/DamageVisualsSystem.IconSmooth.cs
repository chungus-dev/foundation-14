using Content.Client.IconSmoothing;
using Content.Shared.DisplacementMap;
using Content.Shared.FixedPoint;
using Robust.Client.GameObjects;
using Robust.Shared.Utility;

namespace Content.Client.Damage;

#pragma warning disable IDE0130 // Namespace does not match folder structure

public sealed partial class DamageVisualsSystem
{
    [Dependency] private IconSmoothSystem _iconSmooth = default!;

    private static readonly (IconSmoothDamageCorner Corner, SpriteComponent.DirectionOffset Offset)[] IconSmoothCorners =
    [
        (IconSmoothDamageCorner.Se, SpriteComponent.DirectionOffset.None),
        (IconSmoothDamageCorner.Ne, SpriteComponent.DirectionOffset.CounterClockwise),
        (IconSmoothDamageCorner.Nw, SpriteComponent.DirectionOffset.Flip),
        (IconSmoothDamageCorner.Sw, SpriteComponent.DirectionOffset.Clockwise),
    ];

    private void InitializeIconSmooth()
    {
        SubscribeLocalEvent<DamageVisualsComponent, IconSmoothUpdatedEvent>(OnIconSmoothUpdated);
    }

    private bool TryAddIconSmoothDamageLayers(
        Entity<SpriteComponent?> spriteEnt,
        DamageVisualizerSprite sprite,
        string state,
        string mapKey,
        int? index)
    {
        if (!TryComp<DamageVisualsComponent>(spriteEnt, out var damageVisuals)
            || !damageVisuals.SupportIconSmooth
            || !damageVisuals.Overlay)
            return false;

        if (!TryComp<IconSmoothComponent>(spriteEnt, out var iconSmooth)
            || iconSmooth.Mode != IconSmoothingMode.Corners)
            return false;

        if (!Resolve(spriteEnt, ref spriteEnt.Comp))
            return false;

        var separator = state.IndexOf('_');
        if (separator < 0)
            return false;

        var cornerState = state.Insert(separator, "0");
        foreach (var (corner, offset) in IconSmoothCorners)
        {
            var layer = SpriteSystem.AddLayer(spriteEnt, new SpriteSpecifier.Rsi(new(sprite.Sprite), cornerState), index);
            SpriteSystem.LayerMapSet(spriteEnt, GetIconSmoothLayerKey(mapKey, corner), layer);
            SpriteSystem.LayerSetDirOffset(spriteEnt, layer, offset);
            SpriteSystem.LayerSetVisible(spriteEnt, layer, false);

            if (sprite.Color != null)
                SpriteSystem.LayerSetColor(spriteEnt, layer, Color.FromHex(sprite.Color));
        }

        damageVisuals.IconSmoothLayerKeys.Add(mapKey);
        return true;
    }

    private bool TryUpdateIconSmoothDamageLayers(
        Entity<SpriteComponent> spriteEnt,
        string statePrefix,
        FixedPoint2 threshold,
        string layerKey,
        DisplacementData? displacement)
    {
        // these lists currently contain one key; switch to a set if overlays start stacking.
        if (!TryComp<DamageVisualsComponent>(spriteEnt, out var damageVisuals)
            || !damageVisuals.IconSmoothLayerKeys.Contains(layerKey))
            return false;

        var corners = _iconSmooth.GetCornerStateIds(spriteEnt);
        foreach (var (corner, _) in IconSmoothCorners)
        {
            UpdateIconSmoothDamageLayer(spriteEnt, statePrefix, threshold, layerKey, corner, GetCornerState(corners, corner), displacement);
        }

        return true;
    }

    private void UpdateIconSmoothDamageLayer(
        Entity<SpriteComponent> spriteEnt,
        string statePrefix,
        FixedPoint2 threshold,
        string baseMapKey,
        IconSmoothDamageCorner corner,
        byte cornerState,
        DisplacementData? displacement)
    {
        var layerKey = GetIconSmoothLayerKey(baseMapKey, corner);
        if (!SpriteSystem.LayerMapTryGet(spriteEnt.AsNullable(), layerKey, out var layer, false))
            return;

        if (threshold == 0)
        {
            SpriteSystem.LayerSetVisible(spriteEnt.AsNullable(), layer, false);
            return;
        }

        SpriteSystem.LayerSetVisible(spriteEnt.AsNullable(), layer, true);
        SpriteSystem.LayerSetRsiState(spriteEnt.AsNullable(), layer, GetIconSmoothState(statePrefix, cornerState, threshold));

        if (displacement != null)
            _displacement.TryAddDisplacement(displacement, spriteEnt, layer, layerKey, out _);
        else
            _displacement.EnsureDisplacementIsNotOnSprite(spriteEnt, layerKey);
    }

    private void OnIconSmoothUpdated(Entity<DamageVisualsComponent> ent, ref IconSmoothUpdatedEvent args)
    {
        if (ent.Comp.IconSmoothLayerKeys.Count == 0 || !TryComp(ent, out SpriteComponent? sprite))
            return;

        var spriteEnt = (ent, sprite);
        var corners = _iconSmooth.GetCornerStateIds(spriteEnt);

        foreach (var baseMapKey in ent.Comp.IconSmoothLayerKeys)
        {
            foreach (var (corner, _) in IconSmoothCorners)
            {
                RefreshIconSmoothDamageLayer(spriteEnt, baseMapKey, corner, GetCornerState(corners, corner));
            }
        }
    }

    private void RefreshIconSmoothDamageLayer(
        Entity<SpriteComponent> spriteEnt,
        string baseMapKey,
        IconSmoothDamageCorner corner,
        byte cornerState)
    {
        var layerKey = GetIconSmoothLayerKey(baseMapKey, corner);
        if (!SpriteSystem.LayerMapTryGet(spriteEnt.AsNullable(), layerKey, out var layer, false)
            || !spriteEnt.Comp[layer].Visible)
            return;

        var state = SpriteSystem.LayerGetRsiState(spriteEnt.AsNullable(), layer).Name;
        if (state == null)
            return;

        var separator = state.IndexOf('_');
        if (separator < 1 || state[separator - 1] - '0' == cornerState)
            return;

        SpriteSystem.LayerSetRsiState(spriteEnt.AsNullable(), layer, $"{state[..(separator - 1)]}{cornerState}{state[separator..]}");
    }

    private static string GetIconSmoothState(string statePrefix, byte cornerState, FixedPoint2 threshold)
    {
        var separator = statePrefix.IndexOf('_');
        return separator < 0
            ? $"{statePrefix}{cornerState}_{threshold}"
            : $"{statePrefix[..separator]}{cornerState}{statePrefix[separator..]}_{threshold}";
    }

    private static string GetIconSmoothLayerKey(string baseMapKey, IconSmoothDamageCorner corner)
    {
        return $"{baseMapKey}_{corner}";
    }

    private static byte GetCornerState(
        (byte Se, byte Ne, byte Nw, byte Sw) corners,
        IconSmoothDamageCorner corner)
    {
        return corner switch
        {
            IconSmoothDamageCorner.Se => corners.Se,
            IconSmoothDamageCorner.Ne => corners.Ne,
            IconSmoothDamageCorner.Nw => corners.Nw,
            IconSmoothDamageCorner.Sw => corners.Sw,
            _ => 0,
        };
    }

    private enum IconSmoothDamageCorner : byte
    {
        Se,
        Ne,
        Nw,
        Sw,
    }
}
