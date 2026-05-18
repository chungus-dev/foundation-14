using Robust.Shared.Serialization;

namespace Content.Shared._Scp.Anomaly.Scp106;

[Serializable, NetSerializable]
public enum Scp106Visuals
{
    Visuals,
}

[Serializable, NetSerializable]
public enum Scp106VisualsState
{
    Default,
    Entering,
    Exiting,
}
