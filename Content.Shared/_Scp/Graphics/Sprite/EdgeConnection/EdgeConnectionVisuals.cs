using Robust.Shared.Serialization;

namespace Content.Shared._Scp.Graphics.Sprite.EdgeConnection;

[Serializable, NetSerializable]
public enum EdgeConnectionVisuals : byte
{
    ConnectionMask,
}

[Flags]
[Serializable, NetSerializable]
public enum EdgeConnectionFlags : byte
{
    None = 0,
    North = 1,
    South = 2,
    East = 4,
    West = 8,
}
