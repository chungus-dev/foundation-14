using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Server._Scp.Research.XenoArch.Effects.CreateSmoke;

[RegisterComponent]
public sealed partial class ArtifactCreateSmokeComponent : Component
{
    [DataField(required: true)]
    public ProtoId<ReagentPrototype> Reagent;

    [DataField(required: true)]
    public float Quantity;

    [DataField(required: true)]
    public EntProtoId Prototype;

    [DataField]
    public float Duration = 30.0f;

    [DataField]
    public int SpreadRadius = 10;
}
