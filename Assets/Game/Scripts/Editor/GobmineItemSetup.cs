#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// One-time setup for the slot inventory. Each step is safe to run again.
///  1. Convert Minerals To Items: every terrain material's old MineralData gets an ItemSO with the
///     same ID, so saved worlds and the world rules signature are unchanged.
///  2. Assign Missing Item IDs: every ItemSO without an ID gets a free one (100 and up).
///  3. Rebuild Item Database: Resources/Gobmine/ItemDatabase lists every ItemSO.
///  4. Create Inventory UI: copies the sandbox inventory canvas into Resources/Gobmine/InventoryUI,
///     which the local player spawns at runtime.
///  5. Add Shared Storage: places a shared storage box in the demo level's spawn room.
/// </summary>
public static class GobmineItemSetup
{
    private const string Menu = "Tools/Gobmine/Items/";
    public const int ResourceStackSize = 9999;
    private const string OreItemFolder = "Assets/Game/Items/Ores";
    private const string DatabasePath = "Assets/Game/Resources/Gobmine/ItemDatabase.asset";
    private const string UiPrefabPath = "Assets/Game/Resources/Gobmine/InventoryUI.prefab";
    private const string SlotPrefabSource = "Assets/Prefabs/Inventory/Slot.prefab";
    private const string SlotPrefabCopy = "Assets/Game/Prefabs/UI/Slot.prefab";
    private const string SandboxScene = "Assets/Scenes/Sandbox.unity";
    private const string DemoScene = "Assets/Game/Scene/DemoLvl.unity";

    [MenuItem(Menu + "Set Up Items (all steps)", false, 0)]
    public static void SetUpAll()
    {
        ConvertMineralsToItems();
        AssignMissingItemIds();
        RebuildItemDatabase();
        CreateInventoryUi();
        AddSharedStorageToDemoLevel();
        ValidateCatalogues();
        Debug.Log("[Gobmine Items] Setup finished.");
    }

