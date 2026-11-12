#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Isolated mesh handoff checks. Run inside the Unity Editor before live
/// host/client mining tests. These tests do not claim to measure frame rate.
/// </summary>
public static class GobmineAtomicShapeSwapTests
{
    [MenuItem("Tools/Gobmine/Falling Shapes/Apply Fast Shape Swap Settings (Open Scene)")]
    public static void ApplySettings()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[Gobmine Shapes] Change scene settings outside Play mode.");
            return;
        }
        World[] worlds = UnityEngine.Object.FindObjectsByType<World>(FindObjectsSortMode.None);
        if (worlds.Length == 0)
        {
            Debug.LogError("[Gobmine Shapes] Open DemoLvl or another scene containing World.");
            return;
        }
        foreach (World world in worlds)
        {
            Undo.RecordObject(world, "Apply fast shape-swap settings");
            world.preserveDisconnectedShapes = true;
            world.shapeMeshCellsPerFrame = 8192;
            world.shapeRemovalMillisecondsPerFrame = 5f;
            world.shapeStagedChunksPerFrame = 2;
            world.shapeStagingMillisecondsPerFrame = 3f;
            world.shapePacketsPerFrame = 6;
            world.densityChangesPerFrame = 2048;
            EditorUtility.SetDirty(world);
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            Debug.Log("[Gobmine Shapes] Fast staged swap settings applied. Save scene with Ctrl+S. " +
                "These are starting budgets; measure Profiler impact under multiplayer load.", world);
        }
    }

    [MenuItem("Tools/Gobmine/Falling Shapes/Run Atomic Mesh Handoff Tests")]
    public static void Run()
    {
        GameObject go = null;
        Chunk.PreparedMesh staged = null;
        try
        {
            go = new GameObject("_GobmineAtomicMeshHandoffTest");
            Chunk chunk = go.AddComponent<Chunk>();
            var sizes = new Vector3Int(3,3,3);
            var densities = new float[4,4,4];
            for (int x = 0; x <= 3; x++)
                for (int y = 0; y <= 3; y++)
                    for (int z = 0; z <= 3; z++)
                        densities[x,y,z] = x < 2 ? -1f : 1f;
            chunk.Init(sizes, 0.25f, Vector3.zero, 0f, 1729, densities);
            MeshFilter filter = go.GetComponent<MeshFilter>();
            MeshCollider collider = go.GetComponent<MeshCollider>();
            Mesh oldVisible = filter.sharedMesh;
            Require(oldVisible != null && oldVisible.vertexCount > 0,
                "Initial terrain surface was not created.");
            Require(collider.sharedMesh == oldVisible,
                "Initial collider must use the currently visible terrain mesh.");
            for (int x = 0; x <= 3; x++)
                for (int y = 0; y <= 3; y++)
                    for (int z = 0; z <= 3; z++) densities[x,y,z] = 1f;
            staged = chunk.PrepareMeshWithoutPublishing();
            Require(filter.sharedMesh == oldVisible,
                "Preparing terrain modified the visible mesh before the release.");
            Require(collider.sharedMesh == oldVisible,
                "Preparing terrain modified the physics collider before the release.");
            chunk.PublishPreparedMesh(staged);
            staged = null;
            Require(filter.sharedMesh != oldVisible,
                "Published terrain did not swap the visible mesh.");
            Require(collider.sharedMesh == null,
                "An empty replacement must not keep the original collision wall.");
            Require(chunk.CollisionReady,
                "A deliberately empty terrain chunk must count as collision-ready.");
            Debug.Log("[Gobmine Shapes] Atomic mesh handoff tests PASSED (staging, visible hold, collider hold, synchronized publication, empty collision). Run host/client tests next.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[Gobmine Shapes] Atomic mesh handoff FAILED: " + ex);
        }
        finally
        {
            if (staged != null) staged.Dispose();
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
#endif
