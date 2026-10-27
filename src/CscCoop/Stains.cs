using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CscCoop;

// Blood and other stains (Taint + CleanableDecal).
//  - Ids: a stain that exists when the job starts is its index in the scene table; a stain made during the
//    job (BloodSpreaderController.ProjectBlood: dragged bodies, dripping, dirty tools) gets an id from the
//    machine that made it (NewBase + player << 20 + counter), and the others make the same stain with the
//    same call, then put it exactly where the maker's is.
//  - Cleaning strokes: every cleaning tool ends in CleanableDecal.PaintTexture(strength, size, uv,
//    effectiveness, exponent) (seen: one call per mop call). Those calls are replayed on the others
//    with the same arguments, so the same spots get wiped. Stream events, batched every 0.1 s.
//  - Intensity (what the job report counts): only ever goes down, so the lowest value anyone saw wins;
//    a machine only sends a value lower than any it knows, so the host's newest state is the minimum.
//  - DestroyDecal (stain gone): a state event, so late joiners get it too.
[HarmonyPatch]
public static class Stains
{
    const int NewBase = 0x1000000;
    static readonly Dictionary<int, float> known = new();
    static readonly HashSet<int> destroyed = new();
    static readonly Dictionary<int, Taint> newById = new();
    static readonly Dictionary<IntPtr, int> newByTaint = new();
    static uint tableKey;
    static int counter;
    static readonly List<(int id, float s, float size, Vector2 uv, float e, int x)> hits = new();
    static float nextFlush;
    public static int HitsSent, HitsApplied, Made, Copied;

    static GameWorld W => CoopRunner.Instance.World;

    static void EnsureTable()
    {
        if (tableKey == W.SceneKey) return;
        tableKey = W.SceneKey;
        // Nothing known yet (not the current intensity): the table is made lazily, and when the first call is a
        // SetIntensity postfix the current value is already the new one (stain suite: the host's first change was never sent).
        known.Clear(); destroyed.Clear(); newById.Clear(); newByTaint.Clear(); hits.Clear();
    }

    static int Id(Taint t)
    {
        if (t == null) return -1;
        int i = W.TaintIndex(t);
        if (i >= 0) return i;
        return newByTaint.TryGetValue(t.Pointer, out var id) ? id : -1;
    }

    static Taint Get(int id) => id < NewBase ? (!W.IsOff("stains") && (uint)id < (uint)W.Taints.Count ? W.Taints[id] : null) : newById.TryGetValue(id, out var t) ? t : null;
    static float Known(int id) => known.TryGetValue(id, out var v) ? v : float.MaxValue;

    // ---- local changes ----

    [HarmonyPatch(typeof(Taint), nameof(Taint.SetIntensity))]
    [HarmonyPostfix]
    static void SetIntensity(Taint __instance, float intensity)
    {
        if (!WorldEvents.Live) return;
        EnsureTable();
        int id = Id(__instance);
        if (id < 0 || intensity >= Known(id) - 0.0005f && Env.Get("CSCCOOP_MUTANT") != "stain-raise") return;
        known[id] = intensity;
        WorldEvents.Send(Ev.StainIntensity, w => { w.Write(id); w.Write(intensity); }, StateKey(id, 1));
    }

    [HarmonyPatch(typeof(Taint), nameof(Taint.DestroyDecal))]
    [HarmonyPostfix]
    static void DestroyDecal(Taint __instance)
    {
        if (!WorldEvents.Live) return;
        EnsureTable();
        int id = Id(__instance);
        if (id < 0 || !destroyed.Add(id)) return;
        WorldEvents.Send(Ev.StainDestroy, w => w.Write(id), StateKey(id, 2));
    }

    // Static stains: 1 + index (like before). New stains: high range, one key per kind of state.
    static uint StateKey(int id, int what) => id < NewBase ? (uint)id + 1 : 0x20000000u + (uint)(id - NewBase) * 4 + (uint)what;

    [HarmonyPatch(typeof(CleanableDecal), nameof(CleanableDecal.PaintTexture), new[] { typeof(float), typeof(float), typeof(Vector2), typeof(float), typeof(int) })]
    [HarmonyPostfix]
    static void PaintTexture(CleanableDecal __instance, float strength, float scaledSize, Vector2 coordinates, float effectiveness, int exponent)
    {
        if (!WorldEvents.Live) return;
        EnsureTable();
        int id = Id(__instance.taint != null ? __instance.taint : __instance.GetComponent<Taint>());
        if (id < 0) return;
        hits.Add((id, strength, scaledSize, coordinates, effectiveness, exponent));
    }

