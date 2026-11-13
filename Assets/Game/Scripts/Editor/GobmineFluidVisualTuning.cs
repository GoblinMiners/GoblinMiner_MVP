#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Non-destructive visuals/damage tuning. No generation rules or saves touched.</summary>
public static class GobmineFluidVisualTuning
{
    private const string Menu = "Tools/Gobmine/Visuals and Damage/";

    [MenuItem(Menu + "Apply Smooth Pool + Forgiving Fall Preset (Open Scene)")]
    public static void ApplyPreset()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play mode before editing your Expedition settings asset.");
        World world = UnityEngine.Object.FindFirstObjectByType<World>();
        if (world == null || world.expeditionSettings == null)
            throw new InvalidOperationException("Open your DemoLvl with its configured World/ExpeditionSettings asset.");
        GobmineExpeditionSettings settings = world.expeditionSettings;
        Undo.RecordObject(settings, "Apply Gobmine Smooth Fluid and Fall Damage Settings");
        // These are runtime/visual budgets, NOT changes to seed or cave geometry.
        settings.fluidOperationsPerFrame = Mathf.Max(settings.fluidOperationsPerFrame, 96);
        settings.fluidDiscoveryCellsPerFrame = Mathf.Max(settings.fluidDiscoveryCellsPerFrame, 96);
        settings.fluidMillisecondsPerFrame = Mathf.Max(settings.fluidMillisecondsPerFrame, 2f);
        settings.fluidRegionsPerFrame = Mathf.Max(settings.fluidRegionsPerFrame, 3);
        settings.safeFallMetres = 5.5f;
        settings.lethalFallMetres = 34f;
        Tune(settings.waterMaterial, new Color(.023f, .29f, .44f, .50f), 0f,
            1.65f, .75f, .20f, .58f, .36f);
        Tune(settings.lavaMaterial, new Color(1f, .18f, .025f, .94f), 1.25f,
            2.25f, .58f, .27f, .42f, .14f);
        EditorUtility.SetDirty(settings);
        if (world.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[Gobmine Visuals] Applied pooled fluid appearance, 3 fluid-region builds/frame, " +
            "96 simulation operations/frame (2ms soft cap), and forgiving fall values. " +
            "No terrain generation rules, prefabs, or world saves changed. Save DemoLvl.", world);
    }

    private static void Tune(Material material, Color color, float glow, float rippleScale,
        float rippleSpeed, float rippleStrength, float fresnel, float shore)
    {
        if (material == null) return;
        if (material.shader == null || material.shader.name != "Gobmine/Expedition Fluid URP")
        {
            Debug.LogWarning("[Gobmine Visuals] " + material.name +
                " is using a custom shader. Material left unchanged; assign the Gobmine fluid shader manually if desired.", material);
            return;
        }
        Undo.RecordObject(material, "Improve Gobmine Fluid Material");
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Glow", glow);
        material.SetFloat("_RippleScale", rippleScale);
        material.SetFloat("_RippleSpeed", rippleSpeed);
        material.SetFloat("_RippleStrength", rippleStrength);
        material.SetFloat("_FresnelStrength", fresnel);
        material.SetFloat("_ShoreFoam", shore);
        material.SetFloat("_ZWrite", 0f);
        EditorUtility.SetDirty(material);
    }

    [MenuItem(Menu + "Audit Active Terrain Meshes (Host)")]
    public static void AuditTerrain()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Gobmine Visuals] Start Host in DemoLvl before auditing live colliders.");
            return;
        }
        int scanned = 0, missing = 0, mismatched = 0;
        foreach (Chunk chunk in UnityEngine.Object.FindObjectsByType<Chunk>(FindObjectsSortMode.None))
        {
            if (chunk == null || !chunk.gameObject.activeInHierarchy) continue;
            scanned++;
            MeshFilter filter = chunk.GetComponent<MeshFilter>();
            MeshCollider collider = chunk.GetComponent<MeshCollider>();
            Mesh visible = filter != null ? filter.sharedMesh : null;
            Mesh collision = collider != null ? collider.sharedMesh : null;
            bool hasSurface = visible != null && visible.vertexCount > 0;
            if (hasSurface && collision == null) { missing++; Debug.LogWarning(
                "[Gobmine Visuals] Visible terrain without collider: " + chunk.name, chunk); }
            else if (hasSurface && visible != collision) { mismatched++; Debug.LogWarning(
                "[Gobmine Visuals] Visual/collider mesh mismatch: " + chunk.name, chunk); }
        }
        Debug.Log($"[Gobmine Visuals] Live terrain chunks={scanned}; " +
            $"visible chunks with missing collider={missing}; mesh mismatches={mismatched}. " +
            "A clean report does not rule out chunk-loading or cross-chunk boundary gaps.");
    }

    [MenuItem(Menu + "Log Fluid and Fall Settings (Host)")]
    public static void LogRuntimeSettings()
    {
        World world = UnityEngine.Object.FindFirstObjectByType<World>();
        if (world == null || !world.ExpeditionEnabled)
        {
            Debug.LogWarning("[Gobmine Visuals] Load DemoLvl with an active Expedition World first.");
            return;
        }
        GobmineExpeditionSettings s = world.ExpeditionProfile;
        Debug.Log($"[Gobmine Visuals] Fluid cell={s.fluidCellMetres:0.##}m " +
            $"region builds/frame={s.fluidRegionsPerFrame}, fluid ops/frame={s.fluidOperationsPerFrame}, " +
            $"fluid CPU cap={s.fluidMillisecondsPerFrame:0.##}ms. " +
            $"Fall safe={s.safeFallMetres:0.##}m, lethal={s.lethalFallMetres:0.##}m. " +
            $"Example damage at 8m={GobmineExpeditionRules.FallDamage(8,-24,25,s.safeFallMetres,s.lethalFallMetres,100):0.#}, " +
            $"12m={GobmineExpeditionRules.FallDamage(12,-24,25,s.safeFallMetres,s.lethalFallMetres,100):0.#}, " +
            $"20m={GobmineExpeditionRules.FallDamage(20,-24,25,s.safeFallMetres,s.lethalFallMetres,100):0.#}. " +
            $"Visible fluid components={UnityEngine.Object.FindObjectsByType<GobmineFluidRenderer>(FindObjectsSortMode.None).Length}", world);
    }
}
#endif
