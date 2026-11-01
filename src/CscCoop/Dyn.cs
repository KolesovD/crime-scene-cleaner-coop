using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace CscCoop;

// Objects that appear during a job and are not in the scene tables: garbage bags (made by the trash can)
// and each player's own tools (bucket, lamp, power washer station: PortableItem).
//  - The machine where one appears owns it and sends its state (kind, prefab name, pose, scale, alive) as a
//    state event per object, 10 times a second while it moves, so late joiners get it too.
//  - The others build a puppet from the same prefab (bags: the trash can's garbagePrefab; tools: a loaded
//    PortableItem prefab of the same name), kinematic, following the owner's pose.
//  - Grabbing a puppet takes it over (DynTake, ordered by the host): the grabber simulates it from then on and
//    the old owner turns its copy into a puppet. That includes other players' tools: anyone can carry a friend's
//    bucket or lamp (owner's request). Only the player it belongs to (the id's player number) can put a tool back
//    into an inventory (PortableItem.PickUp): picked up by someone else it would be in two inventories; its owner
//    takes it back first if someone else simulates it.
//  - Buckets carry their water and dirt level (LiquidContainer) and their cleaning products (activeBuffs, as
//    "buffId:effectiveness:threshold;..."), power washer stations their water and products: a friend's bucket
//    looks the same, and a mop rinsed in it takes its products (CleanTool copies the bucket's buffs). Using another
//    player's bucket or station here (rinsing, pouring a product or water in, emptying it) takes it over first, so its new
//    levels and products come from this machine (as in the earlier csc-coop prototype).
//  - Gone (bag merged into another, bag into the van, tool picked up): alive=false removes the puppet; a bag
//    that went into the van is collected on the others' side too, so their report counts it.
[HarmonyPatch]
public static class Dyn
{
    public enum Kind : byte { Bag = 1, Tool = 2, Shard = 3 }

    sealed class Obj
    {
        public uint Id;
        public Kind Kind;
        public string Name;
        public GameObject Go;
        public bool Mine;
        public Vector3 SentPos, TargetPos;
        public Quaternion SentRot, TargetRot = Quaternion.identity;
        public Vector3 Scale = Vector3.one;
        public float NextSend;
        public LiquidContainer Liquid;
        public PowerWasherStation Washer;
        public LampController Lamp;
        public bool SentLamp;
        public float SentWater = -1, SentTaint = -1;
        public string SentBuffs = "", AppliedBuffs = "";
    }

    static readonly Dictionary<uint, Obj> byId = new();
    static readonly Dictionary<IntPtr, Obj> byGo = new();
    static uint counter;
    static bool building;   // creating a puppet: its Awake/Start must not register it as ours
    static uint sceneKey;
    public static int Spawned, Puppets, Taken;

    static CoopCore.Session S => CoopRunner.Instance?.Session;
    static GameWorld W => CoopRunner.Instance.World;
    const uint StateBase = 0xD0000000;

    // ---- registering local objects ----

    [HarmonyPatch(typeof(GarbageBagConnector), nameof(GarbageBagConnector.Awake))] [HarmonyPostfix]
    static void BagAwake(GarbageBagConnector __instance) => Register(__instance.gameObject, Kind.Bag);

    [HarmonyPatch(typeof(PortableItem), nameof(PortableItem.Start))] [HarmonyPostfix]
    static void ToolStart(PortableItem __instance) => Register(__instance.gameObject, Kind.Tool);

    static void Register(GameObject go, Kind kind)
    {
        if (building || go == null || byGo.ContainsKey(go.Pointer)) return;
        var s = S;
        byte me = s == null ? (byte)0 : s.Me == CoopCore.Session.NoPlayer ? (byte)0 : s.Me;
        var o = new Obj { Id = ((uint)me << 20) | (++counter & 0xFFFFF), Kind = kind, Name = PrefabName(go.name), Go = go, Mine = true };
        if (kind == Kind.Tool) Equip(o);
        byId[o.Id] = o; byGo[go.Pointer] = o;
        Spawned++;
    }