    // The funnel every ProjectBlood overload ends in. Returns the new decal.
    [HarmonyPatch(typeof(BloodSpreaderController), nameof(BloodSpreaderController.ProjectBlood),
        new[] { typeof(GameObject), typeof(BloodSplatterType), typeof(GameObject), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(bool) })]
    [HarmonyPostfix]
    static void ProjectBlood(GameObject __result, BloodSplatterType type, GameObject target, Vector3 point, Vector3 normal, Vector3 forward)
    {
        if (!WorldEvents.Live || __result == null) return;
        EnsureTable();
        var taint = __result.GetComponentInChildren<Taint>();
        if (taint == null) return;
        var s = WorldEvents.S;
        int id = NewBase + (s.Me << 20) + (++counter & 0xFFFFF);
        newById[id] = taint; newByTaint[taint.Pointer] = id;
        string path = target != null ? GameWorld.IndexedPathOf(target.transform) : "";
        var t = __result.transform;
        Made++;
        WorldEvents.Send(Ev.StainNew, w =>
        {
            w.Write(id); w.Write((int)type); w.Write(path);
            W3(w, point); W3(w, normal); W3(w, forward);
            W3(w, t.position); var q = t.rotation; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); W3(w, t.localScale);
        }, StateKey(id, 0));
    }

    static void W3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    static Vector3 R3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    // Called every frame by CoopRunner: sends the batched strokes.
    public static void Tick()
    {
        if (hits.Count == 0 || Time.unscaledTime < nextFlush) return;
        nextFlush = Time.unscaledTime + 0.1f;
        const int perEvent = 38;   // 38 x 28 bytes stays under one packet
        for (int start = 0; start < hits.Count; start += perEvent)
        {
            int end = Mathf.Min(hits.Count, start + perEvent);
            WorldEvents.Send(Ev.StainHits, w =>
            {
                w.Write((ushort)(end - start));
                for (int k = start; k < end; k++)
                {
                    var h = hits[k];
                    w.Write(h.id); w.Write(h.s); w.Write(h.size); w.Write(h.uv.x); w.Write(h.uv.y); w.Write(h.e); w.Write((byte)h.x);
                }
            });
            HitsSent += end - start;
        }
        hits.Clear();
    }

    // ---- remote changes ----

    public static void OnNew(BinaryReader r)
    {
        EnsureTable();
        int id = r.ReadInt32(); var type = (BloodSplatterType)r.ReadInt32(); string path = r.ReadString();
        Vector3 point = R3(r), normal = R3(r), forward = R3(r), pos = R3(r);
        var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var scale = R3(r);
        if (newById.ContainsKey(id)) return;
        var bsc = BloodSpreaderController.Instance;
        if (bsc == null) return;
        var target = path != "" ? GameWorld.FindByIndexedPath(path) : null;
        var go = bsc.ProjectBlood(type, target, point, normal, forward, false);
        var taint = go != null ? go.GetComponentInChildren<Taint>() : null;
        if (taint == null) { Plugin.L.LogWarning($"COOP could not make another player's stain {id:X} ({type} on '{path}')"); return; }
        go.transform.SetPositionAndRotation(pos, rot);
        go.transform.localScale = scale;
        newById[id] = taint; newByTaint[taint.Pointer] = id;
        Copied++;
    }

    public static void OnIntensity(BinaryReader r)
    {
        EnsureTable();
        int id = r.ReadInt32(); float v = r.ReadSingle();
        var t = Get(id);
        if (t == null || destroyed.Contains(id)) return;
        if (v < Known(id)) known[id] = v;
        if (v < t.Intensity || Env.Get("CSCCOOP_MUTANT") == "stain-raise") t.SetIntensity(v);
    }

    public static void OnDestroy(BinaryReader r)
    {
        EnsureTable();
        int id = r.ReadInt32();
        var t = Get(id);
        if (t == null || !destroyed.Add(id)) return;
        if (t.gameObject.activeInHierarchy) t.DestroyDecal();
    }

    public static void OnHits(BinaryReader r)
    {
        EnsureTable();
        int n = r.ReadUInt16();
        for (int k = 0; k < n; k++)
        {
            int id = r.ReadInt32();
            float s = r.ReadSingle(), size = r.ReadSingle(), u = r.ReadSingle(), v = r.ReadSingle(), e = r.ReadSingle();
            int x = r.ReadByte();
            var t = Get(id);
            if (t == null || destroyed.Contains(id)) continue;
            var cd = t.GetComponent<CleanableDecal>();
            if (cd == null) continue;
            cd.PaintTexture(s, size, new Vector2(u, v), e, x);
            HitsApplied++;
        }
    }

    public static bool IsDestroyed(int i) => destroyed.Contains(i);
    public static IEnumerable<KeyValuePair<int, Taint>> NewStains => newById;
}
