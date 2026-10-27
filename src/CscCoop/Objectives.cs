using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using PixelCrushers.DialogueSystem;
using UnityEngine.Localization;

namespace CscCoop;

// Job objectives on both machines (owner's two-window tests of 0.4.0: an attic only one player had "checked"
// kept the job open, a walkie-talkie each player had to pick up, "get rid of the victims' bodies" missing on the
// client, a restart only one player got). Quest and entry states were shared already (Quests). Added:
//  - Dialogue System Lua variables that are flags or text: locations discovered (SetLocationDiscovered), items
//    inspected, story flags set by scripts. True wins over false; text is the latest. The player's own location
//    (SetLoc) stays personal.
//  - objectives added during the job (AddQuestWithName, by the cleaning/evidence/garbage/furniture/
//    contamination counters when their first thing happens on that machine), with the same localized text.
//  - a job restart (GameManager.RestartMission) by either player restarts it for both.
// Numbers never cross (counts, percents, numeric Lua values). Each machine's counters count the world events the
// mod already shares (van, stains, trash, furniture); merging their numbers too counted things twice: BodyQuest
// adds 1 to the Lua value it reads, so one body read the client's 1 and then counted itself, 2/2 on the host;
// a blood percent from the other machine made the client's game wipe the rest of its blood, and left the
// host at 100% with blood still there (owner's test on Dealer's Apartment, 2026-10-27).
[HarmonyPatch]
public static class Objectives
{
    public static int Applied;
    const uint VarBase = 0xE1000000, AddBase = 0xE3000000;
    static int personal;   // inside SetLoc: the player's own location variable
    static bool applying;
    static readonly HashSet<string> added = new();

    public static void OnScene() => added.Clear();

    // A boxed Il2Cpp value (Lua variables take Il2CppSystem.Object).
    internal static Il2CppSystem.Object Box(float v) => BoxRaw(Il2CppInterop.Runtime.Il2CppClassPointerStore<float>.NativeClassPtr, new[] { v });
    internal static Il2CppSystem.Object Box(bool v) => BoxRaw(Il2CppInterop.Runtime.Il2CppClassPointerStore<bool>.NativeClassPtr, new[] { v });
    static Il2CppSystem.Object BoxRaw(System.IntPtr cls, System.Array data)
    {
        var h = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { return new Il2CppSystem.Object(Il2CppInterop.Runtime.IL2CPP.il2cpp_value_box(cls, h.AddrOfPinnedObject())); }
        finally { h.Free(); }
    }

    static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h & 0x00FFFFFF;
    }

    // ---- Lua variables ----

    [HarmonyPatch(typeof(DialogueSystemQuestController), nameof(DialogueSystemQuestController.SetLoc))] [HarmonyPrefix]
    static void LocPre() => personal++;
    [HarmonyPatch(typeof(DialogueSystemQuestController), nameof(DialogueSystemQuestController.SetLoc))] [HarmonyPostfix]
    static void LocPost() => personal--;

    [HarmonyPatch(typeof(DialogueLua), nameof(DialogueLua.SetVariable))] [HarmonyPostfix]
    static void SetVariable(string variable, Il2CppSystem.Object value)
    {
        if (applying || personal > 0 || variable == null || value == null) return;
        string type = value.GetIl2CppType().Name;
        byte kind; string text;
        if (type == "Boolean") { kind = 1; try { text = value.Unbox<bool>() ? "True" : "False"; } catch { return; } }
        else if (type == "String") { kind = 3; text = value.ToString(); }
        else return;   // numbers: each machine counts its own (see above)
        if (!WorldEvents.Live) return;
        WorldEvents.Send(Ev.LuaVar, w => { w.Write(variable); w.Write(kind); w.Write(text); }, VarBase | Hash(variable));
    }

    public static void OnVar(BinaryReader r)
    {
        string variable = r.ReadString(); byte kind = r.ReadByte(); string text = r.ReadString();
        var cur = DialogueLua.GetVariable(variable);
        applying = true;
        try
        {
            switch (kind)
            {
                case 1:
                    if (text != "True" || (cur.hasReturnValue && cur.isBool && cur.asBool)) return;
                    DialogueLua.SetVariable(variable, Box(true));
                    break;
                case 3:
                    if (cur.hasReturnValue && cur.isString && cur.asString == text) return;
                    DialogueLua.SetVariable(variable, (Il2CppSystem.Object)(Il2CppSystem.String)text);
                    break;
                default:
                    return;   // a number from an older build: not taken
            }
            DialogueManager.SendUpdateTracker();
        }
        finally { applying = false; }
        Applied++;
        Plugin.L.LogInfo($"COOP objective variable {variable} = {text} from another player");
    }

    // ---- objectives added during the job ----

    [HarmonyPatch(typeof(DialogueSystemQuestController), nameof(DialogueSystemQuestController.AddQuestWithName))] [HarmonyPostfix]
    static void AddQuest(string name, string location, LocalizedString ls)
    {
        if (applying || name == null || !added.Add(name) || !WorldEvents.Live) return;
        string table = "", entry = ""; long keyId = 0;
        if (ls != null)
        {
            var tr = ls.TableReference; var er = ls.TableEntryReference;
            table = tr.TableCollectionName ?? "";
            if (table == "") table = "guid:" + tr.TableCollectionNameGuid.ToString();
            keyId = er.KeyId; entry = er.Key ?? "";
        }
        WorldEvents.Send(Ev.ObjectiveAdd, w => { w.Write(name); w.Write(location ?? ""); w.Write(table); w.Write(keyId); w.Write(entry); }, AddBase | Hash(name));
    }

    public static void OnAdd(BinaryReader r)
    {
        string name = r.ReadString(), location = r.ReadString(), table = r.ReadString(); long keyId = r.ReadInt64(); string entry = r.ReadString();
        if (!added.Add(name)) return;
        var c = DialogueSystemQuestController.Instance;
        if (c == null) return;
        LocalizedString ls = null;
        if (table != "")
        {
            UnityEngine.Localization.Tables.TableReference tr = table.StartsWith("guid:")
                ? Il2CppSystem.Guid.Parse(table.Substring(5))
                : table;
            UnityEngine.Localization.Tables.TableEntryReference er = keyId != 0 ? keyId : entry;
            ls = new LocalizedString(tr, er);
        }
        applying = true;
        try { c.AddQuestWithName(name, location == "" ? null : location, ls); DialogueManager.SendUpdateTracker(); }
        finally { applying = false; }
        Applied++;
        Plugin.L.LogInfo($"COOP objective '{name}' added by another player");
    }

    // ---- restart ----

    static bool restarting;
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.RestartMission))] [HarmonyPrefix]
    static void Restart(MissionData mission, bool loadData)
    {
        if (restarting || mission == null || !WorldEvents.Live) return;
        int id = mission.id;
        Plugin.L.LogInfo($"COOP restarting job {id} for everyone");
        WorldEvents.Send(Ev.JobRestart, w => { w.Write(id); w.Write(loadData); });
    }

    public static void OnRestart(BinaryReader r)
    {
        int id = r.ReadInt32(); bool loadData = r.ReadBoolean();
        var m = MissionController.Instance?.GetMission(id);
        if (m == null || GameManager.Instance == null) return;
        Plugin.L.LogInfo($"COOP the other player restarted job {id}: restarting it here too");
        restarting = true;
        try { GameManager.Instance.RestartMission(m, loadData); }
        finally { restarting = false; }
    }
}
