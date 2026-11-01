using System;
using System.Collections.Generic;
using System.Linq;
using CoopCore;
using UnityEngine;

namespace CscCoop;

// lamps suite (tools/run-pair.ps1 -Test lamps [-Mission 11]): each player puts down one lamp and two UV lamps the
// game's way (InventoryController.Equip, let go). The host switches its lamp on, the client leaves its own off: the
// client's own lamp must stay off on both (owner's test: my lamp 1 switched the friend's lamp 1). Later the client
// switches the host's lamp off. All UV lamps are on: four on the map, and each machine's own must light blood (a UV
// shader slot from CleaningSenseController; owner's test: lamps over the per-player limit didn't).
public partial class Bot
{
    readonly HashSet<IntPtr> lpPlaced = new();
    int lpCount = -1, lpNext;
    float lpAt;

    static PlayerInventory Inv => InventoryController.Instance?.inventory;
    static bool IsUv(PortableItem p) => p.GetComponentInChildren<UVLampController>(true) != null;
    static LampController LampOf(PortableItem p) => p.GetComponentInChildren<LampController>(true);
    // UVLampController's shader slot (cleanerSenseIndex, -1 = none).
    static int UvSlot(PortableItem p) { var u = p.GetComponentInChildren<UVLampController>(true); return u == null ? -2 : u.cleanerSenseIndex; }

    static IEnumerable<PortableItem> Lamps(bool mine) => UnityEngine.Object.FindObjectsOfType<PortableItem>().Where(p => p != null && LampOf(p) != null && p.name.Contains("(other player)") != mine);

