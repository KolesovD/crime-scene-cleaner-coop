using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CscCoop;

// One-off investigation (CSCCOOP_TEST=menuprobe, one copy, tools/run-probe.ps1): how the main menu's buttons are
// built (to add a "Co-op" one) and which mails and jobs a save has in the hub (to mirror the host's laptop).
// Menu: dumps the main menu section. Then loads slot 1, plays through to the hub (leaving the first job) and
// lists the mails, active and completed jobs there.
static class MenuProbe
{
    static int step, leaves;
    static float at;
    static string dumped = "";

    public static void Tick(GameWorld w)
    {
        string scene = SceneManager.GetActiveScene().name;
        if (Time.unscaledTime < at) return;
        at = Time.unscaledTime + 2;
        if (step < 0) return;
        string dir = Env.Get("CSCCOOP_SHOT_DIR");
        void Shot(string n) { if (dir != "") ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"shot_menu_{n}.png")); }
        var cs = CoopScreen.Instance;
        if (step == 0 && scene.StartsWith("MenuScreen") && Time.unscaledTime > 30) { DumpMenu(); Plugin.L.LogInfo($"MENUPROBE co-op button {cs.ButtonAdded}"); Shot("main"); step = 10; return; }
        if (step == 10) { cs.PressMenuButton(); Plugin.L.LogInfo($"MENUPROBE pressed: page {cs.Current}"); step = 11; return; }
        if (step == 11) { Shot("root"); cs.Join(); step = 12; return; }
        if (step == 12) { Shot("slots"); cs.Slot(1, false); step = 13; return; }
        if (step == 13) { Shot("friends"); Plugin.L.LogInfo($"MENUPROBE page {cs.Current}"); cs.Back(); cs.Back(); cs.Back(); Plugin.L.LogInfo($"MENUPROBE after 3x back: page {cs.Current}"); step = 14; return; }
        if (step == 14)
        {
            var ss = SaveSystem.Instance;
            for (int n = 1; n <= 3; n++) Plugin.L.LogInfo($"MENUPROBE slot {n} available {CoopScreen.SlotExists(ss, n)} {(CoopScreen.SlotExists(ss, n) ? CoopScreen.SlotInfo(ss, n) : "")}");
            int slot = Env.Int("CSCCOOP_SLOT", 1);
            Plugin.L.LogInfo($"MENUPROBE LoadSlot({slot})");
            ss.LoadSlot(CoopScreen.GameSlot(slot));
            step = 1; return;
        }
        if (w.Player == null || !w.Player.CanMove || scene.Contains("Loading")) return;
        if (dumped != scene) { dumped = scene; at = Time.unscaledTime + 5; DumpJobs(scene); return; }
        if (scene == "HubResidence" || scene == "Hub02" || leaves >= 2)
        {
            Plugin.L.LogInfo("BOT done, quitting");
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
        var report = Object.FindObjectOfType<MissionReportFinalView>();
        if (report != null && report.isActiveAndEnabled) { report.OnCloseReport(); at = Time.unscaledTime + 10; return; }
        if (!scene.Contains("Hub"))
        {
            var leave = Object.FindObjectOfType<MissionLeaveController>();
            if (leave != null && step < 2 + leaves) { leaves++; step++; Plugin.L.LogInfo("MENUPROBE leaving the job"); leave.LeaveMissionWithReport(); at = Time.unscaledTime + 15; }
        }
    }

    static void DumpMenu()
    {
        var mb = Object.FindObjectOfType<MenuButtonsController>();
        if (mb == null) { Plugin.L.LogInfo("MENUPROBE no MenuButtonsController"); return; }
        foreach (var (n, b) in new[] { ("continue", mb.continueButton), ("new", mb.newGameButton), ("load", mb.loadSaveButton), ("settings", mb.settingsButton), ("exit", mb.exitButton) })
        {
            if (b == null) { Plugin.L.LogInfo($"MENUPROBE button {n} null"); continue; }
            var rt = b.GetComponent<RectTransform>();
            Plugin.L.LogInfo($"MENUPROBE button {n} '{Path(b.transform)}' active {b.gameObject.activeInHierarchy} pos {rt.anchoredPosition} size {rt.sizeDelta} anchors {rt.anchorMin}-{rt.anchorMax} sibling {b.transform.GetSiblingIndex()}");
        }
        var section = mb.mainMenu;
        if (section != null) Dump(section.transform, 0);
    }

    static void Dump(Transform t, int depth)
    {
        if (depth > 4) return;
        var sb = new StringBuilder();
        foreach (var c in t.GetComponents<Component>()) sb.Append(c.GetIl2CppType().Name).Append(' ');
        var rt = t.GetComponent<RectTransform>();
        var tmp = t.GetComponent<TMPro.TextMeshProUGUI>();
        Plugin.L.LogInfo($"MENUPROBE {new string(' ', depth * 2)}{t.name} [{sb}] {(rt != null ? $"pos {rt.anchoredPosition} size {rt.sizeDelta}" : "")}{(tmp != null ? $" text '{tmp.text}'" : "")}");
        for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1);
    }

    static void DumpJobs(string scene)
    {
        var mc = MissionController.Instance;
        Plugin.L.LogInfo($"MENUPROBE jobs in '{scene}'");
        if (mc == null) { Plugin.L.LogInfo("MENUPROBE no MissionController"); return; }
        if (mc.AvailableMails != null)
            foreach (var m in mc.AvailableMails) Plugin.L.LogInfo($"MENUPROBE mail {m.id} '{m.Name}' hasMission {m.hasMission} mission {m.missionId} accepted {m.HasBeenAccepted}");
        if (mc.ActiveMissions != null) foreach (var m in mc.ActiveMissions) Plugin.L.LogInfo($"MENUPROBE active {m.id} '{m.displayName}'");
        if (mc.CompletedMissions != null) foreach (var m in mc.CompletedMissions) Plugin.L.LogInfo($"MENUPROBE completed {m.id} '{m.displayName}'");
        Plugin.L.LogInfo($"MENUPROBE MissionsMapController {Resources.FindObjectsOfTypeAll<MissionsMapController>().Length}, MailController {Resources.FindObjectsOfTypeAll<MailController>().Length}, active mission {GameManager.Instance?.ActiveMission?.id}");
    }

    static string Path(Transform t) => t == null ? "" : t.parent == null ? t.name : Path(t.parent) + "/" + t.name;
}
