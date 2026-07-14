using Robust.Shared.Threading;

namespace Content.Client._Scp.Graphics.Shadows;

public sealed partial class ScpShadowCasterSystem
{
    #region Parallel geometry scheduling

    [Dependency] private IParallelManager _parallel = default!;

    internal int GeometryBatchSize => Math.Clamp(_parallel.ParallelProcessCount * 2, 1, 16);

    internal void ProcessGeometryBatch(
        IParallelRobustJob job,
        int lightCount,
        long intersectionChecks)
    {
        if (_parallel.ParallelProcessCount < 2 || lightCount < 2 || intersectionChecks < 512)
        {
            _parallel.ProcessSerialNow(job, lightCount);
            return;
        }

        _parallel.ProcessNow(job, lightCount);
    }

    #endregion
}
