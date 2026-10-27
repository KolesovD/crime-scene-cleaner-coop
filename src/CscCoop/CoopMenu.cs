using System;
using System.Collections.Generic;
using CscCoop.Net;
using UnityEngine;

namespace CscCoop;

// F8 (by default): in the main menu the co-op screen (CoopScreen), in play the co-op panel (status, players
// with ping, hosting or joining from the hub, leaving, settings). Always: the corner label (status, players,
// ping) while in a session, and short notices (Toast). And the automatic part of getting together:
//  - AutoHost: start hosting as soon as Steam is up, no F8 needed;
//  - AutoJoin: join the last host (LastHost) whenever that friend is hosting, also after either restarts.
//  - LoadSlot: in the main menu, an automatic host or join also loads this save slot (SaveSystem.LoadSlot),
//    so a player goes from the menu straight into the hub and then follows the host's job.
// All are settings in BepInEx/config/csccoop.cfg and switches in the panel.
public class CoopMenu : MonoBehaviour
{
    public CoopMenu(IntPtr p) : base(p) { }

    public static CoopMenu Instance;
    void Awake() => Instance = this;

    bool open, blockedPlayer;
    float nextScan;
    List<SteamDirectory.Friend> hosts = new();
    CursorLockMode oldLock;
    bool oldVisible;
    Texture2D bg;

    public bool IsOpen => open;

    static string toast = "";
    static float toastUntil;
    public static void Toast(string text, float seconds = 4) { toast = text; toastUntil = Time.unscaledTime + seconds; }
    readonly HashSet<byte> seen = new();

    void Update()
    {
        if (Input.GetKeyDown(Plugin.MenuKey.Value))
        {
            if (CoopScreen.InMainMenu) { var cs = CoopScreen.Instance; if (cs.Current == CoopScreen.Page.Closed) cs.Open(); else cs.Close(); }
            else Toggle();
        }
        var run = CoopRunner.Instance;
        if (Env.Get("CSCCOOP_ROLE") == "" && Env.Get("CSCCOOP_TEST") == "") Automatic(run);
        Arrivals(run.Session);
        if (!open) return;
        Cursor.lockState = CursorLockMode.None;   // the game re-locks it every frame
        Cursor.visible = true;
    }

    // "Игрок 2 подключился / отключился".
    void Arrivals(CoopCore.Session s)
    {
        if (s == null) { seen.Clear(); return; }
        foreach (var n in s.Players.Keys) if (seen.Add(n)) Toast(s.IsHost || n != CoopCore.Session.HostNumber ? $"Игрок {n} подключился" : "Подключились к хосту");
        foreach (var n in new List<byte>(seen)) if (!s.Players.ContainsKey(n)) { seen.Remove(n); Toast(n == CoopCore.Session.HostNumber ? "Хост отключился" : $"Игрок {n} отключился"); }
    }

