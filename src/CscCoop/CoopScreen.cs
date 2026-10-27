using System;
using System.Collections.Generic;
using CscCoop.Net;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace CscCoop;

// The co-op screen of the main menu: a "Кооператив" button added to the game's own menu list opens it (F8 too).
//   Кооператив -> Хостить -> slot -> hosting starts and the slot loads;
//              -> Присоединиться -> slot -> friend -> "Играть у ..." joins and loads the slot (one click), or
//                 "Ждать ..." loads the slot and joins as soon as that friend hosts (AutoJoin on that friend);
//              -> Назад.
// A slot is loaded with SaveSystem.LoadSlot (the game's "Load game"); an empty slot can start a new game there
// (SaveSystem.StartGame, the game's "New game": it makes a fresh save in that slot, so never for a used one).
// The bot drives the same methods the buttons call (Open/Host/Join/Slot/Play), so tests go the players' way.
public class CoopScreen : MonoBehaviour
{
    public CoopScreen(IntPtr p) : base(p) { }

    public static CoopScreen Instance;
    void Awake() => Instance = this;

    public enum Page { Closed, Root, Slot, Friend }
    public Page Current { get; private set; }
    bool hosting;            // which way the slot page goes on
    int slot;                // chosen on the slot page
    bool newGame;            // the chosen slot is empty: a new game starts there
    float nextScan;
    List<SteamDirectory.Friend> friends = new();
    EventSystem menuEvents;  // the game's menu input, off while the screen is up (it is drawn over the menu)
    GameObject menuButton;
    public bool ButtonAdded => menuButton != null;

    public static bool InMainMenu => SceneManager.GetActiveScene().name.StartsWith("MenuScreen");

    void Update()
    {
        if (!InMainMenu) { if (Current != Page.Closed) Close(); return; }
        if (menuButton == null) AddMenuButton();
        if (Current == Page.Closed) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (Input.GetKeyDown(KeyCode.Escape)) Back();
        var run = CoopRunner.Instance;
        if (Current == Page.Friend && Time.unscaledTime >= nextScan && !run.TestMode)
        {
            nextScan = Time.unscaledTime + 3;
            try { run.Steam ??= new SteamDirectory(); friends = run.Steam.Everyone(); run.Error = ""; }
            catch (Exception e) { run.Error = "Steam недоступен: " + e.Message; }
        }
    }

