using Content.Shared._Scp.Anomaly.Scp330;
using Content.Shared.Xenoarchaeology.Artifact.Components;
using Content.Shared.Xenoarchaeology.Artifact.XAT;

namespace Content.Shared._Scp.Research.Artifact.XAT._ScpSpecific.Scp330;

public sealed class XATScp330PunishmentSystem : BaseXATSystem<XATScp330PunishmentComponent>
{
    public override void Initialize()
    {
        base.Initialize();

        XATSubscribeDirectEvent<Scp330SelfPunishmentEvent>(OnPunishment);
    }

    private void OnPunishment(Entity<XenoArtifactComponent> artifact,
        Entity<XATScp330PunishmentComponent, XenoArtifactNodeComponent> node,
        ref Scp330SelfPunishmentEvent args)
    {
        Trigger(artifact, node);
    }
}
