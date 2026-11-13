using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Coherent terrain-mesh publication. Editing a lattice sample shared by
/// neighbouring chunks must never publish one neighbour's new triangles while
/// its neighbour still displays an older contour on the same boundary.
///
/// This does NOT generate new terrain or change saves, network messages,
/// resource accounting, or the shape-swap protocol. Only presentation changes:
/// each affected set is sampled once, built over a bounded number of frames,
/// then published in one synchronous Update call.
/// </summary>
public partial class World
{
    private sealed class SeamStage
    {
        public Chunk Target;
        public long Version;
        public float[,,] Density;
        public Chunk.PreparedMesh Prepared;
    }

    private sealed class SeamWave
    {
        public readonly HashSet<Vector3Int> Coordinates = new HashSet<Vector3Int>();
        public readonly Dictionary<Vector3Int, SeamStage> Stages = new Dictionary<Vector3Int, SeamStage>();
        public readonly List<Vector3Int> Order = new List<Vector3Int>();
        public int Cursor;
    }

    private readonly List<SeamWave> _seamPending = new List<SeamWave>();
    private readonly Dictionary<Vector3Int, SeamWave> _seamQueuedByChunk =
        new Dictionary<Vector3Int, SeamWave>();
    private SeamWave _seamActive;
    private readonly List<Vector3Int> _seamTouchedScratch = new List<Vector3Int>(8);
    // Reuse frozen snapshot arrays. Each is 17^3 samples at the default chunk
    // resolution; allocating these on every mining hit would create GC spikes.
    private readonly Stack<float[,,]> _seamDensityPool = new Stack<float[,,]>();
    private const int SeamDensityPoolLimit = 64;

    private float[,,] RentSeamDensitySnapshot(Vector3Int coord)
    {
        float[,,] snapshot = _seamDensityPool.Count > 0 ? _seamDensityPool.Pop() :
            new float[chunkSize.x + 1, chunkSize.y + 1, chunkSize.z + 1];
        if (snapshot.GetLength(0) != chunkSize.x + 1 ||
            snapshot.GetLength(1) != chunkSize.y + 1 ||
            snapshot.GetLength(2) != chunkSize.z + 1)
            snapshot = new float[chunkSize.x + 1, chunkSize.y + 1, chunkSize.z + 1];
        _terrain.FillChunk(coord, snapshot);
        return snapshot;
    }

    private void RecycleSeamSnapshot(SeamStage stage)
    {
        if (stage.Density != null &&
            (stage.Target == null || !object.ReferenceEquals(stage.Target.GetDensities(), stage.Density)) &&
            _seamDensityPool.Count < SeamDensityPoolLimit)
            _seamDensityPool.Push(stage.Density);
        stage.Density = null;
    }

    // Read-only counters for editor diagnostics. Unlike mesh queue length, the
    // cohort count describes how many publication boundaries are pending.
    public int PendingSeamPublicationGroups => _seamPending.Count + (_seamActive != null ? 1 : 0);
    public int PendingSeamPublicationChunks => _dirtyMeshes.Count;
    public int AtomicTerrainPublications { get; private set; }
    public int AtomicTerrainRetriedStages { get; private set; }

    private void QueueSeamSafeMeshEdit(Vector3Int sample)
    {
        SeamWave wave = null;
        List<Vector3Int> touched = _seamTouchedScratch;
        touched.Clear();
        foreach (Vector3Int coord in _terrain.ChunksSharingSample(sample))
        {
            touched.Add(coord);
            _meshFence.MarkDirty(coord);
            _dirtyMeshes.Add(coord);
            SeamWave other;
            if (!_seamQueuedByChunk.TryGetValue(coord, out other)) continue;
            if (wave == null) wave = other;
            else if (!object.ReferenceEquals(wave, other))
            {
                // Transitive grouping is necessary: A-B and B-C must be
                // published together, never as A-B and then B-C.
                foreach (Vector3Int member in other.Coordinates)
                {
                    wave.Coordinates.Add(member);
                    _seamQueuedByChunk[member] = wave;
                }
                _seamPending.Remove(other);
            }
        }
        if (wave == null)
        {
            wave = new SeamWave();
            _seamPending.Add(wave);
        }
        foreach (Vector3Int coord in touched)
        {
            wave.Coordinates.Add(coord);
            _seamQueuedByChunk[coord] = wave;
        }
    }

    private void QueueSeamSafeSingleChunk(Vector3Int coord)
    {
        if (!_dirtyMeshes.Contains(coord) || _seamQueuedByChunk.ContainsKey(coord)) return;
        var wave = new SeamWave();
        wave.Coordinates.Add(coord);
        _seamPending.Add(wave);
        _seamQueuedByChunk[coord] = wave;
    }

    private static int CompareSeamCoords(Vector3Int a, Vector3Int b)
    {
        int x = a.x.CompareTo(b.x);
        if (x != 0) return x;
        int y = a.y.CompareTo(b.y);
        return y != 0 ? y : a.z.CompareTo(b.z);
    }

