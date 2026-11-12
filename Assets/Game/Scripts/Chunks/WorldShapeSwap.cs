using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>
/// Shape disconnection is a two-phase visual transaction:
/// (1) continue showing old, collision-ready terrain while edits, mesh builds and
///     reliable shape transfer finish;
/// (2) publish ALL affected terrain meshes and activate the prepared Rigidbody
///     in the same Unity update. Overlapping ceiling pieces commit as a group.
/// Density changes remain authoritative throughout; this is a presentation fence.
/// </summary>
public partial class World
{
    [Header("Shape release performance")]
    [Tooltip("Maximum terrain chunks prepared offscreen in one frame. The final handoff is atomic.")]
    [Range(1, 8)] public int shapeStagedChunksPerFrame = 2;
    [Tooltip("Soft per-frame staging budget. One mesh may still exceed this value.")]
    [Min(0.1f)] public float shapeStagingMillisecondsPerFrame = 3f;

    private sealed class ShapeSwap
    {
        public int Id;
        public TerrainShapeRecord Record;
        public TerrainShapeMeshJob Mesh;
        public TerrainShapeChunk PreparedShape;
        public bool RemovalComplete;
        public float StartTime;
        public float RemovalFinishedAt;
        public float PreparedAt;
        public readonly HashSet<Vector3Int> Chunks = new HashSet<Vector3Int>();
    }
    private sealed class StagedChunk
    {
        public Chunk Chunk;
        public Chunk.PreparedMesh Prepared;
        public long Revision;
    }
    private readonly Dictionary<int, ShapeSwap> _shapeSwaps = new Dictionary<int, ShapeSwap>();
    private readonly Dictionary<Vector3Int, HashSet<int>> _shapeHeldChunks =
        new Dictionary<Vector3Int, HashSet<int>>();
    private readonly Dictionary<Vector3Int, StagedChunk> _shapeStagedChunks =
        new Dictionary<Vector3Int, StagedChunk>();
    private readonly HashSet<int> _shapeSwapGroup = new HashSet<int>();
    private readonly HashSet<Vector3Int> _shapeSwapGroupChunks = new HashSet<Vector3Int>();
    private readonly Queue<int> _shapeSwapSearch = new Queue<int>();
    private readonly System.Diagnostics.Stopwatch _shapeStageWatch = new System.Diagnostics.Stopwatch();
    private readonly Dictionary<int, KeyValuePair<Vector3, Quaternion>> _shapePendingPoses =
        new Dictionary<int, KeyValuePair<Vector3, Quaternion>>();
    public int PendingVisualShapeSwaps => _shapeSwaps.Count;
    public int HeldTerrainChunkViews => _shapeHeldChunks.Count;
    public int PreparedTerrainChunkViews => _shapeStagedChunks.Count;
    public float LastShapeReleaseSeconds { get; private set; }
    public float LastShapeRemovalSeconds { get; private set; }
    public int AtomicShapeReleases { get; private set; }

    private bool IsChunkHeldForShapeSwap(Vector3Int coord) => _shapeHeldChunks.ContainsKey(coord);

    private void BeginShapeSwapOnServer(TerrainShapeRecord record)
    {
        if (record == null || record.Samples.Count == 0) return;
        var affected = new HashSet<Vector3Int>();
        foreach (TerrainShapeSample sample in record.Samples)
        {
            if (sample.Density >= 0f) continue;
            foreach (Vector3Int chunk in _terrain.ChunksSharingSample(sample.Grid))
                affected.Add(chunk);
        }
        var chunks = new Vector3Int[affected.Count];
        affected.CopyTo(chunks);
        BeginShapeSwapLocally(record.Id, chunks);
        // Reliable: this message is sent BEFORE any density changes for the
        // detached component, so remote players do not rebuild it piecemeal.
        RpcBeginShapeSwap(record.Id, chunks);
    }

    private void BeginShapeSwapLocally(int id, Vector3Int[] chunks)
    {
        if (_shapeSwaps.ContainsKey(id) || _removedClientShapes.Contains(id)) return;
        var swap = new ShapeSwap { Id = id, StartTime = Time.unscaledTime };
        _shapeSwaps.Add(id, swap);
        if (chunks == null) return;
        foreach (Vector3Int coord in chunks)
        {
            if (!swap.Chunks.Add(coord)) continue;
            HashSet<int> owners;
            if (!_shapeHeldChunks.TryGetValue(coord, out owners))
            {
                owners = new HashSet<int>();
                _shapeHeldChunks.Add(coord, owners);
            }
            owners.Add(id);
        }
    }

