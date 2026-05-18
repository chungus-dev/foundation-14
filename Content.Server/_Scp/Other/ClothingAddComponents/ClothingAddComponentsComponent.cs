using Robust.Shared.Prototypes;

namespace Content.Server._Scp.Other.ClothingAddComponents;

[RegisterComponent]
public sealed partial class ClothingAddComponentsComponent : Component
{
    [DataField(required:true)]
    public ComponentRegistry Components = new();
}
