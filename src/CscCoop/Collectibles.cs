using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CscCoop;

// Things found or taken once per job, all state events:
//  - cassettes (MusicCasette.UnlockMusic -> CasettesController.UnlockCasette): everyone unlocks the song;
//  - secrets (SecretArea.UnlockSecret): discovered for everyone, with the game's own notification;
//  - valuables (MissionStealingController.AddStolenItemFunds = OnSteal + the report's stolenItems list,
//    without the pick-up animation): stolen for everyone, so everyone's report pays for it;
//  - breakables: DestroyableItem.CrackItem(velocity) (swaps the item for shards) and DestroyableObject.Destroy.
//    The shards themselves are new objects and are simulated by each machine on its own (not shared yet).
[HarmonyPatch]
public static class Collectibles
{
    public static GameWorld.Table<MusicCasette> Cassettes;
    public static GameWorld.Table<SecretArea> Secrets;
    public static GameWorld.Table<StealableItem> Valuables;
    public static GameWorld.Table<DestroyableItem> Breakables;
    public static GameWorld.Table<DestroyableObject> BreakableObjects;
    public static int Applied;

    public static void Register(GameWorld w)
    {
        Cassettes = w.Register<MusicCasette>("cassettes");
        Secrets = w.Register<SecretArea>("secrets");
        Valuables = w.Register<StealableItem>("valuables");
        Breakables = w.Register<DestroyableItem>("breakables");
        BreakableObjects = w.Register<DestroyableObject>("breakable objects");
    }

    const uint CassetteKey = 0x80000, SecretKey = 0x90000, StealKey = 0xA0000, CrackKey = 0xB0000, DestroyKey = 0xC0000;

    static void Send<T>(Ev kind, GameWorld.Table<T> table, T c, uint baseKey) where T : Component
    {
        if (!WorldEvents.Live) return;
        int i = table.IndexOf(c);
        if (i >= 0) WorldEvents.Send(kind, w => w.Write((ushort)i), baseKey + (uint)i);
    }

    [HarmonyPatch(typeof(MusicCasette), nameof(MusicCasette.UnlockMusic))] [HarmonyPostfix]
    static void Cassette(MusicCasette __instance) => Send(Ev.Cassette, Cassettes, __instance, CassetteKey);

    [HarmonyPatch(typeof(SecretArea), nameof(SecretArea.UnlockSecret))] [HarmonyPostfix]
    static void Secret(SecretArea __instance) => Send(Ev.Secret, Secrets, __instance, SecretKey);

    // StealableItem.OnSteal, not AddStolenItemFunds: the real pick-up (MissionStealingController.Steal's tween
    // callback) calls OnSteal and adds to stolenItems itself (ISIL), so valuables taken by hand weren't shared
    // (owner's manual test: a valuable from a cabinet stayed on the host). The others get AddStolenItemFunds,
    // which is OnSteal + the report list without the animation.
    [HarmonyPatch(typeof(StealableItem), nameof(StealableItem.OnSteal))] [HarmonyPostfix]
    static void Steal(StealableItem __instance) => Send(Ev.Steal, Valuables, __instance, StealKey);

    [HarmonyPatch(typeof(DestroyableObject), nameof(DestroyableObject.Destroy))] [HarmonyPostfix]
    static void DestroyObject(DestroyableObject __instance) => Send(Ev.BreakObject, BreakableObjects, __instance, DestroyKey);

    [HarmonyPatch(typeof(DestroyableItem), nameof(DestroyableItem.CrackItem))] [HarmonyPostfix]
    static void Crack(DestroyableItem __instance, Vector3 velocity)
    {
        if (!WorldEvents.Live) return;
        int i = Breakables.IndexOf(__instance);
        if (i >= 0) WorldEvents.Send(Ev.Crack, w => { w.Write((ushort)i); w.Write(velocity.x); w.Write(velocity.y); w.Write(velocity.z); }, CrackKey + (uint)i);
        if (i >= 0) Dyn.RegisterShards(__instance, i, true);
    }

    public static void Apply(Ev kind, BinaryReader r)
    {
        int i = r.ReadUInt16();
        switch (kind)
        {
            case Ev.Cassette:
                Cassettes[i]?.UnlockMusic();
                break;
            case Ev.Secret:
                var s = Secrets[i];
                if (s == null || s.isDiscovered) return;
                s.UnlockSecret();
                break;
            case Ev.Steal:
                var v = Valuables[i];
                var msc = MissionStealingController.Instance;
                if (v == null || v.wasStolen || msc == null) return;
                msc.AddStolenItemFunds(v);
                v.gameObject.SetActive(false);   // the thief's game hides it with the pick-up animation; here it just goes (owner's test: it stayed visible)
                break;
            case Ev.BreakObject:
                var o = BreakableObjects[i];
                if (o == null || o.wasDestroyed) return;
                o.Destroy();
                break;
            case Ev.Crack:
                var vel = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var d = Breakables[i];
                if (d == null || d.WasCracked) return;
                d.CrackItem(vel);
                Dyn.RegisterShards(d, i, false);
                break;
        }
        Applied++;
    }
}