    [ClientRpc]
    private void RpcBeginShapeSwap(int id, Vector3Int[] chunks)
    {
        if (isServer || _removedClientShapes.Contains(id)) return;
        // A joining client receives a full terrain baseline and then individual
        // shape records. It need not retain the old terrain visual during sync.
        if (!IsReady || _receivingSnapshot) return;
        BeginShapeSwapLocally(id, chunks);
    }

    private void FinishShapeSwapRemoval(TerrainShapeRecord record)
    {
        if (record == null) return;
        ShapeSwap swap;
        if (!_shapeSwaps.TryGetValue(record.Id, out swap)) return;
        swap.RemovalComplete = true;
        swap.RemovalFinishedAt = Time.unscaledTime;
        RpcFinishShapeSwapRemoval(record.Id);
    }

    [ClientRpc]
    private void RpcFinishShapeSwapRemoval(int id)
    {
        if (isServer) return;
        ShapeSwap swap;
        if (_shapeSwaps.TryGetValue(id, out swap))
        { swap.RemovalComplete = true; swap.RemovalFinishedAt = Time.unscaledTime; }
    }

    private void PrepareShapeForAtomicRelease(TerrainShapeRecord record, TerrainShapeMeshJob mesh)
    {
        ShapeSwap swap;
        if (!_shapeSwaps.TryGetValue(record.Id, out swap)) return;
        swap.Record = record;
        swap.Mesh = mesh;
        // Creating a complex mesh and collider proxy is expensive. Do that
        // while the original terrain is STILL on screen, not during the swap.
        var go = new GameObject("Gobmine_PreparedShape_" + record.Id);
        go.SetActive(false);
        TerrainShapeChunk piece = go.AddComponent<TerrainShapeChunk>();
        piece.Configure(record, mesh, this, isServer, shapeCollisionBoxes, shapeMaterialDensityKgPerM3);
        swap.PreparedShape = piece;
        swap.PreparedAt = Time.unscaledTime;
    }

    private bool GatherReadySwapGroup(int start)
    {
        _shapeSwapGroup.Clear(); _shapeSwapGroupChunks.Clear(); _shapeSwapSearch.Clear();
        _shapeSwapSearch.Enqueue(start);
        while (_shapeSwapSearch.Count > 0)
        {
            int id = _shapeSwapSearch.Dequeue();
            if (!_shapeSwapGroup.Add(id)) continue;
            ShapeSwap swap;
            if (!_shapeSwaps.TryGetValue(id, out swap) || !swap.RemovalComplete ||
                swap.PreparedShape == null || swap.Record == null || swap.Mesh == null || !swap.Mesh.Complete)
                return false;
            foreach (Vector3Int coord in swap.Chunks)
            {
                if (!_shapeSwapGroupChunks.Add(coord)) continue;
                HashSet<int> owners;
                if (!_shapeHeldChunks.TryGetValue(coord, out owners)) continue;
                foreach (int other in owners)
                    if (!_shapeSwapGroup.Contains(other)) _shapeSwapSearch.Enqueue(other);
            }
        }
        return _shapeSwapGroup.Count > 0;
    }

