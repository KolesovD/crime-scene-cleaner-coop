using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CscCoop;

// One-off investigation (CSCCOOP_TEST=stainprobe): which arguments of the UV-space
// CleanableDecal.PaintTexture actually clean a stain. Seen so far: every mop call ends in exactly one
// PaintTexture(strength, size, uv, effectiveness, exponent); (1, 0.25, grid, 1, 1) changed nothing.
// This paints 4 nearby stains with different argument sets and reports intensity/coverage per stain.
[HarmonyPatch]
static class StainProbePatches
{
    public static int PaintUv;

    [HarmonyPatch(typeof(CleanableDecal), nameof(CleanableDecal.PaintTexture), new[] { typeof(float), typeof(float), typeof(Vector2), typeof(float), typeof(int) })]
    [HarmonyPostfix] static void OnPaintUv() => PaintUv++;
}

static class StainProbe
{
    static int step;
    static float at;
    static readonly List<Taint> targets = new();
    static readonly (float strength, float eff, int exp)[] sets = { (1, 1, 1), (100, 1, 1), (1, 100, 1), (-1, 1, 1) };

    public static void Tick(GameWorld w)
    {
        if (w.Player == null || !w.Player.CanMove || Time.unscaledTime < at) return;
        if (step == 0)
        {
            var all = new List<Taint>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Taint>()) if (t != null && t.GetComponent<CleanableDecal>() != null) all.Add(t);
            var me = w.Player.transform.position;
            all.Sort((a, b) => Vector3.Distance(me, a.transform.position).CompareTo(Vector3.Distance(me, b.transform.position)));
            for (int i = 0; i < 4 && i < all.Count; i++) targets.Add(all[i]);
            var near = targets[0].transform.position + Vector3.up * 0.2f;
            if (w.Player.rb != null) w.Player.rb.position = near;
            w.Player.transform.position = near;
            step = 1; at = Time.unscaledTime + 3;
            return;
        }
        if (step >= 1 && step <= 3)
        {
            for (int k = 0; k < targets.Count; k++)
            {
                var cd = targets[k].GetComponent<CleanableDecal>();
                var (s, e, x) = sets[k];
                for (float u = 0.05f; u < 1f; u += 0.1f)
                    for (float v = 0.05f; v < 1f; v += 0.1f)
                        cd.PaintTexture(s, 0.25f, new Vector2(u, v), e, x);
            }
            step++; at = Time.unscaledTime + 3;
            return;
        }
        if (step == 4)
        {
            for (int k = 0; k < targets.Count; k++)
            {
                var t = targets[k];
                var cd = t != null ? t.GetComponent<CleanableDecal>() : null;
                Plugin.L.LogInfo($"STAINPROBE set {sets[k]}: taint intensity {(t != null ? t.Intensity : -1):F3}, coverage {(cd != null ? cd.coveragePercentage : -1):F3}, " +
                                 $"colour start {cd?.startingColour} current {cd?.currentColour} target {cd?.targetColour}, active {t != null && t.gameObject.activeInHierarchy}");
            }
            Plugin.L.LogInfo($"STAINPROBE after: PaintTexture(uv) calls {StainProbePatches.PaintUv}");
            step = 9;
        }
    }
}
