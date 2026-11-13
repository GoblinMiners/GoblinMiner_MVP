#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Portable, deterministic fluid-volume tests plus runtime diagnostics.
/// Does not replace a Unity Play Mode, terrain, shader or Mirror multiplayer test.</summary>
public static class GobmineFluidPhysicsPatchTests
{
    [MenuItem("Tools/Gobmine/Fluids and Falling Bodies/Run Fluid Conservation Tests")]
    public static void RunFluidRules()
    {
        int passed = 0;
        try
        {
            Vector3Int start = new Vector3Int(0,1,0);
            Vector3Int lower = Vector3Int.zero;
            // Newly opened cells must get fluid without minting any new volume.
            var store = new GobmineFluidStore(
                p => p == start ? new GobmineFluidCell(GobmineFluidKind.Water,1000) : default,
                p => true, (a,b) => true);
            Require(store.PrimeNewOpening(lower, p => true), "water primes newly opened rock cavity");passed++;
            Require(store.Get(start).Amount + store.Get(lower).Amount == 1000,
                "opening conserves every fluid unit");passed++;
            Require(store.Get(lower).Kind == GobmineFluidKind.Water,
                "newly wet cavity keeps original material kind");passed++;
            Require(!store.PrimeNewOpening(new Vector3Int(30,0,0), p => true),
                "unconnected air does not manufacture fluid");passed++;
            // A fully solid rock face must block even an otherwise open coarse cell.
            var blocked = new GobmineFluidStore(
                p => p == start ? new GobmineFluidCell(GobmineFluidKind.Lava,1000) : default,
                p => true, (a,b) => false);
            Require(!blocked.PrimeNewOpening(lower,p => true) && blocked.Get(lower).Amount == 0,
                "solid face blocks lava priming");passed++;
            blocked.Wake(start);
            blocked.Step(p => true);
            Require(blocked.Get(start).Amount == 1000 && blocked.Get(lower).Amount == 0,
                "solid face blocks ordinary fluid flow");passed++;
            // Water and lava never mix or transmute into free volume.
            var mixed = new GobmineFluidStore(
                p => p == start ? new GobmineFluidCell(GobmineFluidKind.Water,500) :
                    p == lower ? new GobmineFluidCell(GobmineFluidKind.Lava,500) : default,
                p => true,(a,b) => true);
            mixed.Wake(start); mixed.Step(p => true);
            Require(mixed.Get(lower).Kind == GobmineFluidKind.Lava && mixed.Get(lower).Amount == 500,
                "water never overwrites lava"); passed++;
            Debug.Log("[Gobmine] Fluid rules PASS: " + passed +
                " assertions. Next run the 3D pool and undermine tests in Play Mode.");
        }
        catch(Exception e) { Debug.LogError("[Gobmine] Fluid rules FAILED after " + passed + ": " + e); }
    }

    [MenuItem("Tools/Gobmine/Fluids and Falling Bodies/Log Fluid and Physics State (Host)")]
    public static void LogState()
    {
        World world=World.Instance;
        if (world == null || !Application.isPlaying)
        {
            Debug.LogWarning("[Gobmine] Start your regular level as Host before logging runtime state.");
            return;
        }
        Debug.Log("[Gobmine Fluid] open-refills waiting=" + world.PendingFluidRefills +
            ", primed=" + world.PrimedFluidCells +
            ", simulation work=" + world.PendingFluidWork +
            ", last fluid CPU=" + world.LastFluidMilliseconds.ToString("0.00") + " ms.");
        world.LogFallingShapeCounters();
    }
    private static void Require(bool condition,string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
}
#endif
