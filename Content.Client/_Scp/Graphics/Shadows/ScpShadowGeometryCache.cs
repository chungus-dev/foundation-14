using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.Client._Scp.Graphics.Shadows;

/// <summary>
/// Exact inputs for the world-space sprite shadow geometry of one light.
/// Render-only light properties and the camera are intentionally excluded.
/// </summary>
internal readonly record struct ScpCasterGeometryCacheKey(
    EntityUid Owner,
    Vector2 Position,
    Vector2 ProjectionPosition,
    float Radius,
    bool BuildOutsideMask);

/// <summary>
/// Exact inputs for the world-space stock occluder geometry of one light.
/// </summary>
internal readonly record struct ScpOccluderGeometryCacheKey(
    EntityUid Owner,
    Vector2 Position,
    float Radius);

/// <summary>
/// Exact inputs for relocating and clipping one light into the temporary atlas.
/// </summary>
internal readonly record struct ScpAtlasGeometryCacheKey(
    EntityUid Owner,
    uint CasterRevision,
    uint OccluderRevision,
    Matrix3x2 SourceRelativeTargetMatrix,
    Vector2i SourceSize);

/// <summary>
/// Tracks a value-type cache key without hashing. A revision changes only after
/// the rebuilt value has been committed successfully.
/// </summary>
internal struct ScpGeometryCacheState<TKey>
    where TKey : struct, IEquatable<TKey>
{
    private TKey _key;
    private bool _initialized;

    public uint Revision { get; private set; }

    public readonly bool IsCurrent(in TKey key)
    {
        return _initialized && _key.Equals(key);
    }

    public void Commit(in TKey key)
    {
        _key = key;
        _initialized = true;
        Revision = unchecked(Revision + 1);
    }
}
