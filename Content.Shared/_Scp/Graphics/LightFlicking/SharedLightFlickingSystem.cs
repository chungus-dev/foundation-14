using Content.Shared._Scp.Utility.Random;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared._Scp.Graphics.LightFlicking;

public abstract partial class SharedLightFlickingSystem : EntitySystem
{
    [Dependency] private RandomPredictedSystem _randomPredicted = default!;
    [Dependency] protected IRobustRandom Random = default!;
    [Dependency] protected IGameTiming Timing = default!;

    private static readonly TimeSpan FlickInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan FlickVariation = TimeSpan.FromSeconds(0.45);

    protected void SetNextFlickingTime(Entity<ActiveLightFlickingComponent> ent)
    {
        var variation = _randomPredicted.NextFloatForEntity(ent, 0f, (float) FlickVariation.TotalSeconds);
        var additionalTime = FlickInterval.TotalSeconds - variation;
        ent.Comp.NextFlickTime = Timing.CurTime + TimeSpan.FromSeconds(additionalTime);

        Dirty(ent);
    }
}