    /// <summary>
    /// At most one multi-shape release per frame. Staging remains resumable and
    /// the old mesh is visible until every mesh needed by the group is ready.
    /// </summary>
    private void PublishReadyShapeSwaps()
    {
        if (_shapeSwaps.Count == 0) return;
        _shapeStageWatch.Restart();
        int preparedThisFrame = 0;
        foreach (var entry in _shapeSwaps)
        {
            if (!GatherReadySwapGroup(entry.Key)) continue;
            bool complete = true;
            foreach (Vector3Int coord in _shapeSwapGroupChunks)
            {
                Chunk chunk;
                if (!_activeChunks.TryGetValue(coord, out chunk) || chunk == null ||
                    !chunk.gameObject.activeInHierarchy) continue;
                long revision = _meshFence.Capture(coord);
                StagedChunk staged;
                if (_shapeStagedChunks.TryGetValue(coord, out staged))
                {
                    if (staged.Chunk == chunk && staged.Revision == revision) continue;
                    staged.Prepared.Dispose();
                    _shapeStagedChunks.Remove(coord);
                }
                if (preparedThisFrame >= Mathf.Max(1, shapeStagedChunksPerFrame) ||
                    (preparedThisFrame > 0 &&
                     _shapeStageWatch.Elapsed.TotalMilliseconds >= shapeStagingMillisecondsPerFrame))
                { complete = false; break; }
                _terrain.FillChunk(coord, chunk.GetDensities());
                Chunk.PreparedMesh prepared = chunk.PrepareMeshWithoutPublishing();
                _shapeStagedChunks.Add(coord, new StagedChunk {
                    Chunk = chunk, Prepared = prepared, Revision = revision
                });
                preparedThisFrame++;
            }
            if (!complete) break;
            // Validate again: another mining operation may have changed a held
            // chunk between its staging pass and today's commit.
            foreach (Vector3Int coord in _shapeSwapGroupChunks)
            {
                Chunk chunk;
                if (!_activeChunks.TryGetValue(coord, out chunk) || chunk == null ||
                    !chunk.gameObject.activeInHierarchy) continue;
                StagedChunk staged;
                if (!_shapeStagedChunks.TryGetValue(coord, out staged) ||
                    staged.Chunk != chunk || staged.Revision != _meshFence.Capture(coord))
                { complete = false; break; }
            }
            if (!complete) break;

            // One visible frame: old chunk geometry disappears while complete
            // new terrain and complete detached physics geometry appear.
            foreach (Vector3Int coord in _shapeSwapGroupChunks)
            {
                StagedChunk staged;
                if (_shapeStagedChunks.TryGetValue(coord, out staged))
                {
                    Chunk live;
                    if (_activeChunks.TryGetValue(coord, out live) &&
                        live != null && live == staged.Chunk && live.gameObject.activeInHierarchy)
                    {
                        live.PublishPreparedMesh(staged.Prepared);
                        _meshFence.MarkBuilt(coord);
                        _dirtyMeshes.Remove(coord);
                    }
                    else staged.Prepared.Dispose(); // Sleeping/reused chunk: never publish into another coordinate.
                    _shapeStagedChunks.Remove(coord);
                }
            }
            foreach (int id in _shapeSwapGroup)
            {
                ShapeSwap swap = _shapeSwaps[id];
                LastShapeReleaseSeconds = Time.unscaledTime - swap.StartTime;
                LastShapeRemovalSeconds = swap.RemovalFinishedAt - swap.StartTime;
                AtomicShapeReleases++;
                TerrainShapeChunk piece = swap.PreparedShape;
                if (!isServer)
                {
                    KeyValuePair<Vector3,Quaternion> pose;
                    if (_shapePendingPoses.TryGetValue(id, out pose))
                        piece.SetPose(pose.Key, pose.Value);
                    _shapePendingPoses.Remove(id);
                }
                piece.ReleasePreparedShape();
                _shapes[id] = piece;
                if (isServer) _shapeRecords[id] = swap.Record;
            }
            foreach (int id in _shapeSwapGroup)
            {
                ShapeSwap swap = _shapeSwaps[id];
                foreach (Vector3Int coord in swap.Chunks)
                {
                    HashSet<int> owners;
                    if (_shapeHeldChunks.TryGetValue(coord, out owners))
                    {
                        owners.Remove(id);
                        if (owners.Count == 0) _shapeHeldChunks.Remove(coord);
                    }
                }
                _shapeSwaps.Remove(id);
            }
            _shapeSwapGroup.Clear();
            break;
        }
    }

    private void CancelShapeSwap(int id)
    {
        ShapeSwap swap;
        if (!_shapeSwaps.TryGetValue(id, out swap)) return;
        if (swap.PreparedShape != null) Destroy(swap.PreparedShape.gameObject);
        foreach (Vector3Int coord in swap.Chunks)
        {
            HashSet<int> owners;
            if (!_shapeHeldChunks.TryGetValue(coord, out owners)) continue;
            owners.Remove(id);
            if (owners.Count != 0) continue;
            _shapeHeldChunks.Remove(coord);
            StagedChunk staged;
            if (_shapeStagedChunks.TryGetValue(coord, out staged))
            { staged.Prepared.Dispose(); _shapeStagedChunks.Remove(coord); }
            // If the server already removed the terrain but the shape timed out
            // in transit, do not leave the client looking at a phantom wall.
            _meshFence.MarkDirty(coord);
            if (_dirtyMeshes.Add(coord)) _dirtyMeshOrder.Enqueue(coord);
        }
        _shapeSwaps.Remove(id);
    }

    private void ResetShapeSwaps()
    {
        foreach (ShapeSwap swap in _shapeSwaps.Values)
            if (swap.PreparedShape != null)
                Destroy(swap.PreparedShape.gameObject);
        foreach (StagedChunk staged in _shapeStagedChunks.Values)
            if (staged.Prepared != null) staged.Prepared.Dispose();
        _shapeSwaps.Clear(); _shapeHeldChunks.Clear(); _shapeStagedChunks.Clear();
        _shapeSwapGroup.Clear(); _shapeSwapGroupChunks.Clear(); _shapeSwapSearch.Clear();
        _shapePendingPoses.Clear();
        LastShapeReleaseSeconds = 0f; LastShapeRemovalSeconds = 0f;
        AtomicShapeReleases = 0;
    }
}
