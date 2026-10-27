using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CscCoop;

// Things the owner's manual test found unshared (2026-10-26):
//  - drawers, cabinets and room doors are Openable (Open/Close tweens), not ShortcutDoors;
//  - lifts are MovingPlatform: Interact starts a ride to the other end (isMoving, then FixedUpdate steps it with
//    ChangePlatformState and flips isInUpperPosition on arrival). The event carries the destination;
//  - boarded windows and similar break part by part: CrackedObject.CrackItem(direction);
//  - "put the furniture back": both ways of fixing a piece on its hologram end in
//    FurnitureHologram.UpdateQuest; the others get FurnitureHologram.LoadItem (the save-load path: fixes it
//    there and counts it, without the distance checks a synced, still-moving copy might fail);
//  - heavy furniture (sofas, wardrobes) is not picked up but pushed: HeavyGrabber.GrabItem parents it to the
//    player's grab point (no rigidbody), GrabChildObjects takes whatever stands on it along, and DropItem
//    unparents it and calls Release, which drops it onto the floor by a raycast and puts the things back.
//    The mover streams the pose (Ev.Heavy, 10 Hz); the others replay Grab / Release around it, so the things
//    on top ride along there too and the floor raycast comes out the same.
// All state events.
[HarmonyPatch]
public static class Furniture
{
    public static GameWorld.Table<Openable> Openables;
    public static GameWorld.Table<MovingPlatform> Lifts;
    public static GameWorld.Table<CrackedObject> Parts;
    public static GameWorld.Table<FurnitureHologram> Holograms;
    public static GameWorld.Table<FixableFurniture> Pieces;
    public static GameWorld.Table<HeavyItemController> Heavies;
    public static int Applied;
    const uint OpenKey = 0x100000, LiftKey = 0x110000, PartKey = 0x120000, HoloKey = 0x130000, HeavyKey = 0x140000;

    public static void Register(GameWorld w)
    {
        Openables = w.Register<Openable>("drawers/cabinets");
        Lifts = w.Register<MovingPlatform>("lifts");
        Parts = w.Register<CrackedObject>("breakable parts");
        Holograms = w.Register<FurnitureHologram>("furniture spots");
        Pieces = w.Register<FixableFurniture>("furniture");
        Heavies = w.Register<HeavyItemController>("heavy furniture");
    }

    // ---- drawers, cabinets, room doors ----

    // A player's click goes through Interact, which tweens and flips isOpen itself without calling Open/Close
    // (ISIL; owner's manual test: drawers and room doors weren't shared). Open/Close are watched too (scripts).
    [HarmonyPatch(typeof(Openable), nameof(Openable.Interact))] [HarmonyPostfix] static void Interact(Openable __instance) => SendOpen(__instance, __instance.isOpen);
    [HarmonyPatch(typeof(Openable), nameof(Openable.Open))] [HarmonyPostfix] static void Open(Openable __instance) => SendOpen(__instance, true);
    [HarmonyPatch(typeof(Openable), nameof(Openable.Close))] [HarmonyPostfix] static void Close(Openable __instance) => SendOpen(__instance, false);

    static void SendOpen(Openable o, bool open)
    {
        if (!WorldEvents.Live) return;
        int i = Openables.IndexOf(o);
        if (i >= 0) WorldEvents.Send(Ev.Openable, w => { w.Write((ushort)i); w.Write(open); }, OpenKey + (uint)i);
    }

    // ---- lifts ----

    // Not ChangePlatformState: that is the per-physics-frame movement step, and replaying it (with the not yet
    // flipped isInUpperPosition) made the friend's lift ride the opposite way (owner's two-PC test).
    static bool liftWasMoving;
    [HarmonyPatch(typeof(MovingPlatform), nameof(MovingPlatform.Interact))] [HarmonyPrefix]
    static void LiftPre(MovingPlatform __instance) => liftWasMoving = __instance.isMoving;

    [HarmonyPatch(typeof(MovingPlatform), nameof(MovingPlatform.Interact))] [HarmonyPostfix]
    static void Lift(MovingPlatform __instance)
    {
        if (!WorldEvents.Live || liftWasMoving || !__instance.isMoving) return;   // only a ride that just started
        int i = Lifts.IndexOf(__instance);
        bool up = !__instance.isInUpperPosition;   // heading to the other end
        if (i >= 0) WorldEvents.Send(Ev.Lift, w => { w.Write((ushort)i); w.Write(up); }, LiftKey + (uint)i);
    }

    // ---- breakable parts ----

    [HarmonyPatch(typeof(CrackedObject), nameof(CrackedObject.CrackItem))] [HarmonyPostfix]
    static void CrackPart(CrackedObject __instance, Vector3 hitDirection)
    {
        if (!WorldEvents.Live) return;
        int i = Parts.IndexOf(__instance);
        if (i >= 0) WorldEvents.Send(Ev.PartCrack, w => { w.Write((ushort)i); w.Write(hitDirection.x); w.Write(hitDirection.y); w.Write(hitDirection.z); }, PartKey + (uint)i);
    }

    // ---- furniture back in place ----

    [HarmonyPatch(typeof(FurnitureHologram), nameof(FurnitureHologram.UpdateQuest))] [HarmonyPostfix]
    static void HologramQuest(FurnitureHologram __instance, bool condition)
    {
        if (!WorldEvents.Live) return;
        int h = Holograms.IndexOf(__instance);
        int f = Pieces.IndexOf(__instance.storedFurniture);
        if (h >= 0) WorldEvents.Send(Ev.Furniture, w => { w.Write((ushort)h); w.Write(condition); w.Write((short)f); }, HoloKey + (uint)h);
    }

