using Content.Shared.Cargo;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Robust.Shared.Containers;

namespace Content.Server.Xenoarchaeology.Artifact;

/// <inheritdoc cref="SharedXenoArtifactSystem"/>
public sealed partial class XenoArtifactSystem : SharedXenoArtifactSystem
{
    [Dependency] private readonly SharedContainerSystem _container = default!; // Scp added

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<XenoArtifactComponent, MapInitEvent>(OnArtifactMapInit);
        SubscribeLocalEvent<XenoArtifactComponent, PriceCalculationEvent>(OnCalculatePrice);
    }

    private void OnArtifactMapInit(Entity<XenoArtifactComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NodeContainer = _container.EnsureContainer<Container>(ent, XenoArtifactComponent.NodeContainerId); // Scp added - moved from shared because we need MapInitEvent to fix UninitializedSaveTest

        if (ent.Comp.IsGenerationRequired)
            GenerateArtifactStructure(ent);
    }

    private void OnCalculatePrice(Entity<XenoArtifactComponent> ent, ref PriceCalculationEvent args)
    {
        foreach (var node in GetAllNodes(ent))
        {
            if (node.Comp.Locked)
                continue;

            args.Price += node.Comp.ResearchValue * ent.Comp.PriceMultiplier;
        }
    }
}
