using System.Runtime.CompilerServices;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Supplies allocation-free exact equality and hashing for a hot render snapshot.
/// Implementations should hash primitive fields directly instead of delegating to
/// composite <see cref="object.GetHashCode"/> implementations.
/// </summary>
internal interface IScpExactSnapshotComparer<T>
{
    int ElementSizeBytes { get; }

    bool AreEqual(in T left, in T right);

    ulong AddToHash(ulong hash, in T value);
}

internal static class ScpExactSnapshotHash
{
    public const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Mix(ulong hash, uint value)
    {
        return (hash ^ value) * Prime;
    }
}

/// <summary>
/// Keeps an exact, allocation-free-after-growth copy of a render input sequence.
/// Stable inputs take the exact comparison fast path without rehashing. The hash
/// is updated only on a miss and is never trusted without an exact comparison.
/// </summary>
internal sealed class ScpExactSnapshot<T, TComparer>
    where TComparer : struct, IScpExactSnapshotComparer<T>
{
    private T[] _values = [];
    private int _count;
    private ulong _hash;
    private bool _initialized;

    public uint Revision { get; private set; }

    public int Count => _count;

    public long EstimatedBytes =>
        64L +
        24L +
        (long) _values.Length * default(TComparer).ElementSizeBytes;

    public T this[int index]
    {
        get
        {
            if ((uint) index >= (uint) _count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _values[index];
        }
    }

    public bool Update(ReadOnlySpan<T> values)
    {
        if (IsCurrent(values, out var hash))
            return false;

        Commit(values, hash);
        return true;
    }

    public bool IsCurrent(ReadOnlySpan<T> values, out ulong hash)
    {
        var comparer = default(TComparer);
        var exact = _initialized && values.Length == _count;
        for (var i = 0; exact && i < values.Length; i++)
        {
            var value = values[i];
            var cached = _values[i];
            exact = comparer.AreEqual(in value, in cached);
        }

        if (exact)
        {
            hash = _hash;
            return true;
        }

        // The hash is retained for the precomputed-hash entry point and is only
        // paid for when the exact render input actually changed.
        hash = ScpExactSnapshotHash.Mix(ScpExactSnapshotHash.Offset, (uint) values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            // Local values avoid unverifiable managed-reference IL for generic
            // ReadOnlySpan/array indexers in the Content sandbox.
            var value = values[i];
            hash = comparer.AddToHash(hash, in value);
        }

        return false;
    }

    public void Commit(ReadOnlySpan<T> values, ulong hash)
    {
        var previousCount = _count;
        EnsureCapacity(values.Length);
        values.CopyTo(_values);
        if (values.Length < previousCount && RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Array.Clear(_values, values.Length, previousCount - values.Length);
        _count = values.Length;
        _hash = hash;
        _initialized = true;
        Revision = unchecked(Revision + 1);
    }

    /// <summary>
    /// Test and precomputed-hash entry point. Exact equality still confirms a
    /// matching hash, including deliberately forced collisions.
    /// </summary>
    internal bool Update(ReadOnlySpan<T> values, ulong hash)
    {
        var comparer = default(TComparer);
        var exact = _initialized && hash == _hash && values.Length == _count;
        for (var i = 0; exact && i < values.Length; i++)
        {
            var value = values[i];
            var cached = _values[i];
            exact = comparer.AreEqual(in value, in cached);
        }

        return CommitIfChanged(values, hash, exact);
    }

    private bool CommitIfChanged(ReadOnlySpan<T> values, ulong hash, bool exact)
    {
        if (_initialized && hash == _hash && exact)
            return false;

        Commit(values, hash);
        return true;
    }

    private void EnsureCapacity(int count)
    {
        if (_values.Length >= count)
            return;

        var capacity = Math.Max(count, Math.Max(4, _values.Length * 2));
        Array.Resize(ref _values, capacity);
    }
}
