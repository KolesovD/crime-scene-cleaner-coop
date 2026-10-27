using UnityEngine;

namespace CscCoop;

// One-off investigation (CSCCOOP_TEST=toolprobe, one copy): what the player holds and how the game builds it,
// for showing other players' tools. In the job it lists the inventory (tool data, prefabs), then equips every
// hotbar slot in turn and logs the selected item's renderers (layer, shader, size, place under the camera).
static class ToolProbe
{
    static float at = -1;
    static int slot = -1;

    public static void Tick(GameWorld w)
    {
        if (w.Player == null || !w.Player.CanMove) { at = Time.unscaledTime + 5; return; }
        if (Time.unscaledTime < at) return;
        at = Time.unscaledTime + 3;
        var inv = InventoryController.Instance;
        if (inv == null) { Plugin.L.LogInfo("TOOLPROBE no InventoryController"); return; }
        if (slot < 0)
        {
            var pi = PlayerInventory.Instance;
            Plugin.L.LogInfo($"TOOLPROBE right attach {Path(inv.rightAttachPoint)} left {Path(inv.leftAttachPoint)} hands camera {inv.HandsCamera?.name} fov {inv.HandsCamera?.fieldOfView} main fov {w.Player.mainCamera?.fieldOfView}");
            if (pi != null && pi.Slots != null)
                foreach (var s in pi.Slots)
                {
                    var td = s?.Item;
                    if (td == null) continue;
                    Plugin.L.LogInfo($"TOOLPROBE slot tool {td.id} '{td.DisplayName}' cat {td.Category} type {td.Type} prefab '{td.ItemPrefab?.name}' available {s.IsItemAvailable} instance '{s.ItemInstance?.name}'");
                }
            if (inv.EquippedItems != null)
                foreach (var u in inv.EquippedItems) if (u != null) Plugin.L.LogInfo($"TOOLPROBE equipped list '{u.name}' {u.GetIl2CppType().Name} active {u.gameObject.activeInHierarchy}");
            slot = 0;
        }
        LogSelected(inv, w);
        if (slot > 9) { Plugin.L.LogInfo("BOT done, quitting"); System.Diagnostics.Process.GetCurrentProcess().Kill(); }
        Plugin.L.LogInfo($"TOOLPROBE Equip({slot})");
        try { inv.Equip(slot); } catch (System.Exception e) { Plugin.L.LogInfo($"TOOLPROBE Equip({slot}) failed: {e.Message}"); }
        slot++;
    }

    static void LogSelected(InventoryController inv, GameWorld w)
    {
        var u = inv.SelectedItem?.TryCast<UsableItem>();
        if (u == null) { Plugin.L.LogInfo($"TOOLPROBE selected: {(inv.SelectedItem == null ? "nothing" : "not a UsableItem")}"); return; }
        var cam = w.Player.mainCamera != null ? w.Player.mainCamera.transform : w.Player.transform;
        var lp = cam.InverseTransformPoint(u.transform.position);
        Plugin.L.LogInfo($"TOOLPROBE selected '{Path(u.transform)}' {u.GetIl2CppType().Name} layer {u.gameObject.layer} scale {u.transform.lossyScale.x:F3} at cam {lp.ToString("F3")} rot {(Quaternion.Inverse(cam.rotation) * u.transform.rotation).eulerAngles.ToString("F0")} offset {u.ModelOffsetPosition} {u.ModelOffsetRotation} target '{u.targetModel?.name}'");
        foreach (var r in u.GetComponentsInChildren<Renderer>(true))
        {
            var mf = r.GetComponent<MeshFilter>();
            var smr = r.TryCast<SkinnedMeshRenderer>();
            var mesh = smr != null ? smr.sharedMesh : mf != null ? mf.sharedMesh : null;
            var m = r.sharedMaterial;
            Plugin.L.LogInfo($"TOOLPROBE   {r.GetIl2CppType().Name} '{Path(r.transform)}' on {r.enabled && r.gameObject.activeInHierarchy} layer {r.gameObject.layer} mesh '{mesh?.name}' readable {mesh?.isReadable} size {r.bounds.size.ToString("F2")} mats {r.sharedMaterials.Length} shader '{m?.shader?.name}' color {(m != null && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString() : "-")}");
        }
    }

    static string Path(Transform t)
    {
        if (t == null) return "null";
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}
