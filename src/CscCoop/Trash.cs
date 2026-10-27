using System.IO;
using HarmonyLib;

namespace CscCoop;

// Trash picked up with the trash can. The can calls CollectableItem.Collect for every item it sucks in
// (GarbageCollector.GrabItem): collider off, kinematic, removed from GarbageManager (what the report
// counts). The others get the same Collect and the item disappears there; the can's fill level stays
// with the player whose can it is. The bag the can makes later is a dynamic object (Dyn).
[HarmonyPatch]
public static class Trash
{
    public static GameWorld.Table<CollectableItem> Items;
    public static int Applied;
    const uint Key = 0xE0000;

    public static void Register(GameWorld w) => Items = w.Register<CollectableItem>("trash");

    [HarmonyPatch(typeof(CollectableItem), nameof(CollectableItem.Collect))] [HarmonyPostfix]
    static void Collect(CollectableItem __instance)
    {
        Watchdog.Where = "Trash.Collect patch";
        if (!WorldEvents.Live) return;
        int i = Items.IndexOf(__instance);
        if (i >= 0) WorldEvents.Send(Ev.TrashCollect, w => w.Write((ushort)i), Key + (uint)i);
        else
        {
            // Trash made during the job. A part of a broken object (CrackedObject, made collectable when it breaks)
            // is in the parts table; a shard of a broken item is a dynamic object, maybe one level up.
            var part = __instance.GetComponentInParent<CrackedObject>();
            int p = part != null ? Furniture.Parts.IndexOf(part) : -1;
            if (p >= 0) { WorldEvents.Send(Ev.PartCollect, w => w.Write((ushort)p), 0x140000u + (uint)p); return; }
            var g = __instance.GetComponentInParent<GrabableItemController>();
            Dyn.OnConsumed(g != null ? g.gameObject : __instance.gameObject);
        }
    }

    public static void OnPartCollect(BinaryReader r)
    {
        var part = Furniture.Parts[r.ReadUInt16()];
        if (part == null) return;
        var c = part.GetComponentInChildren<CollectableItem>(true);
        if (c != null && !c.isCollected) c.Collect();
        part.gameObject.SetActive(false);
        Applied++;
    }

    public static void OnCollect(BinaryReader r)
    {
        var c = Items[r.ReadUInt16()];
        if (c == null || c.isCollected) return;
        c.Collect();
        c.gameObject.SetActive(false);
        Applied++;
    }
}
