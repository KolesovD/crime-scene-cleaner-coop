using System.IO;
using HarmonyLib;
using PixelCrushers.DialogueSystem;

namespace CscCoop;

// Job quests (PixelCrushers Dialogue System). Every quest change, whatever made it, ends in
// QuestLog.DefaultSetQuestState / DefaultSetQuestEntryState (the default overrides behind SetQuestState and
// SetQuestEntryState). Those are state events per quest / quest entry, applied with the same call; most
// quest progress also follows from the shared world (stains, bodies, trash), this covers the rest
// (locations found, talks, scripted steps).
[HarmonyPatch]
public static class Quests
{
    public static int Applied;
    const uint QuestBase = 0xF0000000;

    static uint Key(string quest, int entry)
    {
        uint h = 2166136261;
        foreach (char c in quest) { h ^= c; h *= 16777619; }
        return QuestBase | ((h & 0x0FFFF00) + (uint)(entry & 0xFF));
    }

    [HarmonyPatch(typeof(QuestLog), nameof(QuestLog.DefaultSetQuestState))] [HarmonyPostfix]
    static void SetQuestState(string questName, string state)
    {
        if (!WorldEvents.Live || questName == null) return;
        WorldEvents.Send(Ev.Quest, w => { w.Write(questName); w.Write(-1); w.Write(state ?? ""); }, Key(questName, 0xFF));
    }

    [HarmonyPatch(typeof(QuestLog), nameof(QuestLog.DefaultSetQuestEntryState))] [HarmonyPostfix]
    static void SetEntryState(string questName, int entryNumber, string state)
    {
        if (!WorldEvents.Live || questName == null) return;
        WorldEvents.Send(Ev.Quest, w => { w.Write(questName); w.Write(entryNumber); w.Write(state ?? ""); }, Key(questName, entryNumber));
    }

    public static void OnQuest(BinaryReader r)
    {
        string quest = r.ReadString(); int entry = r.ReadInt32(); string state = r.ReadString();
        if (entry < 0)
        {
            if (QuestLog.DefaultStateToString(QuestLog.GetQuestState(quest)) == state) return;
            QuestLog.DefaultSetQuestState(quest, state);
        }
        else
        {
            if (QuestLog.DefaultStateToString(QuestLog.GetQuestEntryState(quest, entry)) == state) return;
            QuestLog.DefaultSetQuestEntryState(quest, entry, state);
        }
        Applied++;
        Plugin.L.LogInfo($"COOP quest '{quest}'{(entry < 0 ? "" : " entry " + entry)} -> {state} from another player");
    }
}
