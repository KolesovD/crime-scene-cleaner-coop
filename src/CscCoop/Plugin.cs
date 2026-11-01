using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace CscCoop;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "csccoop";
    public const string Name = "Crime Scene Cleaner Co-op";
    public const string Version = "0.4.2";

    internal static ManualLogSource L;
    internal static BepInEx.Configuration.ConfigEntry<bool> SkipIntro;
    internal static BepInEx.Configuration.ConfigEntry<UnityEngine.KeyCode> MenuKey, ReportKey;
    internal static BepInEx.Configuration.ConfigEntry<bool> AutoHost, AutoJoin;
    internal static BepInEx.Configuration.ConfigEntry<string> LastHost;
    internal static BepInEx.Configuration.ConfigEntry<int> LoadSlot;

    public override void Load()
    {
        L = Log;
        SkipIntro = Config.Bind("General", "SkipIntro", true, "Fast-forward the drive-in cutscene at the start of a job.");
        MenuKey = Config.Bind("General", "MenuKey", UnityEngine.KeyCode.F8, "Opens the co-op panel.");
        ReportKey = Config.Bind("General", "ReportKey", UnityEngine.KeyCode.F9, "Bug report: a screenshot and the game state for every player, saved to BepInEx/coop-reports.");
        AutoHost = Config.Bind("Connect", "AutoHost", false, "Host as soon as the game starts.");
        AutoJoin = Config.Bind("Connect", "AutoJoin", true, "Join LastHost automatically whenever that friend is hosting.");
        LastHost = Config.Bind("Connect", "LastHost", "", "SteamID of the friend joined last (set by the panel).");
        LoadSlot = Config.Bind("Connect", "LoadSlot", 0, "Save slot (1-3, as the game's menu shows them) to load from the main menu when hosting or joining automatically; 0 = choose yourself.");
        Log.LogInfo($"{Name} {Version} loaded");
        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(GrabPatches));
        harmony.PatchAll(typeof(Stains));
        harmony.PatchAll(typeof(Bodies));
        harmony.PatchAll(typeof(Doors));
        harmony.PatchAll(typeof(Collectibles));
        harmony.PatchAll(typeof(Jobs));
        harmony.PatchAll(typeof(Dyn));
        harmony.PatchAll(typeof(Trash));
        harmony.PatchAll(typeof(Quests));
        harmony.PatchAll(typeof(Furniture));
        harmony.PatchAll(typeof(Objectives));
        harmony.PatchAll(typeof(Cctv));
        harmony.PatchAll(typeof(ToolLook));
        Watchdog.Start();
        HandsOff.Start();
        AddComponent<CoopRunner>();
        AddComponent<CoopMenu>();
        AddComponent<CoopScreen>();
        AddComponent<Reports>();
        Actions.Register(CoopRunner.Instance.World, harmony);
        if (Env.Get("CSCCOOP_TEST") == "stainprobe") new Harmony(Guid + ".probe").PatchAll(typeof(StainProbePatches));
        if (Env.Get("CSCCOOP_TEST") != "") AddComponent<Bot>();
    }
}

static class Env
{
    public static string Get(string k) => System.Environment.GetEnvironmentVariable(k) ?? "";
    public static int Int(string k, int def) => int.TryParse(Get(k), out var v) ? v : def;
}