    static string PrefabName(string n) => n.Replace("(Clone)", "").Trim();

    // ---- every frame ----

    public static void Tick()
    {
        var s = S;
        if (s == null) return;
        if (sceneKey != W.SceneKey) OnScene();
        if (!s.Synced) return;
        UvSlots.Tick();
        float now = Time.unscaledTime;
        foreach (var o in new List<Obj>(byId.Values))
        {
            if (o.Mine)
            {
                if (o.Go == null) { if (o.Name != null) SendState(o, false, false); Forget(o); continue; }
                if (now < o.NextSend) continue;
                var t = o.Go.transform;
                bool moved = (t.position - o.SentPos).sqrMagnitude > 1e-4f || Quaternion.Angle(t.rotation, o.SentRot) > 0.5f || o.SentPos == Vector3.zero;
                if (o.Liquid != null && (Mathf.Abs(o.Liquid.WaterLevel - o.SentWater) > 0.01f || Mathf.Abs(o.Liquid.TaintLevel - o.SentTaint) > 0.01f)) moved = true;
                if (o.Washer != null && Mathf.Abs(o.Washer.waterLevel - o.SentWater) > 0.01f) moved = true;
                if ((o.Liquid != null || o.Washer != null) && Buffs(o) != o.SentBuffs) moved = true;
                if (o.Lamp != null && o.Lamp.isOn != o.SentLamp) moved = true;
                if (!moved && o.Go.activeInHierarchy == true && o.NextSend > 0) continue;
                o.NextSend = now + 0.1f;
                SendState(o, o.Go.activeInHierarchy, false);
            }
            else if (o.Go != null)
            {
                var prb = o.Go.GetComponent<Rigidbody>();
                if (prb != null && !prb.isKinematic) prb.isKinematic = true;   // the game may switch physics back on
                var t = o.Go.transform;
                float k = 1 - Mathf.Exp(-Time.deltaTime * 20);
                var pos = Vector3.Distance(t.position, o.TargetPos) > 3 ? o.TargetPos : Vector3.Lerp(t.position, o.TargetPos, k);
                t.SetPositionAndRotation(pos, Quaternion.Slerp(t.rotation, o.TargetRot, k));
            }
        }
    }

    // A new scene: objects of the old one are gone. Tools that live under the player stay registered.
    static void OnScene()
    {
        sceneKey = W.SceneKey;
        foreach (var o in new List<Obj>(byId.Values))
            if (!o.Mine || o.Go == null) { if (!o.Mine && o.Go != null) UnityEngine.Object.Destroy(o.Go); Forget(o); }
            else o.SentPos = Vector3.zero;   // resend in the new scene
    }

    static void Forget(Obj o) { byId.Remove(o.Id); if (o.Go != null) byGo.Remove(o.Go.Pointer); }

    static void SendState(Obj o, bool alive, bool inVan)
    {
        var t = o.Go != null ? o.Go.transform : null;
        var pos = t != null ? t.position : o.SentPos;
        var rot = t != null ? t.rotation : o.SentRot;
        var scale = t != null ? t.localScale : o.Scale;
        o.SentPos = pos; o.SentRot = rot;
        bool liquid = o.Liquid != null;
        float water = liquid ? o.Liquid.WaterLevel : 0, taint = liquid ? o.Liquid.TaintLevel : 0;
        bool washer = o.Washer != null;
        if (washer) water = o.Washer.waterLevel;
        string buffs = Buffs(o);
        bool lamp = o.Lamp != null, lampOn = lamp && o.Lamp.isOn;
        o.SentWater = water; o.SentTaint = taint; o.SentBuffs = buffs; o.SentLamp = lampOn;
        WorldEvents.Send(Ev.DynState, w =>
        {
            w.Write(o.Id); w.Write((byte)o.Kind); w.Write(o.Name ?? "");
            w.Write(pos.x); w.Write(pos.y); w.Write(pos.z); w.Write(rot.x); w.Write(rot.y); w.Write(rot.z); w.Write(rot.w);
            w.Write(scale.x); w.Write(scale.y); w.Write(scale.z);
            w.Write(alive); w.Write(inVan);
            w.Write(liquid); w.Write(water); w.Write(taint);
            w.Write(buffs); w.Write(washer);   // last: 0.4.2 readers stop before them
            w.Write(lamp); w.Write(lampOn);
        }, StateBase | o.Id);
    }

