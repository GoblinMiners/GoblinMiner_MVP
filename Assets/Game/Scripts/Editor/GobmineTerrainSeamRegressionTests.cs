#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Editor-only smoke tests. These do not replace Play Mode testing.</summary>
public static class GobmineTerrainSeamRegressionTests
{
    [MenuItem("Tools/Gobmine/Terrain/Run Seam Regression Tests")]
    private static void Run()
    {
        int passed = 0;
        CheckSharedSamples(); passed++;
        CheckVersionedFence(); passed++;
        CheckStagedPublication(); passed++;
        Debug.Log($"[Gobmine Seam Tests] {passed} checks passed. Run the live seam audit during/after mining next.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("[Gobmine Seam Tests] " + message);
    }

    private static void CheckSharedSamples()
    {
        var store = new TerrainDensityStore(new Vector3Int(16,16,16), 0.25f);
        var sample = new Vector3Int(16,-16,16);
        store.Set(sample, 0.73f);
        int owners = 0;
        foreach (Vector3Int coord in store.ChunksSharingSample(sample))
        {
            owners++;
            Vector3Int origin = Vector3Int.Scale(coord, store.ChunkSize);
            Vector3Int local = sample - origin;
            float[,,] densities = store.MakeChunk(coord);
            Require(Mathf.Abs(densities[local.x,local.y,local.z] - 0.73f) < 1e-6f,
                "Different density on a shared boundary");
        }
        Require(owners == 8, "Corner sample must be owned by eight chunks including negative-coordinate neighbours");
    }

    private static void CheckVersionedFence()
    {
        var fence = new TerrainMeshFence();
        Vector3Int c = new Vector3Int(1,0,0);
        fence.MarkDirty(c); long older = fence.Capture(c);
        fence.MarkDirty(c); long newer = fence.Capture(c);
        fence.MarkBuiltAtVersion(c, older);
        Require(fence.IsSatisfied(c,older), "Older prepared mesh is not marked built");
        Require(!fence.IsSatisfied(c,newer), "Older prepared mesh must not claim a newer edit");
        fence.MarkBuiltAtVersion(c,newer);
        Require(fence.IsSatisfied(c,newer), "Latest built version is still unsatisfied");
    }

    private static void CheckStagedPublication()
    {
        var go = new GameObject("Gobmine Seam Smoke Test");
        try
        {
            Chunk chunk = go.AddComponent<Chunk>();
            float[,,] a = new float[3,3,3];
            float[,,] b = new float[3,3,3];
            for (int x=0;x<3;x++) for(int y=0;y<3;y++) for(int z=0;z<3;z++)
            { a[x,y,z] = x < 1 ? -1f : 1f; b[x,y,z] = x < 2 ? -1f : 1f; }
            chunk.Init(new Vector3Int(2,2,2),0.25f,Vector3.zero,0f,42,a);
            Mesh before = go.GetComponent<MeshFilter>().sharedMesh;
            Chunk.PreparedMesh prepared = chunk.PrepareMeshWithoutPublishing(b);
            Require(object.ReferenceEquals(go.GetComponent<MeshFilter>().sharedMesh,before),
                "Preparing a mesh must not change the displayed mesh");
            Require(object.ReferenceEquals(chunk.GetDensities(),a),
                "Preparing must not overwrite the currently displayed density view");
            chunk.PublishPreparedMesh(prepared);
            Require(!object.ReferenceEquals(go.GetComponent<MeshFilter>().sharedMesh,before),
                "Publishing must replace the visible mesh");
            Require(Mathf.Abs(chunk.GetDensities()[1,1,1] - b[1,1,1]) < 1e-6f,
                "Published density must match the newly visible mesh");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }
}
#endif
