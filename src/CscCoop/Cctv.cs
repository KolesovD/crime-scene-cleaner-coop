using System.Collections.Generic;
using System.IO;
using HarmonyLib;

namespace CscCoop;

// The CCTV laptops (Smart House first; HackableComputerModel): hacked and the deleted recordings were each
// player's own (owner: "everyone has their own laptop there"). The hack (Hack(true)) and every deleted recording
// (RemoveRecordInFolder, by its name) are state events per laptop; a recording deleted on the other side is taken
// out of the list here at once and again whenever the laptop fills its folder later (PopulateRecordingData on
// first open would bring it back otherwise).
[HarmonyPatch]
public static class Cctv
{
    public static GameWorld.Table<HackableComputerModel> Laptops;
    const uint HackKey = 0x150000, DeleteBase = 0xE4000000;   // delete: laptop in bits 16-23, the name's hash below
    static readonly Dictionary<int, HashSet<string>> deleted = new();
    static bool applying;

    public static void Register(GameWorld w) => Laptops = w.Register<HackableComputerModel>("cctv laptops");
    public static void OnScene() { deleted.Clear(); seen.Clear(); hackSent.Clear(); }

    // Hack(bool) is one store and its patch never fired in the cctv suite: the laptops are also watched here, once
    // a second, and what changed (hacked, recordings gone from the folder) is sent the same way.
    static readonly Dictionary<int, HashSet<string>> seen = new();
    static readonly HashSet<int> hackSent = new();
    static float next;
    public static void Tick()
    {
        if (Laptops == null || UnityEngine.Time.unscaledTime < next || !WorldEvents.Live) return;
        next = UnityEngine.Time.unscaledTime + 1;
        for (int i = 0; i < Laptops.Count; i++)
        {
            var m = Laptops[i];
            if (m == null) continue;
            if (m.IsHacked && hackSent.Add(i)) { int k = i; WorldEvents.Send(Ev.CctvHack, w => w.Write((ushort)k), HackKey + (uint)k); Plugin.L.LogInfo($"COOP CCTV laptop {i} hacked here"); }
            var now = new HashSet<string>();
            var list = m.listOfRecordsInFolder;
            if (list != null) foreach (var rec in list) if (rec != null && rec.recordName != null) now.Add(rec.recordName);
            if (seen.TryGetValue(i, out var before))
                foreach (var name in before) if (!now.Contains(name)) SendDelete(m, name);
            seen[i] = now;
        }
    }

    static uint Hash(string s) { uint h = 2166136261; foreach (char c in s) { h ^= c; h *= 16777619; } return h & 0xFFFF; }

    [HarmonyPatch(typeof(HackableComputerModel), nameof(HackableComputerModel.Hack))] [HarmonyPostfix]
    static void Hack(HackableComputerModel __instance, bool hacked)
    {
        if (applying || !hacked || !WorldEvents.Live) return;
        int i = Laptops.IndexOf(__instance);
        if (i >= 0) WorldEvents.Send(Ev.CctvHack, w => w.Write((ushort)i), HackKey + (uint)i);
    }

    [HarmonyPatch(typeof(HackableComputerModel), nameof(HackableComputerModel.RemoveRecordInFolder), typeof(string))] [HarmonyPostfix]
    static void RemoveByName(HackableComputerModel __instance, string recordToRemove) => SendDelete(__instance, recordToRemove);

    [HarmonyPatch(typeof(HackableComputerModel), nameof(HackableComputerModel.RemoveRecordInFolder), typeof(CCTVRecord))] [HarmonyPostfix]
    static void RemoveRecord(HackableComputerModel __instance, CCTVRecord recordToRemove) => SendDelete(__instance, recordToRemove?.recordName);

    static void SendDelete(HackableComputerModel m, string name)
    {
        if (applying || name == null || !WorldEvents.Live) return;
        int i = Laptops.IndexOf(m);
        if (i < 0) return;
        if (deleted.TryGetValue(i, out var known) && known.Contains(name)) return;
        Remember(i, name);
        Plugin.L.LogInfo($"COOP CCTV laptop {i}: recording '{name}' deleted here");
        WorldEvents.Send(Ev.CctvDelete, w => { w.Write((ushort)i); w.Write(name); }, DeleteBase | ((uint)(i & 0xFF) << 16) | Hash(name));
    }

    static void Remember(int i, string name)
    {
        if (!deleted.TryGetValue(i, out var set)) deleted[i] = set = new HashSet<string>();
        set.Add(name);
    }

    [HarmonyPatch(typeof(HackableComputerModel), nameof(HackableComputerModel.PopulateRecordingData))] [HarmonyPostfix]
    static void Populated(HackableComputerModel __instance)
    {
        int i = Laptops?.IndexOf(__instance) ?? -1;
        if (i < 0 || !deleted.TryGetValue(i, out var set)) return;
        applying = true;
        try { foreach (var name in set) __instance.RemoveRecordInFolder(name); }
        finally { applying = false; }
    }

    public static void Apply(Ev kind, BinaryReader r)
    {
        int i = r.ReadUInt16();
        var m = Laptops[i];
        applying = true;
        try
        {
            if (kind == Ev.CctvHack)
            {
                hackSent.Add(i);
                if (m == null || m.IsHacked) return;
                m.Hack(true);
                Plugin.L.LogInfo($"COOP CCTV laptop {i} hacked by another player");
            }
            else
            {
                string name = r.ReadString();
                Remember(i, name);
                if (m == null) return;
                m.RemoveRecordInFolder(name);
                Plugin.L.LogInfo($"COOP CCTV laptop {i}: recording '{name}' deleted by another player");
            }
        }
        finally { applying = false; }
    }
}
