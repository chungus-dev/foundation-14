using System.Numerics;
using System.Runtime.CompilerServices;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

internal enum ScpCasterContourSourceKind : byte
{
    None,
    Rsi,
    Texture,
}

/// <summary>
/// Exact input for one sprite layer's world-space shadow contour. The source is
/// compared by identity because loaded RSI and texture resources are immutable;
/// the selected state, direction and frame identify the exact alpha region.
/// </summary>
internal readonly record struct ScpCasterLayerGeometrySnapshot(
    int LayerIndex,
    ScpCasterContourSourceKind SourceKind,
    object? Source,
    string? State,
    byte Direction,
    int Frame,
    Matrix3x2 WorldMatrix);

/// <summary>
/// Inputs shared by the fallback bounds path and every detailed layer path.
/// Viewport position and bounds are deliberately absent: only a matrix that
/// actually changes the rendered silhouette invalidates retained geometry.
/// </summary>
internal readonly record struct ScpCasterWorldGeometryHeader(
    ScpShadowQuality Quality,
    Box2 FallbackBounds,
    Matrix3x2 FallbackWorldMatrix);

internal readonly record struct ScpCasterWorldGeometryPendingCommit(
    ulong LayerHash,
    bool LayersChanged);

/// <summary>
/// Allocation-free-after-growth exact snapshot for retained caster geometry.
/// Hashes are computed only after an exact miss and are never accepted without
/// comparing every field.
/// </summary>
internal sealed class ScpCasterWorldGeometryInputState
{
    private readonly ScpExactSnapshot<
        ScpCasterLayerGeometrySnapshot,
        LayerSnapshotComparer> _layers = new();
    private ScpCasterWorldGeometryHeader _header;
    private bool _initialized;

    public uint Revision { get; private set; }

    public long EstimatedBytes => 64L + _layers.EstimatedBytes;

    public bool Update(
        in ScpCasterWorldGeometryHeader header,
        ReadOnlySpan<ScpCasterLayerGeometrySnapshot> layers,
        bool fallbackGeometryRelevant = true)
    {
        if (IsCurrent(
                in header,
                layers,
                fallbackGeometryRelevant,
                out var pendingCommit))
        {
            return false;
        }

        Commit(in header, layers, in pendingCommit);
        return true;
    }

    public bool IsCurrent(
        in ScpCasterWorldGeometryHeader header,
        ReadOnlySpan<ScpCasterLayerGeometrySnapshot> layers,
        bool fallbackGeometryRelevant,
        out ScpCasterWorldGeometryPendingCommit pendingCommit)
    {
        var headerCurrent = _initialized &&
            _header.Quality == header.Quality &&
            (!fallbackGeometryRelevant ||
             _header.FallbackBounds.Equals(header.FallbackBounds) &&
             _header.FallbackWorldMatrix.Equals(header.FallbackWorldMatrix));
        var layersCurrent = _layers.IsCurrent(layers, out var layerHash);
        pendingCommit = new ScpCasterWorldGeometryPendingCommit(layerHash, !layersCurrent);
        return headerCurrent && layersCurrent;
    }

    public void Commit(
        in ScpCasterWorldGeometryHeader header,
        ReadOnlySpan<ScpCasterLayerGeometrySnapshot> layers,
        in ScpCasterWorldGeometryPendingCommit pendingCommit)
    {
        if (pendingCommit.LayersChanged)
            _layers.Commit(layers, pendingCommit.LayerHash);

        _header = header;
        _initialized = true;
        Revision = unchecked(Revision + 1);
    }

    private readonly struct LayerSnapshotComparer : IScpExactSnapshotComparer<ScpCasterLayerGeometrySnapshot>
    {
        public int ElementSizeBytes => 56;

        public bool AreEqual(
            in ScpCasterLayerGeometrySnapshot left,
            in ScpCasterLayerGeometrySnapshot right)
        {
            return left.LayerIndex == right.LayerIndex &&
                left.SourceKind == right.SourceKind &&
                ReferenceEquals(left.Source, right.Source) &&
                string.Equals(left.State, right.State, StringComparison.Ordinal) &&
                left.Direction == right.Direction &&
                left.Frame == right.Frame &&
                left.WorldMatrix.Equals(right.WorldMatrix);
        }

        public ulong AddToHash(ulong hash, in ScpCasterLayerGeometrySnapshot value)
        {
            hash = Mix(hash, value.LayerIndex);
            hash = Mix(hash, (int) value.SourceKind);
            hash = Mix(hash, value.Source == null ? 0 : RuntimeHelpers.GetHashCode(value.Source));
            hash = Mix(hash, value.State == null ? 0 : StringComparer.Ordinal.GetHashCode(value.State));
            hash = Mix(hash, value.Direction);
            hash = Mix(hash, value.Frame);
            hash = Mix(hash, value.WorldMatrix.M11.GetHashCode());
            hash = Mix(hash, value.WorldMatrix.M12.GetHashCode());
            hash = Mix(hash, value.WorldMatrix.M21.GetHashCode());
            hash = Mix(hash, value.WorldMatrix.M22.GetHashCode());
            hash = Mix(hash, value.WorldMatrix.M31.GetHashCode());
            return Mix(hash, value.WorldMatrix.M32.GetHashCode());
        }

        private static ulong Mix(ulong hash, int value)
        {
            return ScpExactSnapshotHash.Mix(hash, unchecked((uint) value));
        }
    }
}