    [MenuItem(Menu + "1. Convert Minerals To Items", false, 20)]
    public static void ConvertMineralsToItems()
    {
        int converted = 0;
        var byId = new Dictionary<int, ItemSO>();
        foreach (ItemSO existing in AllAssets<ItemSO>())
            if (existing.itemID > 0 && !byId.ContainsKey(existing.itemID)) byId.Add(existing.itemID, existing);

        foreach (TerrainMaterialDefinition definition in AllAssets<TerrainMaterialDefinition>())
        {
            if (definition.item != null || definition.inventoryItem == null) continue;
            MineralData mineral = definition.inventoryItem;
            ItemSO item;
            if (!byId.TryGetValue(mineral.mineralID, out item))
            {
                EnsureFolder(OreItemFolder);
                string title = string.IsNullOrWhiteSpace(mineral.mineralName) ? mineral.name : mineral.mineralName;
                string path = AssetDatabase.GenerateUniqueAssetPath(OreItemFolder + "/" + SafeFileName(title) + ".asset");
                item = ScriptableObject.CreateInstance<ItemSO>();
                item.itemID = mineral.mineralID;
                item.itemName = title;
                item.itemType = ItemType.Resource;
                item.maxStackSize = ResourceStackSize;
                item.baseSellValue = Mathf.Max(0, mineral.baseValue);
                item.itemPrefab = mineral.dropPrefab;
                AssetDatabase.CreateAsset(item, path);
                byId[item.itemID] = item;
                converted++;
            }
            definition.item = item;
            EditorUtility.SetDirty(definition);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[Gobmine Items] Converted " + converted + " mineral(s) to items in " + OreItemFolder + ".");
    }

    [MenuItem(Menu + "2. Assign Missing Item IDs", false, 21)]
    public static void AssignMissingItemIds()
    {
        List<ItemSO> items = AllAssets<ItemSO>().OrderBy(i => AssetDatabase.GetAssetPath(i)).ToList();
        var used = new HashSet<int>();
        foreach (ItemSO item in items)
        {
            if (item.itemID <= 0) continue;
            if (!used.Add(item.itemID))
                Debug.LogError("[Gobmine Items] Item ID " + item.itemID + " is used twice (" + item.name + "). Fix it by hand: renumbering could break a saved world.", item);
        }
        int next = 100, assigned = 0;
        foreach (ItemSO item in items)
        {
            if (item.itemID > 0) continue;
            while (used.Contains(next)) next++;
            item.itemID = next;
            used.Add(next);
            EditorUtility.SetDirty(item);
            assigned++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[Gobmine Items] Assigned " + assigned + " item ID(s).");
    }

    [MenuItem(Menu + "3. Rebuild Item Database", false, 22)]
    public static void RebuildItemDatabase()
    {
        EnsureFolder(Path.GetDirectoryName(DatabasePath).Replace('\\', '/'));
        ItemDatabase database = AssetDatabase.LoadAssetAtPath<ItemDatabase>(DatabasePath);
        if (database == null)
        {
            database = ScriptableObject.CreateInstance<ItemDatabase>();
            AssetDatabase.CreateAsset(database, DatabasePath);
        }
        database.items = AllAssets<ItemSO>().Where(i => i.itemID > 0).OrderBy(i => i.itemID).ToList();
        EditorUtility.SetDirty(database);
        AssetDatabase.SaveAssets();
        string problem = database.Validate();
        if (problem != null) Debug.LogError("[Gobmine Items] Item database problem: " + problem, database);
        else Debug.Log("[Gobmine Items] Item database lists " + database.items.Count + " item(s).", database);
    }

    [MenuItem(Menu + "4. Create Inventory UI From Sandbox", false, 23)]
    public static void CreateInventoryUi()
    {
        Scene sandbox = SceneManager.GetSceneByPath(SandboxScene);
        bool openedHere = !sandbox.isLoaded;
        if (openedHere) sandbox = EditorSceneManager.OpenScene(SandboxScene, OpenSceneMode.Additive);
        try
        {
            InventoryView view = sandbox.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<InventoryView>(true)).FirstOrDefault();
            if (view == null) { Debug.LogError("[Gobmine Items] No InventoryView found in " + SandboxScene + "."); return; }
            GameObject canvas = view.GetComponentInParent<Canvas>(true) != null ? view.GetComponentInParent<Canvas>(true).rootCanvas.gameObject : view.gameObject;

            EnsureFolder(Path.GetDirectoryName(UiPrefabPath).Replace('\\', '/'));
            PrefabUtility.SaveAsPrefabAsset(canvas, UiPrefabPath);

            GameObject slotPrefab = CopySlotPrefab();
            GameObject root = PrefabUtility.LoadPrefabContents(UiPrefabPath);
            try
            {
                root.name = "InventoryUI";
                // The sandbox spawns local cubes when items are dropped; the game drops on the server.
                foreach (ItemDropSpawner spawner in root.GetComponentsInChildren<ItemDropSpawner>(true))
                    UnityEngine.Object.DestroyImmediate(spawner, true);
                foreach (InventoryView v in root.GetComponentsInChildren<InventoryView>(true))
                {
                    var so = new SerializedObject(v);
                    so.FindProperty("playerInventorySource").objectReferenceValue = null; // Bound at runtime.
                    if (slotPrefab != null)
                        so.FindProperty("slotPrefab").objectReferenceValue = slotPrefab.GetComponent<SlotView>();
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(root, UiPrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            Debug.Log("[Gobmine Items] Inventory UI saved to " + UiPrefabPath + ". The local player spawns it at runtime.");
        }
        finally
        {
            if (openedHere) EditorSceneManager.CloseScene(sandbox, true);
        }
    }

    [MenuItem(Menu + "5. Add Shared Storage To Demo Level", false, 24)]
    public static void AddSharedStorageToDemoLevel()
    {
        Scene demo = SceneManager.GetSceneByPath(DemoScene);
        bool openedHere = !demo.isLoaded;
        if (openedHere) demo = EditorSceneManager.OpenScene(DemoScene, OpenSceneMode.Additive);
        try
        {
            if (demo.GetRootGameObjects().Any(root => root.GetComponentInChildren<SharedStorage>(true) != null))
            { Debug.Log("[Gobmine Items] " + DemoScene + " already has a shared storage box."); return; }

            Vector3 position = new Vector3(3f, TerrainDensityStore.RoomMinY + 0.4f, 3f);
            GameObject spawn = demo.GetRootGameObjects().FirstOrDefault(g => g.name == "Player 1 Spawn");
            if (spawn != null)
            {
                Vector3 forward = Vector3.ProjectOnPlane(spawn.transform.forward, Vector3.up).normalized;
                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                position = spawn.transform.position + forward * 3f;
                position.y = TerrainDensityStore.RoomMinY + 0.4f; // Spawn-room floor.
            }

            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "SharedStorage";
            box.transform.position = position;
            box.transform.localScale = new Vector3(1.2f, 0.8f, 0.8f);
            box.AddComponent<NetworkIdentity>();
            box.AddComponent<SharedStorage>();
            SceneManager.MoveGameObjectToScene(box, demo);
            EditorSceneManager.MarkSceneDirty(demo);
            EditorSceneManager.SaveScene(demo);
            Debug.Log("[Gobmine Items] Added a shared storage box to " + DemoScene + " at " + position + ".", box);
        }
        finally
        {
            if (openedHere) EditorSceneManager.CloseScene(demo, true);
        }
    }

    [MenuItem(Menu + "Validate Material Catalogues", false, 40)]
    public static void ValidateCatalogues()
    {
        foreach (TerrainMaterialCatalog catalogue in AllAssets<TerrainMaterialCatalog>())
        {
            try { catalogue.ValidateOrThrow(); Debug.Log("[Gobmine Items] " + catalogue.name + " is valid.", catalogue); }
            catch (Exception ex) { Debug.LogError("[Gobmine Items] " + catalogue.name + ": " + ex.Message, catalogue); }
        }
    }

    private static GameObject CopySlotPrefab()
    {
        GameObject copy = AssetDatabase.LoadAssetAtPath<GameObject>(SlotPrefabCopy);
        if (copy != null) return copy;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(SlotPrefabSource) == null) return null;
        EnsureFolder(Path.GetDirectoryName(SlotPrefabCopy).Replace('\\', '/'));
        AssetDatabase.CopyAsset(SlotPrefabSource, SlotPrefabCopy);
        return AssetDatabase.LoadAssetAtPath<GameObject>(SlotPrefabCopy);
    }

    private static IEnumerable<T> AllAssets<T>() where T : UnityEngine.Object
    {
        foreach (string guid in AssetDatabase.FindAssets("t:" + typeof(T).Name))
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
            if (asset != null) yield return asset;
        }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(" ", "");
    }
}
#endif