    // Every 3 s while not in a session: refresh the list of hosting friends, auto-host or auto-join.
    void Automatic(CoopRunner run)
    {
        if (run.Session != null || Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 3;
        if (Time.unscaledTime < 10) return;   // let the game bring Steam up first
        try { run.Steam ??= new SteamDirectory(); hosts = run.Steam.Hosts(); }
        catch (Exception e) { run.Error = "Steam недоступен: " + e.Message; return; }
        if (Plugin.AutoHost.Value) { Plugin.L.LogInfo("COOP auto-host"); run.HostSteam(); LoadChosenSlot(); return; }
        if (Plugin.AutoJoin.Value && ulong.TryParse(Plugin.LastHost.Value, out var last))
            foreach (var h in hosts)
                if (h.Id == last) { Plugin.L.LogInfo($"COOP auto-join {h.Name}"); run.JoinSteam(h.Id); LoadChosenSlot(); return; }
    }

    void Join(CoopRunner run, SteamDirectory.Friend h)
    {
        Plugin.LastHost.Value = h.Id.ToString();
        Plugin.AutoHost.Value = false;
        run.JoinSteam(h.Id);
    }

    // From the main menu only: load the chosen slot into the hub (the job ticket then takes over).
    public void LoadChosenSlot()
    {
        int slot = Plugin.LoadSlot.Value;
        var ss = SaveSystem.Instance;
        if (!CoopScreen.InMainMenu || slot < 1 || slot > 3 || ss == null || !CoopScreen.SlotExists(ss, slot)) return;
        CoopScreen.Instance.Close();
        CoopScreen.LoadSlot(slot, false);
    }

    public void Toggle()
    {
        open = !open;
        var player = CoopRunner.Instance.World.Player;
        if (open)
        {
            oldLock = Cursor.lockState; oldVisible = Cursor.visible;
            nextScan = 0;
            if (player != null && player.CanMove) { player.Block(); blockedPlayer = true; }
        }
        else
        {
            Cursor.lockState = oldLock; Cursor.visible = oldVisible;
            if (blockedPlayer && player != null) player.Unlock();
            blockedPlayer = false;
        }
    }

    GUIStyle corner;

    void OnGUI()
    {
        var run = CoopRunner.Instance;
        if (run == null) return;
        if (run.Session != null || Time.unscaledTime < toastUntil)
        {
            corner ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(13, Screen.height / 72), fontStyle = FontStyle.Bold, richText = true, wordWrap = true };
            float y = 6;
            if (run.Session != null) { Shadowed(new Rect(12, y, 900, 30), $"Кооп: {run.ConnectionText()}{run.PingText()}"); y += corner.fontSize + 10; }
            if (Time.unscaledTime < toastUntil) Shadowed(new Rect(12, y, Mathf.Min(900, Screen.width - 24), 90), $"<color=#ffcc55>{toast}</color>");
        }
        if (!open) return;
        var area = new Rect(20, 60, 540, 560);
        if (bg == null) { bg = new Texture2D(1, 1); bg.SetPixel(0, 0, new Color(0, 0, 0, 0.85f)); bg.Apply(); }
        GUI.DrawTexture(area, bg);
        GUILayout.BeginArea(new Rect(area.x + 12, area.y + 10, area.width - 24, area.height - 20));
        GUILayout.Label($"<b>Crime Scene Cleaner Co-op {Plugin.Version}</b>");
        GUILayout.Label("Статус: " + run.ConnectionText() + run.PingText());
        var s = run.Session;
        if (run.TestMode)
        {
            // Manual / bot test: a localhost session set up at start; Steam hosting here would replace it
            // (owner's manual test: "Host" in one window cut the other off).
            GUILayout.Label($"Тестовый режим: {Env.Get("CSCCOOP_ROLE")}, локальная сеть.");
            if (s != null) foreach (var p in s.Players.Values) GUILayout.Label("  " + run.PlayerLine(p));
            if (GUILayout.Button("Переподключить (тест)")) run.StartFromEnv();
        }
        else if (s == null)
        {
            GUILayout.Label("Сохранение и друга удобнее выбрать в главном меню: «Кооператив».");
            if (GUILayout.Button("Хостить (друзья смогут зайти ко мне)")) { Plugin.AutoHost.Value = true; run.HostSteam(); }
            GUILayout.Space(6);
            GUILayout.Label(hosts.Count == 0 ? "Сейчас никто из друзей не хостит (список обновляется сам)." : "Друзья, которые хостят:");
            foreach (var h in hosts)
                if (GUILayout.Button($"Зайти к {h.Name}")) Join(run, h);
        }
        else
        {
            if (s.IsHost) GUILayout.Label("Друзья заходят через «Кооператив» в главном меню или сами, если у них включён автовход.");
            foreach (var p in s.Players.Values) GUILayout.Label("  " + run.PlayerLine(p));
            if (GUILayout.Button("Выйти из коопа"))
            {
                Plugin.AutoHost.Value = false; Plugin.AutoJoin.Value = false;   // otherwise the automatic part rejoins at once
                run.Stop();
            }
        }
        GUILayout.Space(8);
        Plugin.AutoHost.Value = GUILayout.Toggle(Plugin.AutoHost.Value, " Хостить автоматически при запуске игры");
        Plugin.AutoJoin.Value = GUILayout.Toggle(Plugin.AutoJoin.Value, " Заходить автоматически к последнему хосту");
        Plugin.SkipIntro.Value = GUILayout.Toggle(Plugin.SkipIntro.Value, " Пропускать заставку в начале заказа");
        if (run.Error != "") GUILayout.Label("<color=red>" + run.Error + "</color>");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Закрыть")) Toggle();
        GUILayout.EndArea();
    }

    void Shadowed(Rect r, string text)
    {
        var c = GUI.color;
        GUI.color = new Color(0, 0, 0, 0.8f);
        GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), System.Text.RegularExpressions.Regex.Replace(text, "<color=[^>]*>|</color>", ""), corner);
        GUI.color = c;
        GUI.Label(r, text, corner);
    }
}
