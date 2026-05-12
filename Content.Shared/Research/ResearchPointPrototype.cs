using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Array;

namespace Content.Shared.Research;

[Prototype]
public sealed partial class ResearchPointPrototype : IPrototype, IInheritingPrototype
{
    [ParentDataField(typeof(AbstractPrototypeIdArraySerializer<ResearchPointPrototype>))]
    public string[]? Parents { get; private set; }

    [NeverPushInheritance, AbstractDataField]
    public bool Abstract { get; private set; }

    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; set; }
}
