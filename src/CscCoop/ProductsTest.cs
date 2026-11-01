using System;
using System.Linq;
using CoopCore;
using UnityEngine;

namespace CscCoop;

// products suite (tools/run-pair.ps1 -Test products [-Mission 11]): cleaning products in buckets. The host puts its
// bucket down, fills it and pours the green product in; the client must see it in the host's bucket, rinse its own
// mop there and get the product on the mop, then empty it, fill it and pour the red product in (a one-star bucket
// holds one). Both must end with the same bucket: water,
// dirt and both products (owner's request: as in the earlier csc-coop prototype).
public partial class Bot
{
    PortableItem prBucket;

    static ToolData Detergent(string color) => PlayerInventory.Instance?.Slots?.ToArray().Select(s => s.Item).FirstOrDefault(d => d != null && d.Category == ItemCategory.detergent && d.DisplayName.Contains(color));
    static BuffBase BuffOf(ToolData d) => d?.TryCast<DetergentData>()?.buffToApply;
    static string BuffNames(Il2CppSystem.Collections.Generic.List<BuffInstance> l) => l == null ? "-" : string.Join("+", l.ToArray().Where(b => b != null).Select(b => b.Buff != null ? b.Buff.name : "?").OrderBy(n => n));

    PortableItem BucketHere(bool other)
    {
        foreach (var p in UnityEngine.Object.FindObjectsOfType<PortableItem>())
            if (p != null && p.name.Contains("(other player)") == other && p.GetComponentInChildren<LiquidContainer>(true) != null) return p;
        return null;
    }

    void ProductsTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        switch (step)
        {
            case 0 when t > 3:
                if (host) InventoryController.Instance.Equip(ItemCategory.bucket, 0);
                step = 10; break;
            case 10 when t > 6:
                if (host)
                {
                    prBucket = BucketHere(false);
                    if (prBucket != null)
                    {
                        if (w.Grabber.GrabbedRb != null) w.Grabber.TryRelease();
                        prBucket.transform.position = w.Player.transform.position + w.Player.transform.forward * 1.5f + Vector3.up * 0.3f;
                        var g = prBucket.GetComponent<GrabableItemController>() ?? prBucket.GetComponentInParent<GrabableItemController>();
                        if (g != null && g.Rb != null) { g.Rb.isKinematic = false; g.Rb.velocity = Vector3.zero; }
                        var lc = prBucket.GetComponentInChildren<LiquidContainer>();
                        lc.AddWater(1f);
                        var green = Detergent("Green");
                        bool ok = lc.TryAddBuff(BuffOf(green));
                        Plugin.L.LogInfo(FormattableString.Invariant($"PRODUCTS host bucket down, water {lc.WaterLevel:F2}, green ({(green != null ? green.DisplayName : "NONE")}) added {ok}: {BuffNames(lc.activeBuffs)}"));
                    }
                    else Plugin.L.LogInfo("PRODUCTS host has no bucket out");
                }
                step = 1; break;
            case 1 when t > 11:
                if (!host)
                {
                    prBucket = BucketHere(true);
                    var lc = prBucket != null ? prBucket.GetComponentInChildren<LiquidContainer>() : null;
                    Plugin.L.LogInfo(FormattableString.Invariant($"PRODUCTS client sees the host's bucket: {(lc != null ? $"water {lc.WaterLevel:F2} products {BuffNames(lc.activeBuffs)}" : "NO")}"));
                    InventoryController.Instance.Equip(ItemCategory.mop, 0);
                }
                step = 2; break;
            case 2 when t > 14:
                if (!host && prBucket != null)
                {
                    var lc = prBucket.GetComponentInChildren<LiquidContainer>();
                    var mop = PlayerInventory.Instance.Slots.ToArray().Select(s => s.itemInstance?.TryCast<CleaningItem>()).FirstOrDefault(x => x != null && x.name.Contains("Mop"));
                    ApproachPoint(w, prBucket.transform.position);
                    if (mop != null)
                    {
                        lc.CleanTool(mop);
                        Plugin.L.LogInfo(FormattableString.Invariant($"PRODUCTS client rinsed its mop in the host's bucket: mop products {BuffNames(mop.activeBuffs)}, bucket dirt {lc.TaintLevel:F2}"));
                    }
                    else Plugin.L.LogInfo("PRODUCTS client has no mop");
                    // a one-star bucket holds one product: empty it, fill it again, pour the red one in
                    lc.CleanWater(); lc.AddWater(1f);
                    var red = Detergent("Red");
                    bool ok = lc.TryAddBuff(BuffOf(red));
                    Plugin.L.LogInfo($"PRODUCTS client poured red ({(red != null ? red.DisplayName : "NONE")}) into the host's bucket: {ok}, now {BuffNames(lc.activeBuffs)}");
                }
                step = 3; break;
            case 3 when t > 24:
                if (prBucket == null) prBucket = BucketHere(!host);
                var b = prBucket != null ? prBucket.GetComponentInChildren<LiquidContainer>() : null;
                Plugin.L.LogInfo(FormattableString.Invariant($"PRODUCTS final bucket {(b != null ? $"water {b.WaterLevel:F2} dirt {b.TaintLevel:F2} products {BuffNames(b.activeBuffs)}" : "none")} simulated here {(prBucket != null && Dyn.IsMine(prBucket.gameObject))}"));
                Plugin.L.LogInfo("PRODUCTS dump done");
                step = 4; break;
        }
    }
}
