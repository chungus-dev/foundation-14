using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

internal readonly record struct ScpGeometryEntityKey(
    EntityUid Owner,
    NetEntity NetIdentity);

/// <summary>
/// Identifies one incarnation of a geometry source. The network identity catches
/// local UID replacement, while <see cref="Generation"/> changes only after the
/// source has been evicted from the bounded exact cache. A regular PVS leave and
/// re-entry keeps the same incarnation and reuses its exact buffers.
/// </summary>
internal readonly record struct ScpGeometrySourceIdentity(
    EntityUid Owner,
    NetEntity NetIdentity,
    uint Generation);

/// <summary>
/// Tracks whether a retained exact geometry snapshot participates in the current
/// active PVS set. Missing/reappearing transitions are reported once without
/// discarding the snapshot's identity or reusable arrays.
/// </summary>
internal struct ScpGeometrySnapshotResidency
{
    public bool Active { get; private set; }
    public uint LastSeenEpoch { get; private set; }

    /// <returns>Whether the snapshot was active before this observation.</returns>
    public bool MarkSeen(uint epoch)
    {
        var wasActive = Active;
        Active = true;
        LastSeenEpoch = epoch;
        return wasActive;
    }

    /// <returns>Whether this call changed an active snapshot to inactive.</returns>
    public bool MarkMissing()
    {
        if (!Active)
            return false;

        Active = false;
        return true;
    }

    public bool WasSeen(uint epoch)
    {
        return Active && LastSeenEpoch == epoch;
    }
}

internal readonly record struct ScpGeometrySnapshotEvictionCandidate(
    ScpGeometryEntityKey Key,
    uint LastSeenEpoch,
    bool ForceEvict = false);

/// <summary>
/// Deterministic bound for inactive PVS snapshot records. Epochs advance only
/// when the corresponding canonical geometry set changes, so retention is not
/// shortened on clients rendering at a high frame rate.
/// </summary>
internal static class ScpGeometrySnapshotRetention
{
    internal const uint RetentionEpochs = 600;
    internal const int MaximumInactiveRecords = 512;
    private static readonly Comparison<ScpGeometrySnapshotEvictionCandidate> EvictionComparison =
        CompareEvictionCandidates;

    public static int SortAndGetRemovalCount(
        List<ScpGeometrySnapshotEvictionCandidate> candidates,
        uint currentEpoch)
    {
        var requiresOrdering = candidates.Count > MaximumInactiveRecords;
        if (!requiresOrdering)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate.ForceEvict ||
                    unchecked(currentEpoch - candidate.LastSeenEpoch) >= RetentionEpochs)
                {
                    requiresOrdering = true;
                    break;
                }
            }
        }

        // PVS churn changes the canonical frame snapshot often, but most retained
        // entries are still comfortably below both bounds. Keep that common path
        // linear and avoid re-sorting the whole inactive cache every changed frame.
        if (!requiresOrdering)
            return 0;

        candidates.Sort(EvictionComparison);

        var required = 0;
        while (required < candidates.Count && candidates[required].ForceEvict)
            required++;

        var expired = required;
        while (expired < candidates.Count &&
               unchecked(currentEpoch - candidates[expired].LastSeenEpoch) >= RetentionEpochs)
        {
            expired++;
        }

        return Math.Max(expired, candidates.Count - MaximumInactiveRecords);
    }

    private static int CompareEvictionCandidates(
        ScpGeometrySnapshotEvictionCandidate left,
        ScpGeometrySnapshotEvictionCandidate right)
    {
        if (left.ForceEvict != right.ForceEvict)
            return left.ForceEvict ? -1 : 1;

        // Snapshot caches are cleared if their epoch wraps, so ordinary
        // unsigned ordering is exact for every live candidate set.
        var comparison = left.LastSeenEpoch.CompareTo(right.LastSeenEpoch);
        if (comparison != 0)
            return comparison;

        comparison = left.Key.Owner.CompareTo(right.Key.Owner);
        return comparison != 0
            ? comparison
            : left.Key.NetIdentity.Id.CompareTo(right.Key.NetIdentity.Id);
    }
}

/// <summary>
/// Exact dependency stored by one light. A source revision changes only when its
/// canonical world geometry changes.
/// </summary>
internal readonly record struct ScpGeometryDependency(
    ScpGeometrySourceIdentity Identity,
    uint SnapshotRevision);

/// <summary>
/// Conservative old/new spatial footprint of one changed geometry source.
/// </summary>
internal readonly record struct ScpGeometrySourceChange(
    EntityUid Owner,
    bool HadPrevious,
    Box2 PreviousBounds,
    bool HasCurrent,
    Box2 CurrentBounds)
{
    public bool Intersects(Vector2 lightPosition, float lightRadius)
    {
        var lightCircle = new Circle(lightPosition, lightRadius);
        return HadPrevious && lightCircle.Intersects(PreviousBounds) ||
            HasCurrent && lightCircle.Intersects(CurrentBounds);
    }
}

internal static class ScpGeometrySourceEpoch
{
    public static bool HasOnlyLatestChanges(uint validatedEpoch, uint currentEpoch)
    {
        return unchecked(validatedEpoch + 1) == currentEpoch;
    }
}

