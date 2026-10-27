using System;
using System.Collections.Generic;
using CoopCore;
using HarmonyLib;
using UnityEngine;

namespace CscCoop;

// Other players' tools (mop, sponge, axe, power washer lance...).
//  - The owner sends in its pose which tool it holds (the game's ToolData id, PlayerPose.ToolLance for the
//    lance, which has none), where its first-person view holds it relative to the player, and whether it is
//    being used right now (CleaningItem / PowerWasherLance cleaning, an axe hit in the last 0.4 s).
//  - The others show a copy of that tool's look: the enabled MeshRenderers of the tool's prefab
//    (ToolData.ItemPrefab; for the lance the local player's own lance), meshes and materials shared, no
//    scripts, colliders or particles, on the default layer (first-person models are on layer 8, which only
//    the hands camera draws).
//  - Tool probe (2026-10-27, Dealer's Apartment): every held tool is an instance of its prefab under
//    ObjAttachRotation_R/L of the first-person arms; URP Lit materials.
[HarmonyPatch]
public sealed class ToolLook
{
    readonly GameObject root;
    short shown = PlayerPose.ToolNone;
    public float Length;      // longest side of the copy's bounds, metres
    public Vector3 Centre;    // world centre of the copy's bounds after Place
    Vector3 centreLocal;
    public Vector3 CentreLocal => centreLocal;   // bounds centre in the tool's own space (the root is the grip)
    public int Renderers;
    public string SourceName = "";
    readonly List<GameObject> useParts = new();   // water stream and the like: shown only while the tool is used
    static readonly System.Text.RegularExpressions.Regex UsePart = new("water|stream|beam|spray|splash|vfx|particle", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public ToolLook(Transform figure)
    {
        root = new GameObject("tool");
        root.transform.SetParent(figure, false);
        root.SetActive(false);
    }

    // Builds the copy for this tool if needed; false if this machine can't find what it looks like.
    public bool Show(short tool)
    {
        if (tool != shown)
        {
            shown = tool;
            for (int i = root.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(root.transform.GetChild(i).gameObject);
            Renderers = 0; Length = 0; useParts.Clear(); names.Clear();
            var src = Source(tool);
            SourceName = src != null ? src.name : "";
            if (src != null) Copy(src);
            // A tool this machine's copy shows nothing of (friend's test of 0.4.1: the host's MopTool came out with 0
            // renderers on the client, which only has the WeakMop, so the friend saw empty hands): show the nearest
            // tool of the same kind that does draw (mop, sponge, detergent, axe...).
            if (Renderers == 0 && src != null)
                foreach (var alt in SameKind(src.name))
                {
                    Copy(alt);
                    if (Renderers > 0) { SourceName = alt.name + " (in place of " + src.name + ")"; break; }
                }
            Plugin.L.LogInfo($"COOP tool look {tool}: '{SourceName}', {Renderers} renderers, {Length:F2} m ({string.Join(", ", names)}; only while used: {useParts.Count})");
        }
        if (Renderers == 0) return false;
        if (!root.activeSelf) root.SetActive(true);
        return true;
    }

    public void Hide() { if (root.activeSelf) root.SetActive(false); }

    public void SetUsing(bool on) { foreach (var p in useParts) if (p != null && p.activeSelf != on) p.SetActive(on); }
    readonly List<string> names = new();

    public void Place(Vector3 pos, Quaternion rot, float scale)
    {
        root.transform.SetPositionAndRotation(pos, rot);
        var parent = root.transform.parent.lossyScale.x;
        root.transform.localScale = Vector3.one * ((scale > 0.01f && scale < 10 ? scale : 1) / Mathf.Max(parent, 0.01f));
        Centre = root.transform.TransformPoint(centreLocal);
    }

    void Copy(GameObject src)
    {
        var s = src.transform;
        var bounds = new Bounds();
        bool any = false;
        var parts = new List<(Renderer r, Mesh mesh)>();
        foreach (var mr in src.GetComponentsInChildren<MeshRenderer>(true))
        {
            var f = mr != null ? mr.GetComponent<MeshFilter>() : null;
            if (f != null && f.sharedMesh != null) parts.Add((mr, f.sharedMesh));
        }
        foreach (var sr in src.GetComponentsInChildren<SkinnedMeshRenderer>(true))   // some upgraded tools draw skinned
            if (sr != null && sr.sharedMesh != null) parts.Add((sr, sr.sharedMesh));
        foreach (var (mr, mesh) in parts)
        {
            if (mr == null || !mr.enabled || !ActiveUnder(mr.transform, s)) continue;
            var go = new GameObject(mr.name);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = s.InverseTransformPoint(mr.transform.position);
            go.transform.localRotation = Quaternion.Inverse(s.rotation) * mr.transform.rotation;
            var ls = mr.transform.lossyScale; var ss = s.lossyScale;
            go.transform.localScale = new Vector3(ls.x / ss.x, ls.y / ss.y, ls.z / ss.z);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mr.sharedMaterials;
            Renderers++;
            names.Add(mr.name);
            if (UsePart.IsMatch(PathUnder(mr.transform, s))) { useParts.Add(go); go.SetActive(false); continue; }   // not in the bounds either
            // Bounds in the copy's space: the mesh's corners through the part's local transform.
            var mb = mesh.bounds;
            var m = Matrix4x4.TRS(go.transform.localPosition, go.transform.localRotation, go.transform.localScale);
            for (int c = 0; c < 8; c++)
            {
                var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1)));
                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
            }
        }
        Length = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        centreLocal = bounds.center;
    }

    static string PathUnder(Transform t, Transform top)
    {
        var p = t.name;
        for (t = t.parent; t != null && t != top; t = t.parent) p = t.name + "/" + p;
        return p;
    }

    // Active within the tool (its own root may be switched off: a lance waiting in the inventory).
    static bool ActiveUnder(Transform t, Transform top)
    {
        for (; t != null && t != top; t = t.parent) if (!t.gameObject.activeSelf) return false;
        return true;
    }

    static readonly Dictionary<short, GameObject> sources = new();

    static GameObject Source(short tool)
    {
        if (sources.TryGetValue(tool, out var go) && go != null) return go;
        go = null;
        try
        {
            if (tool == PlayerPose.ToolLance)
            {
                var lance = PlayerInventory.Instance != null ? PlayerInventory.Instance.PowerWasherLance : null;
                if (lance == null) foreach (var l in Resources.FindObjectsOfTypeAll<PowerWasherLance>()) { lance = l; break; }
                go = lance != null ? lance.gameObject : null;
            }
            else foreach (var td in ToolDatas()) if (td != null && td.id == tool) { go = td.ItemPrefab; break; }
        }
        catch (Exception e) { Plugin.L.LogWarning($"COOP tool look {tool}: {e.Message}"); }
        if (go != null) sources[tool] = go;
        return go;
    }

    static readonly string[] Kinds = { "Mop", "Sponge", "Detergent", "Axe", "Garbage", "Lamp", "Ozon", "Brush", "Scraper" };

    static IEnumerable<GameObject> SameKind(string name)
    {
        string kind = null;
        foreach (var k in Kinds) if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { kind = k; break; }
        if (kind == null) yield break;
        foreach (var td in ToolDatas())
            if (td?.ItemPrefab != null && td.ItemPrefab.name != name && td.ItemPrefab.name.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0)
                yield return td.ItemPrefab;
    }

    static IEnumerable<ToolData> ToolDatas()
    {
        var c = PlayerInventory.Instance != null ? PlayerInventory.Instance.ItemsContainer : null;
        if (c == null || c.toolDatas == null) yield break;
        foreach (var td in c.toolDatas) yield return td;
    }

    // ---- owner side ----

    static readonly Dictionary<string, short> idByPrefab = new();
    static float lastHit = -10;
    public static short ForceTool = short.MinValue;   // tests: pretend to hold this tool
    public static bool ForceUse;

    [HarmonyPatch(typeof(AxeTool), nameof(AxeTool.ExecuteHit))] [HarmonyPostfix]
    static void AxeHit() => lastHit = Time.unscaledTime;

    // The selected tool and where it is, relative to the player transform (feet, yaw).
    public static void Sample(Transform player, ref PlayerPose pose)
    {
        pose.Tool = PlayerPose.ToolNone; pose.ToolRot = Q4.Identity;
        UsableItem u = null;
        try { var inv = InventoryController.Instance; u = inv != null ? inv.SelectedItem?.TryCast<UsableItem>() : null; }
        catch { u = null; }
        if (u != null && u.gameObject.activeInHierarchy)
        {
            pose.Tool = IdOf(u);
            if (pose.Tool != PlayerPose.ToolNone)
            {
                var t = u.transform;
                pose.ToolPos = GameWorld.V(player.InverseTransformPoint(t.position));
                pose.ToolRot = GameWorld.Q(Quaternion.Inverse(player.rotation) * t.rotation);
                pose.ToolScale = t.lossyScale.x;
                bool use = u.TryCast<CleaningItem>()?.isCleaning == true || u.TryCast<PowerWasherLance>()?.isCleaning == true
                    || u.TryCast<AxeTool>() != null && Time.unscaledTime - lastHit < 0.4f;
                if (use) pose.Flags |= PlayerPose.PoseUsing;
            }
        }
        if (ForceTool != short.MinValue)
        {
            if (pose.Tool == PlayerPose.ToolNone && ForceTool != PlayerPose.ToolNone)
            {
                // No real hold to copy: roughly where the first-person arms hold a tool (probe: mop root 0.59 m right, 0.57 m below the eyes).
                pose.ToolPos = new V3(0.32f, 1.05f, 0.35f);
                // The lance's barrel runs along its -z (figure suite picture), the axe's head along +z.
                pose.ToolRot = GameWorld.Q(Quaternion.Euler(-35, ForceTool == PlayerPose.ToolLance ? 180 : 0, 0));
                pose.ToolScale = 1;
            }
            pose.Tool = ForceTool;
        }
        if (ForceUse && pose.Tool != PlayerPose.ToolNone) pose.Flags |= PlayerPose.PoseUsing;
    }

    static short IdOf(UsableItem u)
    {
        if (u.TryCast<PowerWasherLance>() != null) return PlayerPose.ToolLance;
        string name = u.name.Replace("(Clone)", "").Trim();
        if (idByPrefab.TryGetValue(name, out var id)) return id;
        id = PlayerPose.ToolNone;
        foreach (var td in ToolDatas()) if (td?.ItemPrefab != null && td.ItemPrefab.name == name) { id = (short)td.id; break; }
        idByPrefab[name] = id;
        Plugin.L.LogInfo($"COOP my tool '{name}' is tool {id}");
        return id;
    }
}