    // A copy of the game's "Load game" button in the menu's VerticalLayoutGroup, after "New game".
    void AddMenuButton()
    {
        var mb = FindObjectOfType<MenuButtonsController>();
        var src = mb != null ? mb.loadSaveButton : null;
        if (src == null || !src.gameObject.activeInHierarchy) return;
        var go = Instantiate(src.gameObject, src.transform.parent);
        go.name = "CoopButton";
        int after = mb.newGameButton != null ? mb.newGameButton.transform.GetSiblingIndex() : src.transform.GetSiblingIndex();
        go.transform.SetSiblingIndex(after + 1);
        // The game's LocalizedText would put "Загрузить игру" back.
        foreach (var lt in go.GetComponentsInChildren<LocalizedText>(true)) Destroy(lt);
        foreach (var t in go.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true)) t.text = "Кооператив";
        var b = go.GetComponent<UICustomButton>();
        b.add_OnClick(Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(Open)));
        menuButton = go;
        Plugin.L.LogInfo($"COOP co-op button added to the main menu at {go.transform.GetSiblingIndex()}");
    }

    // The bot presses the menu button the way the mouse does.
    public void PressMenuButton()
    {
        if (menuButton != null && EventSystem.current != null) menuButton.GetComponent<UICustomButton>().OnPointerClick(new PointerEventData(EventSystem.current));
    }

    public void Open()
    {
        if (!InMainMenu) return;
        if (CoopMenu.Instance.IsOpen) CoopMenu.Instance.Toggle();
        Current = Page.Root;
        menuEvents = EventSystem.current;
        if (menuEvents != null) menuEvents.enabled = false;
        Plugin.L.LogInfo("COOPSCREEN opened");
    }

    public void Close()
    {
        Current = Page.Closed;
        if (menuEvents != null) menuEvents.enabled = true;
        menuEvents = null;
    }

    public void Back()
    {
        Current = Current switch { Page.Friend => Page.Slot, Page.Slot => Page.Root, _ => Page.Closed };
        if (Current == Page.Closed) Close();
    }

    public void Host() { hosting = true; Current = Page.Slot; Plugin.L.LogInfo("COOPSCREEN host: choose a slot"); }
    public void Join() { hosting = false; Current = Page.Slot; nextScan = 0; Plugin.L.LogInfo("COOPSCREEN join: choose a slot"); }

    public void Slot(int n, bool fresh)
    {
        slot = n; newGame = fresh;
        Plugin.L.LogInfo($"COOPSCREEN slot {n}{(fresh ? " (new game)" : "")}");
        if (!hosting) { Current = Page.Friend; nextScan = 0; return; }
        var run = CoopRunner.Instance;
        if (run.TestMode) run.StartFromEnv();
        else { Plugin.AutoHost.Value = true; run.HostSteam(); }
        if (run.Session == null) return;   // Steam not up: the error shows on the root page
        Plugin.L.LogInfo($"COOPSCREEN hosting with slot {n}");
        Load();
    }

    // The hosts to join: Steam friends in this game, or in a localhost test the test host.
    public IReadOnlyList<SteamDirectory.Friend> Friends => CoopRunner.Instance.TestMode
        ? new[] { new SteamDirectory.Friend { Id = 0, Name = "тестовый хост (localhost)", Hosting = true } } : friends;

    public void Play(SteamDirectory.Friend f)
    {
        var run = CoopRunner.Instance;
        Plugin.AutoHost.Value = false;
        if (run.TestMode) run.StartFromEnv();
        else
        {
            Plugin.LastHost.Value = f.Id.ToString();
            if (!f.Hosting) { Plugin.AutoJoin.Value = true; Plugin.L.LogInfo($"COOPSCREEN waiting for {f.Name} to host"); Load(); return; }
            run.JoinSteam(f.Id);
        }
        if (run.Session == null) return;
        Plugin.L.LogInfo($"COOPSCREEN playing at {f.Name} with slot {slot}");
        Load();
    }

    void Load()
    {
        Plugin.LoadSlot.Value = slot;
        Close();
        LoadSlot(slot, newGame);
    }

    // The game numbers its slots from 0 (GetSlotFileName: 0 -> Slot1.es3, 1 -> Slot2, 2 -> Slot3, anything else
    // -> Slot1); the screen and the LoadSlot setting count 1-3 like the game's menu shows them.
    public static int GameSlot(int n) => n - 1;
    public static bool SlotExists(SaveSystem ss, int n) => ss.IsFileAvailable(GameSlot(n));

    // The game's own two ways in: LoadSlot loads a save (into the hub, or back into the job it was saved in);
    // StartGame begins a new game and makes a fresh save in that slot.
    public static void LoadSlot(int n, bool fresh)
    {
        var ss = SaveSystem.Instance;
        if (ss == null || n < 1 || n > 3) return;
        bool exists = SlotExists(ss, n);
        if (fresh && exists) { Plugin.L.LogWarning($"COOP slot {n} is in use: not starting a new game over it"); return; }
        if (!fresh && !exists) { Plugin.L.LogWarning($"COOP slot {n} is empty: nothing to load"); return; }
        Plugin.L.LogInfo(fresh ? $"COOP new game in slot {n}" : $"COOP loading save slot {n}");
        if (fresh) ss.StartGame(GameSlot(n)); else ss.LoadSlot(GameSlot(n));
    }

    // ---- drawing ----

    GUIStyle title, text, small, button, big, disabled, middle;
    Texture2D bgTex, btnTex, btnHover, cardTex;
    const float W = 760;

    static Texture2D Tex(Color c) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); return t; }

    void Styles()
    {
        if (title != null) return;
        bgTex = Tex(new Color(0.03f, 0.035f, 0.04f, 0.94f));
        btnTex = Tex(new Color(0.16f, 0.17f, 0.18f, 1));
        btnHover = Tex(new Color(0.85f, 0.62f, 0.12f, 1));
        cardTex = Tex(new Color(0.10f, 0.11f, 0.12f, 1));
        title = new GUIStyle(GUI.skin.label) { fontSize = 46, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, richText = true };
        title.normal.textColor = Color.white;
        text = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true, richText = true };
        text.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
        middle = new GUIStyle(text) { alignment = TextAnchor.MiddleLeft, wordWrap = false };
        small = new GUIStyle(text) { fontSize = 18 };
        small.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
        button = new GUIStyle(GUI.skin.button) { fontSize = 26, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(24, 16, 10, 10), richText = true, wordWrap = true };
        button.normal.background = btnTex; button.normal.textColor = Color.white;
        button.hover.background = btnHover; button.hover.textColor = Color.black;
        button.active.background = btnHover; button.active.textColor = Color.black;
        big = new GUIStyle(button) { fontSize = 30, fontStyle = FontStyle.Bold };
        disabled = new GUIStyle(button);
        disabled.normal.textColor = disabled.hover.textColor = new Color(0.5f, 0.5f, 0.5f);
        disabled.hover.background = btnTex;
    }

    void OnGUI()
    {
        if (Current == Page.Closed || !InMainMenu) return;
        Styles();
        float s = Screen.height / 1080f;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), bgTex);
        var old = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1));
        float vw = Screen.width / s;
        GUILayout.BeginArea(new Rect((vw - W) / 2, 90, W, 1080 - 150));
        GUILayout.Label("Кооператив", title);
        GUILayout.Label($"<color=#d99e1f>Crime Scene Cleaner Co-op {Plugin.Version}</color>   ·   {CoopRunner.Instance.ConnectionText()}", small);
        GUILayout.Space(24);
        switch (Current)
        {
            case Page.Root: RootPage(); break;
            case Page.Slot: SlotPage(); break;
            case Page.Friend: FriendPage(); break;
        }
        var err = CoopRunner.Instance.Error;
        if (err != "") { GUILayout.Space(12); GUILayout.Label("<color=#ff6060>" + err + "</color>", text); }
        GUILayout.EndArea();
        GUI.matrix = old;
    }

    // A switch drawn as a button with a visible box (the label style has no checkbox images).
    bool Check(bool on, string label) => GUILayout.Button($"<color={(on ? "#d99e1f" : "#777")}>{(on ? "■" : "□")}</color>  {label}", text) ? !on : on;

    bool Btn(string label, GUIStyle st = null, float h = 72) => GUILayout.Button(label, st ?? button, GUILayout.Height(h));

    void RootPage()
    {
        var run = CoopRunner.Instance;
        if (run.Session != null)
        {
            GUILayout.Label("Кооп уже включён. Загрузи сохранение через «Продолжить» или «Загрузить игру» — или выйди из коопа, чтобы выбрать заново.", text);
            GUILayout.Space(12);
            if (Btn("Выйти из коопа")) { Plugin.AutoHost.Value = false; Plugin.AutoJoin.Value = false; run.Stop(); }
            GUILayout.Space(10);
            if (Btn("Назад", button, 60)) Back();
            return;
        }
        GUILayout.Label("Хост играет в своём сохранении, друзья заходят к нему со своими: деньги, навыки и пройденные заказы у каждого свои.", text);
        GUILayout.Space(18);
        if (Btn("Хостить   <size=20><color=#999>друзья заходят ко мне</color></size>", big, 84)) Host();
        GUILayout.Space(10);
        if (Btn("Присоединиться   <size=20><color=#999>к другу, который хостит</color></size>", big, 84)) Join();
        GUILayout.Space(10);
        if (Btn("Назад", button, 60)) Back();
        GUILayout.Space(28);
        Plugin.AutoJoin.Value = Check(Plugin.AutoJoin.Value, "Заходить автоматически к последнему хосту");
        Plugin.SkipIntro.Value = Check(Plugin.SkipIntro.Value, "Пропускать заставку в начале заказа");
    }

    void SlotPage()
    {
        var ss = SaveSystem.Instance;
        GUILayout.Label(hosting ? "Хостить: выбери сохранение" : "Присоединиться: выбери своё сохранение", text);
        GUILayout.Space(10);
        if (ss == null) { GUILayout.Label("Сохранения ещё не загружены…", text); }
        else
            for (int n = 1; n <= 3; n++)
            {
                if (SlotExists(ss, n))
                {
                    if (Btn($"Слот {n}\n<size=20><color=#aaa>{SlotInfo(ss, n)}</color></size>", button, 90)) Slot(n, false);
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Слот {n} — пусто", middle, GUILayout.Width(300), GUILayout.Height(70));
                    if (Btn("Новая игра в этом слоте", button, 70)) Slot(n, true);
                    GUILayout.EndHorizontal();
                }
                GUILayout.Space(8);
            }
        GUILayout.Space(4);
        GUILayout.Label("Новая игра начинается с пролога: пройди его, а в квартире подключишься к хосту сам (автовход).", small);
        GUILayout.Space(10);
        if (Btn("Назад", button, 60)) Back();
    }

    public static string SlotInfo(SaveSystem ss, int n)
    {
        try
        {
            var hdr = ss.GetSaveFileHeaderInfo(GameSlot(n));
            if (hdr == null) return "";
            var parts = new List<string> { $"глава {hdr.ChapterNumber}" };
            if (!string.IsNullOrWhiteSpace(hdr.timePlayed)) parts.Add($"наиграно {hdr.timePlayed}");
            if (!string.IsNullOrWhiteSpace(hdr.lastSave)) parts.Add(hdr.lastSave);
            return string.Join("  ·  ", parts);
        }
        catch { return ""; }
    }

    void FriendPage()
    {
        GUILayout.Label($"Присоединиться со слотом {slot}{(newGame ? " (новая игра)" : "")}: к кому?", text);
        GUILayout.Space(10);
        var list = Friends;
        if (list.Count == 0) GUILayout.Label("Никого из друзей Steam сейчас нет в Crime Scene Cleaner. Список обновляется сам.", text);
        foreach (var f in list)
        {
            if (f.Hosting) { if (Btn($"Играть у {f.Name}   <size=20><color=#999>хостит</color></size>", big, 80)) Play(f); }
            else if (Btn($"Ждать {f.Name}   <size=20><color=#999>не хостит: загружу слот и зайду, когда начнёт</color></size>", button, 80)) Play(f);
            GUILayout.Space(8);
        }
        GUILayout.Space(10);
        if (Btn("Назад", button, 60)) Back();
    }
}
