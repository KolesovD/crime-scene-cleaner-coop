using System;
using CoopCore;
using UnityEngine;

namespace CscCoop;

// toolcarry suite (tools/run-pair.ps1 -Test toolcarry [-Mission 11]): the host puts its bucket down; the client
// grabs it, carries it 1.5 m and lets go (owner's request: anyone can move a friend's bucket or lamp). The client
// may not put it into its own inventory; the host then picks it up and it must be gone on both.
public partial class Bot
{
    PortableItem tcTool;
    Vector3 tcPlaced;

    PortableItem OtherPlayersTool()
    {
        foreach (var p in UnityEngine.Object.FindObjectsOfType<PortableItem>())
            if (p != null && p.name.Contains("(other player)") && p.GetComponentInChildren<LiquidContainer>(true) != null) return p;
        return null;
    }

    void ToolCarryTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        switch (step)
        {
            case 0 when t > 3:
                // the game's way: out of the inventory into the hands (InventoryController.Equip), then let go
                if (host) { try { InventoryController.Instance.Equip(ItemCategory.bucket, 0); } catch (Exception e) { Plugin.L.LogInfo("TOOLCARRY equip: " + e.Message); } }
                step = 10; break;
            case 10 when t > 6:
                if (host)
                {
                    foreach (var pi in UnityEngine.Object.FindObjectsOfType<PortableItem>())
                        if (pi != null && !pi.name.Contains("(other player)") && pi.GetComponentInChildren<LiquidContainer>(true) != null) { tcTool = pi; break; }
                    if (tcTool != null)
                    {
                        if (w.Grabber.GrabbedRb != null) w.Grabber.TryRelease();
                        tcTool.transform.position = w.Player.transform.position + w.Player.transform.forward * 1.5f + Vector3.up * 0.3f;
                        var g = tcTool.GetComponent<GrabableItemController>() ?? tcTool.GetComponentInParent<GrabableItemController>();
                        if (g != null && g.Rb != null) { g.Rb.isKinematic = false; g.Rb.velocity = Vector3.zero; }
                        tcPlaced = tcTool.transform.position;
                    }
                    Plugin.L.LogInfo($"TOOLCARRY host set down {(tcTool != null ? tcTool.name : "NO BUCKET")} at {P3(tcPlaced)}");
                }
                step = 1; break;
            case 1 when t > 10:
                if (!host)
                {
                    tcTool = OtherPlayersTool();
                    if (tcTool != null) { tcPlaced = tcTool.transform.position; ApproachPoint(w, tcPlaced); }
                    Plugin.L.LogInfo($"TOOLCARRY client sees the host's bucket: {(tcTool != null ? P3(tcPlaced) : "NO")}");
                }
                step = 2; break;
            case 2 when t > 11:
                if (!host && tcTool != null)
                {
                    var g = tcTool.GetComponentInChildren<GrabableItemController>();
                    if (g == null) g = tcTool.GetComponentInParent<GrabableItemController>();
                    if (g != null) w.Grabber.GrabItem(g);
                    Plugin.L.LogInfo($"TOOLCARRY client grab: {(w.Grabber.GrabbedRb != null ? "holding " + w.Grabber.GrabbedRb.name : "NOT holding")} (grabbable {(g != null)})");
                }
                walkFrom = w.Player.transform.position; walkDir = -w.Player.transform.forward;
                step = 3; break;
            case 3 when t > 14.5f:
                if (!host && w.Grabber.GrabbedRb != null) w.Grabber.TryRelease();
                step = 4; break;
            case 4 when t > 20:
                ToolCarryLog(host, "rest");
                // The client may not put the host's bucket into its own inventory.
                if (!host && tcTool != null) { tcTool.PickUp(); Plugin.L.LogInfo("TOOLCARRY client tried to pick up the host's bucket"); }
                step = 5; break;
            case 5 when t > 24:
                ToolCarryLog(host, "after client pick-up");
                if (host && tcTool != null) { tcTool.PickUp(); Plugin.L.LogInfo($"TOOLCARRY host picked up its bucket (it was simulated here: {Dyn.IsMine(tcTool.gameObject)})"); }
                step = 6; break;
            case 6 when t > 32:
                if (!host) tcTool = OtherPlayersTool();
                ToolCarryLog(host, "final");
                Plugin.L.LogInfo("TOOLCARRY dump done");
                step = 7; break;
        }
        if (!host && step == 3 && t < 14) Walk(w, (t - 11) / 3f * 1.5f);
    }

    void ToolCarryLog(bool host, string label)
    {
        bool there = tcTool != null && tcTool.gameObject.activeInHierarchy;
        var p = there ? tcTool.transform.position : Vector3.zero;
        Plugin.L.LogInfo(FormattableString.Invariant($"TOOLCARRY {label} bucket {(there ? "at " + P3(p) : "gone")} moved {(there ? Vector3.Distance(p, tcPlaced) : 0):F2} simulated here {(there && Dyn.IsMine(tcTool.gameObject))}"));
    }
}