    private bool TryBeginSeamWave()
    {
        for (int i = 0; i < _seamPending.Count; i++)
        {
            SeamWave wave = _seamPending[i];
            bool blockedByShape = false;
            foreach (Vector3Int coord in wave.Coordinates)
            {
                if (_dirtyMeshes.Contains(coord) && IsChunkHeldForShapeSwap(coord))
                { blockedByShape = true; break; }
            }
            if (blockedByShape) continue;
            _seamPending.RemoveAt(i);
            foreach (Vector3Int coord in wave.Coordinates)
                if (_seamQueuedByChunk.TryGetValue(coord, out SeamWave current) &&
                    object.ReferenceEquals(current, wave)) _seamQueuedByChunk.Remove(coord);

            // No yields here: every array is a view of the SAME density revision.
            // Later edits form a fresh wave rather than modifying this snapshot.
            foreach (Vector3Int coord in wave.Coordinates)
            {
                if (!_dirtyMeshes.Contains(coord)) continue;
                if (_activeChunks.TryGetValue(coord, out Chunk chunk) &&
                    chunk != null && chunk.gameObject.activeInHierarchy)
                {
                    wave.Stages.Add(coord, new SeamStage
                    {
                        Target = chunk,
                        Version = _meshFence.Capture(coord),
                        Density = RentSeamDensitySnapshot(coord)
                    });
                    wave.Order.Add(coord);
                }
                else
                {
                    // Sleeping/unloaded chunks are regenerated from the central
                    // density store when they return, with no old mesh to show.
                    _dirtyMeshes.Remove(coord);
                }
            }
            wave.Order.Sort(CompareSeamCoords);
            if (wave.Order.Count == 0) { i--; continue; }
            _seamActive = wave;
            return true;
        }
        return false;
    }

    private void FlushSeamSafeMeshes()
    {
        // Compatibility with the existing shape cancellation code, which
        // directly enqueues dirty coordinates when a shape transfer fails.
        while (_dirtyMeshOrder.Count > 0)
            QueueSeamSafeSingleChunk(_dirtyMeshOrder.Dequeue());

        if (_seamActive == null && !TryBeginSeamWave()) return;
        SeamWave wave = _seamActive;
        // A new shape may have acquired one of our chunks since we snapped it.
        // Never overwrite an atomic shape handoff with an older mesh.
        foreach (Vector3Int coord in wave.Order)
        {
            if (IsChunkHeldForShapeSwap(coord))
            {
                CancelSeamWave(wave);
                return;
            }
        }
        int built = 0;
        int maxBuilds = Mathf.Max(1, meshRebuildsPerFrame);
        float milliseconds = Mathf.Max(0.1f, meshMillisecondsPerFrame);
        while (wave.Cursor < wave.Order.Count && built < maxBuilds)
        {
            if (built > 0 && _meshWatch.Elapsed.TotalMilliseconds >= milliseconds) break;
            Vector3Int coord = wave.Order[wave.Cursor++];
            SeamStage stage = wave.Stages[coord];
            Chunk stillActive;
            if (!_activeChunks.TryGetValue(coord, out stillActive) ||
                !object.ReferenceEquals(stillActive, stage.Target) || !stillActive.gameObject.activeInHierarchy)
            {
                // Chunk unloaded/reused while the snapshot was being staged.
                continue;
            }
            stage.Prepared = stillActive.PrepareMeshWithoutPublishing(stage.Density);
            built++;
        }
        if (wave.Cursor < wave.Order.Count) return;

        // All staged meshes become visible in the same Unity frame. There is
        // no partial publish, even if preparing eight chunks took two frames.
        foreach (Vector3Int coord in wave.Order)
        {
            SeamStage stage = wave.Stages[coord];
            if (stage.Prepared == null) continue;
            Chunk live;
            if (_activeChunks.TryGetValue(coord, out live) &&
                object.ReferenceEquals(live, stage.Target) && live.gameObject.activeInHierarchy)
            {
                live.PublishPreparedMesh(stage.Prepared);
                _meshFence.MarkBuiltAtVersion(coord, stage.Version);
                if (_meshFence.Capture(coord) == stage.Version) _dirtyMeshes.Remove(coord);
                // Newer edits remain dirty and are already queued in a new wave.
                OnTerrainColliderRebuilt(coord);
            }
            else stage.Prepared.Dispose();
            stage.Prepared = null;
            RecycleSeamSnapshot(stage);
        }
        _seamActive = null;
        AtomicTerrainPublications++;
    }

    private void CancelSeamWave(SeamWave wave)
    {
        foreach (SeamStage stage in wave.Stages.Values)
        {
            if (stage.Prepared != null) stage.Prepared.Dispose();
            RecycleSeamSnapshot(stage);
        }
        _seamActive = null;
        AtomicTerrainRetriedStages++;
        // Re-queue only still-dirty chunks. The shape owns its own grouped
        // publication and may already have satisfied some of these versions.
        foreach (Vector3Int coord in wave.Coordinates)
            QueueSeamSafeSingleChunk(coord);
    }

    private void ResetSeamSafeMeshes()
    {
        if (_seamActive != null)
            foreach (SeamStage stage in _seamActive.Stages.Values)
            {
                if (stage.Prepared != null) stage.Prepared.Dispose();
                RecycleSeamSnapshot(stage);
            }
        _seamActive = null;
        _seamPending.Clear();
        _seamQueuedByChunk.Clear();
        _seamDensityPool.Clear();
        _seamTouchedScratch.Clear();
        AtomicTerrainPublications = 0;
        AtomicTerrainRetriedStages = 0;
    }
}