    public static void OnState(BinaryReader r)
    {
        uint id = r.ReadUInt32(); var kind = (Kind)r.ReadByte(); string name = r.ReadString();
        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var rot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var scale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        bool alive = r.ReadBoolean(), inVan = r.ReadBoolean();
        bool liquid = r.ReadBoolean(); float water = r.ReadSingle(), taint = r.ReadSingle();
        bool more = r.BaseStream.Position < r.BaseStream.Length;
        string buffs = more ? r.ReadString() : null;
        bool washer = more && r.ReadBoolean();
        bool lampKnown = more && r.BaseStream.Position < r.BaseStream.Length;
        bool lamp = lampKnown && r.ReadBoolean(), lampOn = lampKnown && r.ReadBoolean();
        byId.TryGetValue(id, out var o);
        if (o == null && kind == Kind.Shard)
        {
            // The crack's shards may appear a frame after CrackItem, so pairing at the crack can find none
            // (world2 suite, 1 run in 2): pair them now.
            int breakable = (int)((id & 0x07FFFFFF) >> 6);
            var d = Collectibles.Breakables[breakable];
            if (d != null) RegisterShards(d, breakable, false);
            byId.TryGetValue(id, out o);
        }
        // Ours: a late echo after we took it over, except "gone": another player's trash can picked it up
        // (OnConsumed), and it must go here too, or a shard picked up there stayed here (breakhit suite).
        if (o != null && o.Mine && (alive || !inVan)) return;
        if (!alive)
        {
            if (o?.Go != null)
            {
                var gi = o.Go.GetComponent<GarbageItem>();
                var ci = o.Go.GetComponent<CollectableItem>();
                if (inVan && gi != null && !gi.isCollected) { var trunk = UnityEngine.Object.FindObjectOfType<TrunkController>(); if (trunk != null) trunk.TryShowProp(gi); gi.CollectAndDestroy(); }
                else if (inVan && ci != null) { if (!ci.isCollected) ci.Collect(); o.Go.SetActive(false); }
                else if (o.Kind == Kind.Shard) o.Go.SetActive(false);
                else UnityEngine.Object.Destroy(o.Go);
            }
            if (o != null) Forget(o);
            return;
        }
        if (o == null)
        {
            var go = Build(kind, name, pos, rot);
            if (go == null) return;
            o = new Obj { Id = id, Kind = kind, Name = name, Go = go, Mine = false };
            if (kind == Kind.Tool) Equip(o);
            byId[id] = o; byGo[go.Pointer] = o;
            Puppets++;
        }
        o.TargetPos = pos; o.TargetRot = rot;
        if (o.Go != null) o.Go.transform.localScale = scale;
        if (liquid && o.Liquid != null)
        {
            if (Mathf.Abs(o.Liquid.WaterLevel - water) > 0.005f) o.Liquid.SetWaterLevel(water);
            if (Mathf.Abs(o.Liquid.TaintLevel - taint) > 0.005f) o.Liquid.SetTaintLevel(taint);
        }
        if (washer && o.Washer != null && Mathf.Abs(o.Washer.waterLevel - water) > 0.005f) { o.Washer.waterLevel = water; o.Washer.SetWaterLevel(); }
        // Switch sets the light, Interact flips isOn (earlier prototype's lamp sync)
        if (lamp && o.Lamp != null && o.Lamp.isOn != lampOn) { o.Lamp.isOn = lampOn; o.Lamp.Switch(lampOn); Plugin.L.LogInfo($"COOP {o.Name} {o.Id:X} switched {(lampOn ? "on" : "off")} by its player"); }
        if (buffs != null && buffs != o.AppliedBuffs && (o.Liquid != null || o.Washer != null))
        {
            o.AppliedBuffs = buffs;
            if (o.Liquid != null) SetBuffs(o.Liquid.activeBuffs, buffs, d => o.Liquid.TryFindBuffBase(d, out var b) ? b : null);
            else SetBuffs(o.Washer.activeBuffs, buffs, d => o.Washer.TryFindBuffBase(d, out var b) ? b : null);
            Plugin.L.LogInfo($"COOP {o.Name} {o.Id:X}: products '{buffs}'");
        }
    }