    void LampsTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        var inv = Inv;
        switch (step)
        {
            case 0 when t > 3:
            {
                // test copies only: one lamp and two UV lamps in the inventory
                var tools = inv?.ItemsContainer?.toolDatas?.ToArray().Where(d => d != null && d.Category == ItemCategory.lamp && d.ItemPrefab != null).ToList() ?? new List<ToolData>();
                bool HasUv(ToolData d) => d.ItemPrefab.GetComponentInChildren<UVLampController>(true) != null;
                var plain = tools.Where(d => !HasUv(d)).OrderBy(d => d.itemLevel).FirstOrDefault();
                var uv = tools.Where(HasUv).OrderBy(d => d.itemLevel).FirstOrDefault();
                int Have(Func<ToolData, bool> f) => inv.Slots.ToArray().Count(s => s.Item != null && s.Item.Category == ItemCategory.lamp && s.Item.ItemPrefab != null && f(s.Item));
                if (plain != null && Have(d => !HasUv(d)) < 1) inv.AddItem(plain);
                while (uv != null && Have(HasUv) < 2) inv.AddItem(uv);
                lpCount = inv.GetCategoryCount(ItemCategory.lamp);
                Plugin.L.LogInfo($"LAMPS inventory lamps {lpCount}: {string.Join(", ", inv.Slots.ToArray().Where(s => s.Item != null && s.Item.Category == ItemCategory.lamp).Select(s => s.Item.DisplayName))}");
                lpAt = t; step = 1; break;
            }
            case 1 when t > lpAt + 1.5f:
                // The client puts its lamps down first, then the host (both bots walk all 2n steps, in lockstep):
                // on the host the client's copies come first and take the light slots, the order that left the
                // owner's own lamps dark.
                if (lpNext >= 2 * lpCount) { step = 2; lpAt = t; break; }
                if ((lpNext >= lpCount) != host) { lpAt = t; step = 11; break; }
                {
                    // that lamp's own slot (Equip(category, k) brought the first lamp out every time)
                    var slot = Inv.Slots.ToArray().Where(sl => sl.Item != null && sl.Item.Category == ItemCategory.lamp).ElementAtOrDefault(lpNext % lpCount);
                    if (slot != null) InventoryController.Instance.EquipItem(ItemCategory.lamp, slot);
                }
                lpAt = t; step = 11; break;
            case 11 when t > lpAt + 1.5f:
            {
                var p = (lpNext >= lpCount) == host ? Lamps(true).FirstOrDefault(x => x.gameObject.activeInHierarchy && !lpPlaced.Contains(x.Pointer)) : null;
                if (p != null)
                {
                    if (w.Grabber.GrabbedRb != null) w.Grabber.TryRelease();
                    var dir = host ? w.Player.transform.forward : -w.Player.transform.forward;
                    // upright and apart: a UV lamp only switches on standing (UVLampController.Interact checks
                    // DetectObjectOrientation) and Update turns a fallen one off
                    var spot = w.Player.transform.position + dir * 1.5f + w.Player.transform.right * ((lpPlaced.Count - 1) * 0.8f);
                    if (Physics.Raycast(spot + Vector3.up * 1.0f, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore)) spot = hit.point;
                    p.transform.SetPositionAndRotation(spot + Vector3.up * 0.05f, Quaternion.identity);
                    var g = p.GetComponent<GrabableItemController>() ?? p.GetComponentInParent<GrabableItemController>();
                    // held still where it stands (test only): a UV lamp that tips over can't be switched on, and how
                    // lamps stand is not what this suite is about
                    if (g != null && g.Rb != null) { g.Rb.velocity = Vector3.zero; g.Rb.isKinematic = IsUv(p); }
                    lpPlaced.Add(p.Pointer);
                    Plugin.L.LogInfo($"LAMPS placed {p.name} (uv {IsUv(p)})");
                }
                lpNext++; lpAt = t; step = 1; break;
            }
            case 2 when t > lpAt + 3:
            case 21 when t > lpAt + 1.5f:
            case 22 when t > lpAt + 1.5f:
                foreach (var p in Lamps(true))
                {
                    var l = LampOf(p);
                    bool on = IsUv(p) || host;   // UV lamps on everywhere; the plain lamp only on the host
                    // the UV lamp's own Interact (an interop call through the LampController wrapper is not virtual)
                    var uvc = p.GetComponentInChildren<UVLampController>(true);
                    // a lamp leaning on something (16 deg) isn't "standing" and won't switch on: stand it up again
                    var od = uvc != null ? uvc.DetectObjectOrientation : null;
                    if (od != null && !od.IsStanding)
                    {
                        p.transform.rotation = Quaternion.identity;
                        var g = p.GetComponent<GrabableItemController>() ?? p.GetComponentInParent<GrabableItemController>();
                        if (g != null && g.Rb != null) { g.Rb.velocity = Vector3.zero; g.Rb.angularVelocity = Vector3.zero; }
                    }
                    if (l.isOn != on) { if (uvc != null) uvc.Interact(); else l.Interact(); }
                }
                Plugin.L.LogInfo("LAMPS switched: UV on, plain lamp " + (host ? "on" : "off"));
                lpAt = t; step = step == 2 ? 21 : step == 21 ? 22 : 3; break;
            case 3 when t > lpAt + 8:
                LampReport("on");
                if (!host)
                {
                    var hl = Lamps(false).FirstOrDefault(p => !IsUv(p));
                    if (hl != null && LampOf(hl).isOn) { LampOf(hl).Interact(); Plugin.L.LogInfo($"LAMPS client switched the host's lamp: on {LampOf(hl).isOn}"); }
                }
                lpAt = t; step = 4; break;
            case 4 when t > lpAt + 8:
                LampReport("final");
                Plugin.L.LogInfo("LAMPS dump done");
                step = 5; break;
        }
    }

    void LampReport(string label)
    {
        foreach (var mine in new[] { true, false })
            foreach (var p in Lamps(mine))
            {
                var l = LampOf(p);
                var u = p.GetComponentInChildren<UVLampController>(true);
                var od = u != null ? u.DetectObjectOrientation : null;
                Plugin.L.LogInfo($"LAMPS {label} {(mine ? "own" : "other")} {(u != null ? "uv" : "plain")} on {l.isOn} active {p.gameObject.activeInHierarchy} slot {UvSlot(p)} lit {(u != null && u.Active && u.cleanerSenseIndex > 0)} standing {(od != null && od.IsStanding)} at {P3(p.transform.position)} up {P3(p.transform.up)}");
            }
    }
}
