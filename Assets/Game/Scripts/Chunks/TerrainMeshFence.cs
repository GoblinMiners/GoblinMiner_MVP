using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A rubble request depends on the mesh version for ITS edit, not on a chunk
/// staying clean forever. Later removal-only edits cannot invalidate that version.
/// </summary>
public sealed class TerrainMeshFence
{
    private readonly Dictionary<Vector3Int, long> _wanted = new Dictionary<Vector3Int, long>();
    private readonly Dictionary<Vector3Int, long> _built = new Dictionary<Vector3Int, long>();
    private long _clock;
    public void Clear() { _wanted.Clear(); _built.Clear(); _clock = 0; }
    public void MarkDirty(Vector3Int chunk) { _wanted[chunk] = ++_clock; }
    public long Capture(Vector3Int chunk) { long value; return _wanted.TryGetValue(chunk, out value) ? value : 0; }
    public void MarkBuilt(Vector3Int chunk) { MarkBuiltAtVersion(chunk, Capture(chunk)); }
    // A prepared mesh may represent a frozen earlier revision. Later edits must
    // keep their own fence, not be falsely marked as rebuilt.
    public void MarkBuiltAtVersion(Vector3Int chunk, long version)
    {
        long current;
        if (!_built.TryGetValue(chunk, out current) || current < version) _built[chunk] = version;
    }
    public bool IsSatisfied(Vector3Int chunk, long version)
    {
        long value;
        return version <= 0 || (_built.TryGetValue(chunk, out value) && value >= version);
    }
}