    static void Equip(Obj o)
    {
        o.Liquid = o.Go.GetComponentInChildren<LiquidContainer>(true);
        var pi = o.Go.GetComponent<PortableItem>();
        o.Washer = pi != null ? pi.TryCast<PowerWasherStation>() : null;
        o.Lamp = o.Go.GetComponentInChildren<LampController>(true);
    }

    // ---- cleaning products ----

    static string Buffs(Obj o)
    {
        var list = o.Liquid != null ? o.Liquid.activeBuffs : o.Washer != null ? o.Washer.activeBuffs : null;
        if (list == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var b in list)
        {
            if (b == null) continue;
            var d = b.CreateBuffData();
            sb.Append(System.FormattableString.Invariant($"{d.buffId}:{d.effectiveness:F2}:{d.currentEffectivenessThreshold:F2};"));
        }
        return sb.ToString();
    }

    static void SetBuffs(Il2CppSystem.Collections.Generic.List<BuffInstance> list, string s, Func<BucketBuffData, BuffBase> find)
    {
        if (list == null) return;
        list.Clear();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(':');
            if (f.Length != 3) continue;
            var data = new BucketBuffData { buffId = int.Parse(f[0], ci), effectiveness = float.Parse(f[1], ci), currentEffectivenessThreshold = float.Parse(f[2], ci) };
            var buff = find(data);
            if (buff == null) { Plugin.L.LogWarning($"COOP unknown cleaning product {data.buffId}"); continue; }
            var bi = new BuffInstance(buff);
            bi.LoadBuffData(data);
            list.Add(bi);
        }
    }

    // A player here uses another player's bucket or station: take it over first (its levels and products then
    // go out from here). Instance cast checked: IL2CPP folds identical bodies, so a patch can fire for other types.
    static void Use(Component c)
    {
        var s = S;
        if (c == null || s == null || !s.Synced || WorldEvents.Applying || Env.Get("CSCCOOP_MUTANT") == "bucket-look") return;
        var pi = c.GetComponentInParent<PortableItem>();
        if (pi == null || !byGo.TryGetValue(pi.gameObject.Pointer, out var o) || o.Mine || o.Kind != Kind.Tool) return;
        TakeOver(o, s);
    }

    [HarmonyPatch(typeof(LiquidContainer), nameof(LiquidContainer.CleanTool))] [HarmonyPrefix] static void BucketRinse(LiquidContainer __instance) { if (__instance.TryCast<LiquidContainer>() != null) Use(__instance); }
    [HarmonyPatch(typeof(LiquidContainer), nameof(LiquidContainer.TryAddBuff))] [HarmonyPrefix] static void BucketProduct(LiquidContainer __instance) { if (__instance.TryCast<LiquidContainer>() != null) Use(__instance); }
    [HarmonyPatch(typeof(LiquidContainer), nameof(LiquidContainer.TryAddWater))] [HarmonyPrefix] static void BucketWater(LiquidContainer __instance) { if (__instance.TryCast<LiquidContainer>() != null) Use(__instance); }
    [HarmonyPatch(typeof(LiquidContainer), nameof(LiquidContainer.AddWater))] [HarmonyPrefix] static void BucketFill(LiquidContainer __instance) { if (__instance.TryCast<LiquidContainer>() != null) Use(__instance); }
    [HarmonyPatch(typeof(LiquidContainer), nameof(LiquidContainer.CleanWater))] [HarmonyPrefix] static void BucketEmpty(LiquidContainer __instance) { if (__instance.TryCast<LiquidContainer>() != null) Use(__instance); }
    // Switching another player's lamp here: take it over, so its new on/off goes out from here.
    [HarmonyPatch(typeof(LampController), nameof(LampController.Interact))] [HarmonyPrefix] static void LampSwitch(LampController __instance) { if (__instance.TryCast<LampController>() != null) Use(__instance); }
    [HarmonyPatch(typeof(UVLampController), nameof(UVLampController.Interact))] [HarmonyPrefix] static void UvLampSwitch(UVLampController __instance) { if (__instance.TryCast<UVLampController>() != null) Use(__instance); }
    [HarmonyPatch(typeof(PowerWasherStation), nameof(PowerWasherStation.TryAddBuff))] [HarmonyPrefix] static void WasherProduct(PowerWasherStation __instance) { if (__instance.TryCast<PowerWasherStation>() != null) Use(__instance); }
    [HarmonyPatch(typeof(PowerWasherStation), nameof(PowerWasherStation.TryAddWater))] [HarmonyPrefix] static void WasherWater(PowerWasherStation __instance) { if (__instance.TryCast<PowerWasherStation>() != null) Use(__instance); }

    static GameObject Build(Kind kind, string name, Vector3 pos, Quaternion rot)
    {
        if (kind == Kind.Shard) return null;   // shards come from replaying the crack, not from a prefab
        GameObject prefab = null;
        if (kind == Kind.Bag)
            foreach (var gc in Resources.FindObjectsOfTypeAll<GarbageCollector>()) if (gc.garbagePrefab != null) { prefab = gc.garbagePrefab; break; }
        if (kind == Kind.Tool)
            foreach (var p in Resources.FindObjectsOfTypeAll<PortableItem>()) if (p != null && PrefabName(p.name) == name) { prefab = p.gameObject; break; }
        if (prefab == null) { Plugin.L.LogWarning($"COOP no prefab for {kind} '{name}' here: not shown"); return null; }
        building = true;
        GameObject go;
        try { go = UnityEngine.Object.Instantiate(prefab, pos, rot); }
        finally { building = false; }
        go.name = name + " (other player)";
        go.SetActive(true);
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true;
        Plugin.L.LogInfo($"COOP showing another player's {kind} '{name}'");
        return go;
    }

    public static bool IsMine(GameObject go) => go != null && byGo.TryGetValue(go.Pointer, out var o) && o.Mine;

    public static bool IsPuppet(GameObject go) => go != null && byGo.TryGetValue(go.Pointer, out var o) && !o.Mine;

    // ---- taking over a bag ----

    // Called from the grab patch for objects that are not in the item table.
    public static bool OnLocalGrab(GrabableItemController item)
    {
        if (item == null || !byGo.TryGetValue(item.gameObject.Pointer, out var o) || o.Mine) return true;
        if (o.Kind == Kind.Tool && Env.Get("CSCCOOP_MUTANT") == "tool-locked") return false;   // the 0.4.2 rule
        var s = S;
        if (s == null || !s.Synced) return true;
        TakeOver(o, s);
        return true;
    }

    static void TakeOver(Obj o, CoopCore.Session s)
    {
        o.Mine = true; o.NextSend = 0;
        foreach (var rb in o.Go.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = false;
        byte me = s.Me;
        WorldEvents.Send(Ev.DynTake, w => { w.Write(o.Id); w.Write(me); });
        Taken++;
        Plugin.L.LogInfo($"COOP took over {o.Kind.ToString().ToLowerInvariant()} {o.Id:X} ({o.Name})");
    }

    // Whose inventory a tool belongs to: the player number its id was made with.
    static byte BelongsTo(Obj o) => (byte)(o.Id >> 20);

    // Putting a placed tool back into the inventory: only its own player's, and taken back first. The power
    // washer station has its own PickUp (it overrides PortableItem's).
    [HarmonyPatch(typeof(PowerWasherStation), nameof(PowerWasherStation.PickUp))] [HarmonyPrefix]
    static bool WasherPickUp(PowerWasherStation __instance) => __instance.TryCast<PowerWasherStation>() == null || ToolPickUp(__instance);

    [HarmonyPatch(typeof(PortableItem), nameof(PortableItem.PickUp))] [HarmonyPrefix]
    static bool ToolPickUp(PortableItem __instance)
    {
        var s = S;
        if (s == null || !s.Synced || __instance == null || !byGo.TryGetValue(__instance.gameObject.Pointer, out var o) || o.Kind != Kind.Tool) return true;
        byte me = s.Me == CoopCore.Session.NoPlayer ? (byte)0 : s.Me;
        if (BelongsTo(o) != me)
        {
            Plugin.L.LogInfo($"COOP pick-up of player {BelongsTo(o)}'s {o.Name} refused: it isn't ours (it can be carried)");
            return false;
        }
        if (!o.Mine) TakeOver(o, s);
        return true;
    }

    public static void OnTake(byte from, BinaryReader r)
    {
        uint id = r.ReadUInt32(); r.ReadByte();
        if (!byId.TryGetValue(id, out var o) || o.Go == null) return;
        if (o.Mine)
        {
            var g = W.Grabber;
            if (g != null && g.GrabbedRb != null && g.GrabbedRb.gameObject.Pointer == o.Go.Pointer) g.TryRelease();
            o.Mine = false;
            foreach (var rb in o.Go.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true;
            o.TargetPos = o.Go.transform.position; o.TargetRot = o.Go.transform.rotation;
            Plugin.L.LogInfo($"COOP player {from} took over {o.Kind.ToString().ToLowerInvariant()} {o.Id:X} ({o.Name})");
        }
    }

    // A puppet must not run game logic that changes the world: bags merging on collision happen on the owner.
    [HarmonyPatch(typeof(GarbageBagConnector), nameof(GarbageBagConnector.OnCollisionEnter))] [HarmonyPrefix]
    static bool BagCollision(GarbageBagConnector __instance) => !IsPuppet(__instance.gameObject);

    // A bag of ours went into the van.
    public static bool OnCollected(GarbageItem gi)
    {
        if (gi == null || !byGo.TryGetValue(gi.gameObject.Pointer, out var o) || !o.Mine) return false;
        SendState(o, false, true);
        Forget(o);
        return true;
    }

    // Shards: a cracked item breaks the same way on every machine (Collectibles replays CrackItem), and
    // DestroyableItem.CreatedCracks lists the shards in the same order, so shard k of breakable i has the same
    // id everywhere. The machine that broke it simulates them; the others' shards follow.
    public static void RegisterShards(DestroyableItem d, int breakable, bool mine)
    {
        var list = d != null ? d.CreatedCracks : null;
        Plugin.L.LogInfo($"COOP shards of breakable {breakable}: {(list == null ? "no list" : list.Count + " shards")} ({(mine ? "mine" : "other player's")})");
        if (list == null) return;
        for (int k = 0; k < list.Count && k < 64; k++)
        {
            var g = list[k];
            if (g == null) continue;
            uint id = 0x08000000u | ((uint)breakable << 6) | (uint)k;
            if (byId.ContainsKey(id)) continue;
            var o = new Obj { Id = id, Kind = Kind.Shard, Name = "", Go = g.gameObject, Mine = mine };
            if (!mine) { foreach (var rb in g.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true; o.TargetPos = g.transform.position; o.TargetRot = g.transform.rotation; }
            byId[id] = o; byGo[g.gameObject.Pointer] = o;
        }
    }

    // A dynamic object of ours got used up (a shard picked up by the trash can): gone for everyone, and the
    // others' copy counts as collected too.
    public static void OnConsumed(GameObject go)
    {
        if (go == null || !byGo.TryGetValue(go.Pointer, out var o)) { Plugin.L.LogInfo($"COOP collected '{go?.name}': not a shared object"); return; }
        SendState(o, false, true);
        Forget(o);
    }

    public static int Count(bool mine) { int n = 0; foreach (var o in byId.Values) if (o.Mine == mine && o.Go != null) n++; return n; }
}
