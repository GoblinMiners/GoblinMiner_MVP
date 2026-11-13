#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

public static class GobmineVisualRegressionTests
{
    [MenuItem("Tools/Gobmine/Visuals and Damage/Run Fluid + Fall Rule Tests")]
    public static void Run()
    {
        int checks = 0;
        Action<bool, string> check = (ok, msg) =>
        {
            if (!ok) throw new InvalidOperationException("[Gobmine Visual Tests] " + msg);
            checks++;
        };
        float safe = 5.5f, lethal = 34f;
        check(GobmineExpeditionRules.FallDamage(1.1f,-24,25,safe,lethal,100) == 0f, "normal jump is safe");
        check(GobmineExpeditionRules.FallDamage(5.5f,-24,25,safe,lethal,100) == 0f, "safe fall boundary");
        check(GobmineExpeditionRules.FallDamage(8,-24,25,safe,lethal,100) < 5f, "small cave drop should hurt little");
        check(GobmineExpeditionRules.FallDamage(12,-24,25,safe,lethal,100) < 20f, "12m drop is survivable");
        float big = GobmineExpeditionRules.FallDamage(20,-24,25,safe,lethal,100);
        check(big > 20f && big < 65f, "20m drop must remain dangerous, not guaranteed lethal");
        check(Mathf.Abs(GobmineExpeditionRules.FallDamage(lethal,-24,25,safe,lethal,100)-100f)<.01f,
            "very deep hard fall reaches full damage");
        check(GobmineExpeditionRules.FallDamage(100,-24,10,safe,lethal,100) == 0f,
            "very slow impact is safe");
        check(GobmineExpeditionRules.WaterImpactMultiplier(2.5f,2.5f)==0f,
            "deep water still cushions the fall");
        float d;
        var tracker = new GobmineFallTracker();tracker.Reset(30f);
        check(!tracker.Sample(29,false,false,.1f,out d), "airborne is not landing");
        check(!tracker.Sample(29,false,true,1f,out d), "waiting for terrain is not landing");
        check(tracker.Sample(25,true,false,.1f,out d) && Mathf.Abs(d-5f)<.001f,
            "actual landing only once");
        check(!tracker.Sample(25,true,false,.1f,out d), "no repeat fall damage");
        Debug.Log("[Gobmine Visual Tests] PASS: " + checks +
            " fall/tracker checks. Next test smooth water/lava in Play mode and verify terrain is never visibly cleared.");
    }
}
#endif
