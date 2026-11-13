#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Read-only Play Mode diagnostic of shared terrain-mesh boundaries.</summary>
public static class GobmineTerrainSeamDiagnostics
{
    [MenuItem("Tools/Gobmine/Terrain/Diagnose Visible Mesh Seams (Play Mode)")]
    private static void Diagnose()
    {
        if (!Application.isPlaying || World.Instance == null)
        {
            Debug.LogWarning("[Gobmine Seam Audit] Enter Play mode and start a Host/client world first.");
            return;
        }
        World world = World.Instance;
        Chunk[] all = UnityEngine.Object.FindObjectsByType<Chunk>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        var byCoord = new Dictionary<Vector3Int, Chunk>();
        foreach (Chunk chunk in all)
        {
            if (chunk == null || !chunk.gameObject.activeInHierarchy) continue;
            Vector3Int c = world.GetChunkCoordFromPosition(chunk.transform.position);
            byCoord[c] = chunk;
        }
        int pairs = 0, badDensity = 0, badContour = 0, emptySurfaces = 0;
        int shown = 0;
        Vector3Int[] axes = { Vector3Int.right, Vector3Int.up, Vector3Int.forward };
        foreach (KeyValuePair<Vector3Int, Chunk> entry in byCoord)
        foreach (Vector3Int direction in axes)
        {
            Chunk other;
            if (!byCoord.TryGetValue(entry.Key + direction, out other)) continue;
            pairs++;
            Chunk a = entry.Value, b = other;
            int axis = direction.x != 0 ? 0 : direction.y != 0 ? 1 : 2;
            int densityErrors = CompareSharedDensity(a.GetDensities(), b.GetDensities(), axis);
            Vector3 planePoint = b.transform.position;
            float plane = axis == 0 ? planePoint.x : axis == 1 ? planePoint.y : planePoint.z;
            HashSet<string> aEdges = BoundaryEdges(a, axis, plane);
            HashSet<string> bEdges = BoundaryEdges(b, axis, plane);
            int missingAB = 0, missingBA = 0;
            foreach (string edge in aEdges) if (!bEdges.Contains(edge)) missingAB++;
            foreach (string edge in bEdges) if (!aEdges.Contains(edge)) missingBA++;
            if (densityErrors > 0) badDensity++;
            if (missingAB > 0 || missingBA > 0) badContour++;
            if (aEdges.Count == 0 && bEdges.Count == 0) emptySurfaces++;
            if ((densityErrors > 0 || missingAB > 0 || missingBA > 0) && shown++ < 16)
                Debug.LogWarning($"[Gobmine Seam Audit] {entry.Key} ↔ {entry.Key + direction}: " +
                    $"shared density differences={densityErrors}, unmatched contour edges={missingAB}/{missingBA}; " +
                    $"left contour={aEdges.Count}, right contour={bEdges.Count}; " +
                    $"pending rebuilds={world.PendingMeshRebuilds}, atomic groups={world.PendingSeamPublicationGroups}", a);
        }
        Debug.Log($"[Gobmine Seam Audit] active chunks={byCoord.Count}, adjacent pairs={pairs}, " +
            $"density mismatch pairs={badDensity}, boundary-contour mismatch pairs={badContour}, " +
            $"fully empty shared faces={emptySurfaces}, " +
            $"pending mesh chunks={world.PendingMeshRebuilds}, staged/pending groups={world.PendingSeamPublicationGroups}, " +
            $"atomic publishes={world.AtomicTerrainPublications}. " +
            "Run again after you stop digging for two seconds. " +
            "Zero mismatches while a visible gap remains indicates an INTERNAL mesh/renderer issue, " +
            "missing chunk, or a separate physics object rather than a chunk-boundary seam.", world);
    }

    private static int CompareSharedDensity(float[,,] a, float[,,] b, int axis)
    {
        if (a == null || b == null) return -1;
        int nx = a.GetLength(0), ny = a.GetLength(1), nz = a.GetLength(2);
        if (nx != b.GetLength(0) || ny != b.GetLength(1) || nz != b.GetLength(2)) return -1;
        int failures = 0;
        if (axis == 0)
            for (int y = 0; y < ny; y++) for (int z = 0; z < nz; z++)
                if (Mathf.Abs(a[nx - 1, y, z] - b[0, y, z]) > 0.00001f) failures++;
        if (axis == 1)
            for (int x = 0; x < nx; x++) for (int z = 0; z < nz; z++)
                if (Mathf.Abs(a[x, ny - 1, z] - b[x, 0, z]) > 0.00001f) failures++;
        if (axis == 2)
            for (int x = 0; x < nx; x++) for (int y = 0; y < ny; y++)
                if (Mathf.Abs(a[x, y, nz - 1] - b[x, y, 0]) > 0.00001f) failures++;
        return failures;
    }

    private static HashSet<string> BoundaryEdges(Chunk chunk, int axis, float plane)
    {
        var edges = new HashSet<string>();
        MeshFilter filter = chunk.GetComponent<MeshFilter>();
        Mesh mesh = filter == null ? null : filter.sharedMesh;
        MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
        if (mesh == null || (renderer != null && !renderer.enabled)) return edges;
        Vector3[] points = mesh.vertices;
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            int[] triangles = mesh.GetTriangles(sub);
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Vector3 p0 = chunk.transform.TransformPoint(points[triangles[t]]);
                Vector3 p1 = chunk.transform.TransformPoint(points[triangles[t + 1]]);
                Vector3 p2 = chunk.transform.TransformPoint(points[triangles[t + 2]]);
                AddPlaneEdge(edges, p0, p1, axis, plane);
                AddPlaneEdge(edges, p1, p2, axis, plane);
                AddPlaneEdge(edges, p2, p0, axis, plane);
            }
        }
        return edges;
    }

    private static void AddPlaneEdge(HashSet<string> set, Vector3 a, Vector3 b, int axis, float plane)
    {
        float da = axis == 0 ? a.x : axis == 1 ? a.y : a.z;
        float db = axis == 0 ? b.x : axis == 1 ? b.y : b.z;
        if (Mathf.Abs(da - plane) > 0.0002f || Mathf.Abs(db - plane) > 0.0002f) return;
        string x = Key(a), y = Key(b);
        if (x == y) return;
        set.Add(string.CompareOrdinal(x, y) < 0 ? x + "|" + y : y + "|" + x);
    }

    // Quantization is only for diagnostics. Tiny submillimetre FP differences
    // are not incorrectly presented as large, visible gaps.
    private static string Key(Vector3 point)
        => Mathf.RoundToInt(point.x * 1000f) + "," +
           Mathf.RoundToInt(point.y * 1000f) + "," +
           Mathf.RoundToInt(point.z * 1000f);
}
#endif
