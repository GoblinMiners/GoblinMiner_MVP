#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Explicit migration: creates NEW assets/save slot and never rewrites the existing ore catalogue.</summary>
public static class GobmineOreChunkSetup
{
    private const string Root="Assets/Game/OreChunks";
    private const string CatalogPath=Root+"/MineableOreCatalogue.asset";
    private const string Menu="Tools/Gobmine/Ore Chunks/";

    [MenuItem(Menu+"Create Mineable Ore Catalogue and Configure Open Scene (NEW WORLD)")]
    public static void Install()
    {
        if (EditorApplication.isPlaying) {Debug.LogError("Stop Play mode before changing world-generation rules.");return;}
        World world=UnityEngine.Object.FindFirstObjectByType<World>();
        if (world==null || world.MaterialCatalog==null)
        {Debug.LogError("Open DemoLvl and assign a terrain material catalogue to its World first.");return;}
        if (!EditorUtility.DisplayDialog("Gobmine: NEW ore-chunk world",
            "This creates an independent material catalogue and switches the OPEN scene to a NEW save filename. " +
            "Existing saves and material assets are kept unchanged. Start a new expedition world because ore-generation rules change.",
            "Create new ore world","Cancel")) return;
        try
        {
            EnsureFolders();
            TerrainMaterialCatalog generated=AssetDatabase.LoadAssetAtPath<TerrainMaterialCatalog>(CatalogPath);
            if (generated==null)
            {
                var previous=world.MaterialCatalog;
                generated=ScriptableObject.CreateInstance<TerrainMaterialCatalog>();
                generated.baseStoneId=previous.baseStoneId;
                generated.definitions=new TerrainMaterialDefinition[previous.definitions.Length];
                for (int i=0;i<previous.definitions.Length;i++)
                {
                    TerrainMaterialDefinition original=previous.definitions[i];
                    if (original==null) throw new InvalidOperationException("Original catalogue contains a missing entry.");
                    TerrainMaterialDefinition clone=UnityEngine.Object.Instantiate(original);
                    clone.name=original.name+"_OreChunk";
                    clone.spawnMineableChunks=original.materialId!=previous.baseStoneId && original.generateDeposits;
                    if (clone.spawnMineableChunks)
                    {
                        // Fewer large, flat-looking veins; more individual discoverable nodules.
                        clone.depositGridSizeInCells=Mathf.Max(clone.maximumRadiusInCells*2,
                            Mathf.Min(28,clone.depositGridSizeInCells));
                        clone.depositChancePerThousand=Mathf.Max(600,clone.depositChancePerThousand);
                    }
                    string path=AssetDatabase.GenerateUniqueAssetPath(Root+"/Material_"+original.materialId+".asset");
                    AssetDatabase.CreateAsset(clone,path);
                    generated.definitions[i]=clone;
                }
                AssetDatabase.CreateAsset(generated,CatalogPath);
            }
            generated.ValidateOrThrow();
            Undo.RecordObject(world,"Switch to Gobmine mineable ore chunks");
            var serialized=new SerializedObject(world);
            SerializedProperty catalogue=serialized.FindProperty("materialCatalog");
            if (catalogue==null)throw new InvalidOperationException("World.materialCatalog field could not be found.");
            catalogue.objectReferenceValue=generated;
            serialized.ApplyModifiedProperties();
            world.importLegacyDensitySave=false;
            world.materialSaveFileName="world_ore_chunks_v1.dat";
            EditorUtility.SetDirty(world);
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            AssetDatabase.SaveAssets();
            Debug.Log("[Gobmine Ore Chunks] Configured "+world.gameObject.scene.name+
                " for mineable ore outcrops. Save the scene now. Save: "+world.materialSaveFileName+
                ". Original material catalogue and old save were NOT changed.");
        }
        catch (Exception ex) {Debug.LogError("[Gobmine Ore Chunks] Installation error: "+ex);}
    }

    private static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Game"))throw new InvalidOperationException("Missing Assets/Game folder.");
        if(!AssetDatabase.IsValidFolder(Root))AssetDatabase.CreateFolder("Assets/Game","OreChunks");
    }

    [MenuItem(Menu+"Apply Small Ore + Early Reveal Settings (Open Scene)")]
    public static void ApplySmallOreSettings()
    {
        if (EditorApplication.isPlaying)
        { Debug.LogError("Stop Play mode before updating ore settings."); return; }
        var world=UnityEngine.Object.FindFirstObjectByType<World>();
        if (world==null){Debug.LogError("Open the gameplay scene containing World first.");return;}
        Undo.RecordObject(world,"Apply small ore and early discovery settings");
        world.oreChunkMinimumRadius=0.28f;
        world.oreChunkMaximumRadius=0.65f;
        world.oreChunkPreloadDistance=12f;
        world.oreChunkDiscoveryChecksPerFrame=12;
        world.oreChunkImmediateMiningChecks=32;
        world.oreChunkWorkshopSafeRadiusMetres=4f;
        EditorUtility.SetDirty(world);
        EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        Debug.Log("[Gobmine Ore Chunks] Small embedded ore settings applied. Save the scene. " +
            "Old saved nodes keep their resource quantity but are now visually size-capped. " +
            "For fully consistent generation and placement, use a new save slot.");
    }

    [MenuItem(Menu+"Log Running Ore Chunk Status (Host)")]
    public static void LogStatus()
    {
        var world=World.Instance;
        if(world==null || !world.isServer)
        {Debug.LogWarning("Start a host or dedicated server first.");return;}
        Debug.Log(world.OreChunkStatus());
    }

    [MenuItem(Menu+"Diagnose Nearby Ore (Host)")]
    public static void DiagnoseNearbyOre()
    {
        var world=World.Instance;
        if (world==null || !world.isServer)
        { Debug.LogWarning("Start Host in DemoLvl before diagnosing ore. This tool is read-only.");return; }
        Debug.Log(world.ProbeNearbyOreCandidates(20f));
    }

    [MenuItem(Menu+"Validate Open Scene")]
    public static void Validate()
    {
        var world=UnityEngine.Object.FindFirstObjectByType<World>();
        if (world==null || world.MaterialCatalog==null) {Debug.LogError("World / material catalogue missing.");return;}
        try
        {
            world.MaterialCatalog.ValidateOrThrow();
            int count=0;
            foreach (var d in world.MaterialCatalog.definitions)
                if (d!=null && d.spawnMineableChunks)
                {
                    if (d.materialId==world.MaterialCatalog.baseStoneId || !d.generateDeposits || d.inventoryItem==null || d.itemsPerCubicMetre<=0)
                        throw new InvalidOperationException("Invalid ore chunk definition: "+d.name);
                    count++;
                }
            if(count==0)Debug.LogWarning("[Gobmine Ore Chunks] No ore definitions have Spawn Mineable Chunks enabled.");
            else Debug.Log("[Gobmine Ore Chunks] "+count+" ore types enabled; save="+world.materialSaveFileName+
                "; stream distance="+world.oreChunkStreamDistance+"; active cap="+world.maxActiveOreChunks+
                "; protected workshop radius="+world.oreChunkWorkshopSafeRadiusMetres+"m");
        }
        catch(Exception ex){Debug.LogError("[Gobmine Ore Chunks] "+ex);}
    }
}
#endif