/// <summary>
/// Transactional exact snapshot of an ordered light dependency sequence.
/// Validation never mutates committed state, so a failed geometry job cannot
/// make stale geometry look current. Hashes are rejection-only: equal hashes are
/// always confirmed element by element.
/// </summary>
internal sealed class ScpOrderedGeometryDependencyCache
{
    private ScpGeometryDependency[] _dependencies = [];
    private int _count;
    private ulong _hash;
    private bool _initialized;

    public uint Revision { get; private set; }

    public long EstimatedBytes => 64L + (long) _dependencies.Length * 24L;

    public bool IsCurrent(ReadOnlySpan<ScpGeometryDependency> dependencies, out ulong hash)
    {
        var exact = _initialized && dependencies.Length == _count;
        for (var i = 0; exact && i < dependencies.Length; i++)
        {
            var current = dependencies[i];
            var committed = _dependencies[i];
            exact = AreEqual(in current, in committed);
        }

        if (exact)
        {
            hash = _hash;
            return true;
        }

        // Callers only need a new hash when they are about to commit a changed
        // dependency sequence. Stable lights avoid redundant FNV work entirely.
        hash = ScpExactSnapshotHash.Mix(ScpExactSnapshotHash.Offset, (uint) dependencies.Length);
        for (var i = 0; i < dependencies.Length; i++)
        {
            var current = dependencies[i];
            hash = AddToHash(hash, in current);
        }

        return false;
    }

    internal bool IsCurrent(ReadOnlySpan<ScpGeometryDependency> dependencies, ulong forcedHash)
    {
        if (!_initialized || forcedHash != _hash || dependencies.Length != _count)
            return false;

        for (var i = 0; i < dependencies.Length; i++)
        {
            var current = dependencies[i];
            var committed = _dependencies[i];
            if (!AreEqual(in current, in committed))
                return false;
        }

        return true;
    }

    public void Commit(ReadOnlySpan<ScpGeometryDependency> dependencies, ulong hash)
    {
        EnsureCapacity(dependencies.Length);
        dependencies.CopyTo(_dependencies);
        _count = dependencies.Length;
        _hash = hash;
        _initialized = true;
        Revision = unchecked(Revision + 1);
    }

    private static ulong AddToHash(ulong hash, in ScpGeometryDependency dependency)
    {
        hash = ScpExactSnapshotHash.Mix(hash, unchecked((uint) dependency.Identity.Owner.Id));
        hash = ScpExactSnapshotHash.Mix(hash, unchecked((uint) dependency.Identity.NetIdentity.Id));
        hash = ScpExactSnapshotHash.Mix(hash, dependency.Identity.Generation);
        hash = ScpExactSnapshotHash.Mix(hash, dependency.SnapshotRevision);
        return hash;
    }

    private static bool AreEqual(in ScpGeometryDependency left, in ScpGeometryDependency right)
    {
        return left.Identity.Owner == right.Identity.Owner &&
            left.Identity.NetIdentity == right.Identity.NetIdentity &&
            left.Identity.Generation == right.Identity.Generation &&
            left.SnapshotRevision == right.SnapshotRevision;
    }

    private void EnsureCapacity(int count)
    {
        if (_dependencies.Length >= count)
            return;

        var capacity = Math.Max(count, Math.Max(4, _dependencies.Length * 2));
        Array.Resize(ref _dependencies, capacity);
    }
}

/// <summary>
/// Combines an exact scalar header and two exact variable-sized sequences into
/// one monotonic per-entity revision. Storage grows to a high-water mark and is
/// reused afterwards.
/// </summary>
internal sealed class ScpGeometryEntityRevisionState<
    THeader,
    TPartA,
    TPartAComparer,
    TPartB,
    TPartBComparer>
    where THeader : struct, IEquatable<THeader>
    where TPartAComparer : struct, IScpExactSnapshotComparer<TPartA>
    where TPartBComparer : struct, IScpExactSnapshotComparer<TPartB>
{
    private readonly ScpExactSnapshot<TPartA, TPartAComparer> _partA = new();
    private readonly ScpExactSnapshot<TPartB, TPartBComparer> _partB = new();
    private THeader _header;
    private bool _initialized;

    public uint Revision { get; private set; }

    public long EstimatedBytes => 64L + _partA.EstimatedBytes + _partB.EstimatedBytes;

    public bool Update(
        in THeader header,
        ReadOnlySpan<TPartA> partA,
        ReadOnlySpan<TPartB> partB)
    {
        var changed = !_initialized || !_header.Equals(header);
        changed |= _partA.Update(partA);
        changed |= _partB.Update(partB);
        return CommitHeaderIfChanged(in header, changed);
    }

    internal bool Update(
        in THeader header,
        ReadOnlySpan<TPartA> partA,
        ulong partAHash,
        ReadOnlySpan<TPartB> partB,
        ulong partBHash)
    {
        var changed = !_initialized || !_header.Equals(header);
        changed |= _partA.Update(partA, partAHash);
        changed |= _partB.Update(partB, partBHash);
        return CommitHeaderIfChanged(in header, changed);
    }

    private bool CommitHeaderIfChanged(in THeader header, bool changed)
    {
        if (!changed)
            return false;

        _header = header;
        _initialized = true;
        Revision = unchecked(Revision + 1);
        return true;
    }
}