    // ---- heavy furniture ----

    static HeavyItemController held;   // ours, being pushed
    static float nextHeavy;
    static readonly System.Collections.Generic.Dictionary<int, (Vector3 pos, Quaternion rot)> remoteHeld = new();

    [HarmonyPatch(typeof(HeavyGrabber), nameof(HeavyGrabber.GrabItem))] [HarmonyPrefix]
    static bool HeavyGrab(HeavyItemController item)
    {
        int i = Heavies?.IndexOf(item) ?? -1;
        if (i >= 0 && remoteHeld.ContainsKey(i)) return false;   // someone else is pushing it
        return true;
    }

    [HarmonyPatch(typeof(HeavyGrabber), nameof(HeavyGrabber.GrabItem))] [HarmonyPostfix]
    static void HeavyGrabbed(HeavyGrabber __instance, HeavyItemController item)
    {
        if (__instance.GrabbedRb == null || item == null || __instance.GrabbedRb.Pointer != item.Pointer) return;
        held = item; nextHeavy = 0;
        SendHeavy(item, true);
    }

    // Prefix: the pose before Release drops it onto the floor, so the others' Release raycasts from the same place.
    [HarmonyPatch(typeof(HeavyGrabber), nameof(HeavyGrabber.DropItem))] [HarmonyPrefix]
    static void HeavyDrop(HeavyGrabber __instance)
    {
        var item = __instance.GrabbedRb;
        if (item == null) return;
        SendHeavy(item, false);
        held = null;
    }

    static void SendHeavy(HeavyItemController h, bool holding)
    {
        if (!WorldEvents.Live) return;
        int i = Heavies.IndexOf(h);
        if (i < 0) return;
        var t = h.transform; Vector3 p = t.position; Quaternion q = t.rotation;
        WorldEvents.Send(Ev.Heavy, w =>
        {
            w.Write((ushort)i); w.Write(holding);
            w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
        }, HeavyKey + (uint)i);
    }

    public static void Tick()
    {
        if (held != null && Time.unscaledTime >= nextHeavy) { nextHeavy = Time.unscaledTime + 0.1f; SendHeavy(held, true); }
        if (remoteHeld.Count == 0) return;
        float k = 1f - Mathf.Exp(-15f * Time.deltaTime);
        foreach (var kv in remoteHeld)
        {
            var h = Heavies[kv.Key];
            if (h == null) continue;
            var t = h.transform;
            t.position = Vector3.Lerp(t.position, kv.Value.pos, k);
            t.rotation = Quaternion.Slerp(t.rotation, kv.Value.rot, k);
        }
    }

    public static void OnScene() { held = null; remoteHeld.Clear(); }

    static void ApplyHeavy(int i, BinaryReader r)
    {
        bool holding = r.ReadBoolean();
        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var h = Heavies[i];
        if (h == null || (held != null && held.Pointer == h.Pointer)) return;
        bool was = remoteHeld.ContainsKey(i);
        if (holding)
        {
            if (!was) h.Grab(null);   // takes the things on top along, like on the mover's side
            remoteHeld[i] = (pos, rot);
            return;
        }
        remoteHeld.Remove(i);
        h.transform.SetPositionAndRotation(pos, rot);
        if (was) h.Release();       // floor raycast, things on top put back
        else Physics.SyncTransforms();   // late joiner: just where it ended up
    }

    // ---- washable writing ----

    // A replayed clean of writing that is already gone here would fire its quest event a second time.
    [HarmonyPatch(typeof(CleanableQuote), nameof(CleanableQuote.OnHit))] [HarmonyPrefix]
    static bool QuoteHit(CleanableQuote __instance) => !(WorldEvents.Applying && !__instance.gameObject.activeSelf);

    public static void Apply(Ev kind, BinaryReader r)
    {
        int i = r.ReadUInt16();
        switch (kind)
        {
            case Ev.Openable:
            {
                bool open = r.ReadBoolean();
                var o = Openables[i];
                if (o == null || o.isOpen == open) return;
                o.Interact();   // the same path as a click (Open/Close alone may skip what Interact does)
                if (o.isOpen != open) { if (open) o.Open(); else o.Close(); }
                break;
            }
            case Ev.Lift:
            {
                bool up = r.ReadBoolean();
                var l = Lifts[i];
                if (l == null) return;
                bool heading = l.isMoving ? !l.isInUpperPosition : l.isInUpperPosition;
                if (heading == up) return;   // already there or on its way
                if (!l.isMoving) l.Interact();
                break;
            }
            case Ev.PartCrack:
            {
                var dir = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var p = Parts[i];
                if (p == null || p.IsCracked) return;
                p.CrackItem(dir);
                break;
            }
            case Ev.Heavy: ApplyHeavy(i, r); break;
            case Ev.Furniture:
            {
                bool placed = r.ReadBoolean(); int f = r.ReadInt16();
                var h = Holograms[i];
                var piece = f >= 0 ? Pieces[f] : null;
                if (h == null) return;
                if (placed && piece != null)
                {
                    if (h.storedFurniture != null && h.storedFurniture.Pointer == piece.Pointer && h.IsCorrect) return;
                    h.LoadItem(piece);
                }
                else if (!placed) h.UpdateQuest(false);
                break;
            }
        }
        Applied++;
    }
}
