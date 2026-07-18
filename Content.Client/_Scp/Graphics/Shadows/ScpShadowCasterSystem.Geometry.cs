using Robust.Shared.Threading;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterSystem
{
    #region Parallel geometry scheduling

    [Dependency] private IParallelManager _parallel = default!;

    internal void ProcessGeometryBatch(
        IParallelRobustJob job,
        int lightCount,
        int validLightCount,
        long intersectionChecks)
    {
        if (_parallel.ParallelProcessCount < 2 || validLightCount < 2 || intersectionChecks < 512)
        {
            _parallel.ProcessSerialNow(job, lightCount);
            return;
        }

        _parallel.ProcessNow(job, lightCount);
    }

    #endregion
}
