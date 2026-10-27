using System;
using System.IO;
using System.Text;
using PixelCrushers.DialogueSystem;
using UnityEngine;

namespace CscCoop;

// Bug reports from play (owner's request for testing with the friend): F9 saves a screenshot and the state of
// this game (job, objectives with their counts, what the player holds, where, ping) to BepInEx\coop-reports, and
// the other players save theirs under the same id at the same moment. A box then asks "what's wrong?"; Enter
// saves the comment into every player's report, F9 again skips it. The player can't move while typing.
public class Reports : MonoBehaviour
{
    public static Reports Instance;
    static string dir;
    string typingId;      // the report whose comment is being typed
    string comment = "";
    bool focusSet;
    GUIStyle box, label;

    void Awake()
    {
        Instance = this;
        dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "coop-reports");
    }

    void Update()
    {
        if (!Input.GetKeyDown(Plugin.ReportKey.Value)) return;
        if (typingId != null) { EndTyping(false); return; }
        Press();
    }

    // Tests: F9 and a typed comment, as a player does it.
    public void TestReport(string text) { Press(); comment = text; EndTyping(true); }

    void Press()
    {
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Take(id, null);
        WorldEvents.SendGlobal(Ev.Report, w => { w.Write(id); w.Write(Who()); });
        typingId = id; comment = ""; focusSet = false;
        CoopRunner.Instance?.World?.Player?.Block();
    }

    static string Who()
    {
        var s = CoopRunner.Instance?.Session;
        return s == null ? "без кооп-сессии" : s.IsHost ? "хост" : $"игрок {s.Me}";
    }

    // Another player pressed F9: the same moment from here.
    public static void OnReport(BinaryReader r)
    {
        string id = r.ReadString(), from = r.ReadString();
        Take(id, from);
        Plugin.L.LogInfo($"COOP report {id} asked by {from}");
    }

    public static void OnNote(BinaryReader r)
    {
        string id = r.ReadString(), from = r.ReadString(), text = r.ReadString();
        Append(id, $"Комментарий ({from}): {text}");
    }

    static string FileBase(string id) => Path.Combine(dir, $"{id}-{(Who() == "хост" ? "host" : "client")}");

    static void Take(string id, string askedBy)
    {
        try
        {
            Directory.CreateDirectory(dir);
            ScreenCapture.CaptureScreenshot(FileBase(id) + ".png");
            File.WriteAllText(FileBase(id) + ".txt", State(id, askedBy), Encoding.UTF8);
            Plugin.L.LogInfo($"COOP report {id} saved to {FileBase(id)}.png/.txt");
        }
        catch (Exception e) { Plugin.L.LogWarning($"COOP report {id} failed: {e.Message}"); }
    }

    static void Append(string id, string line)
    {
        try { File.AppendAllText(FileBase(id) + ".txt", line + "\n", Encoding.UTF8); } catch { }
    }

    static string State(string id, string askedBy)
    {
        var sb = new StringBuilder();
        var run = CoopRunner.Instance;
        var w = run?.World;
        sb.AppendLine($"Отчёт {id}, Crime Scene Cleaner Co-op {Plugin.Version}, {Who()}{(askedBy != null ? $", снимок по F9 игрока «{askedBy}»" : ", нажал F9")}");
        sb.AppendLine($"Сцена: {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}; кооп: {run?.ConnectionText()}{run?.PingText()}");
        if (w?.Player != null)
        {
            var p = w.Player.transform.position;
            sb.AppendLine($"Позиция: {p.x:F2} {p.y:F2} {p.z:F2}; может двигаться: {w.Player.CanMove}");
        }
        if (w?.Grabber != null)
        {
            var held = w.Grabber.GrabbedRb;
            sb.AppendLine($"В руках: {(held != null ? held.name : "-")}{(w.Grabber.IsGrabbingBody ? ", несёт тело" : "")}");
        }
        sb.AppendLine("Задания:");
        try
        {
            foreach (var st in new[] { QuestState.Active, QuestState.Success })
                foreach (var q in QuestLog.GetAllQuests(st) ?? new string[0])
                {
                    var line = new StringBuilder($"  [{st}] {q}");
                    foreach (var v in new[] { "Current", "Max", "BodiesMin" })
                    {
                        var r = DialogueLua.GetVariable(q + "." + v);
                        if (r.hasReturnValue && r.isNumber) line.Append($" {v}={r.asFloat:0.##}");
                    }
                    int n = QuestLog.GetQuestEntryCount(q);
                    for (int e = 1; e <= n; e++) line.Append($" | {e}: {QuestLog.GetQuestEntryState(q, e)}");
                    sb.AppendLine(line.ToString());
                }
        }
        catch (Exception e) { sb.AppendLine("  (не прочитались: " + e.Message + ")"); }
        return sb.ToString();
    }

    void EndTyping(bool save)
    {
        string id = typingId; typingId = null;
        CoopRunner.Instance?.World?.Player?.Unlock();
        if (!save || string.IsNullOrWhiteSpace(comment)) return;
        string who = Who(), text = comment.Trim();
        Append(id, $"Комментарий ({who}): {text}");
        WorldEvents.SendGlobal(Ev.ReportNote, w => { w.Write(id); w.Write(who); w.Write(text); });
    }

    void OnGUI()
    {
        if (typingId == null) return;
        box ??= new GUIStyle(GUI.skin.box);
        label ??= new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(14, Screen.height / 60), richText = true, wordWrap = true };
        var e = Event.current;
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) { EndTyping(true); e.Use(); return; }
        float wdt = Mathf.Min(700, Screen.width - 40), h = label.fontSize * 6;
        var area = new Rect((Screen.width - wdt) / 2, Screen.height * 0.35f, wdt, h);
        GUI.Box(area, "", box);
        GUILayout.BeginArea(new Rect(area.x + 12, area.y + 8, area.width - 24, area.height - 16));
        GUILayout.Label($"<b>Скриншот {typingId} сохранён у всех.</b> Что не так? Enter — сохранить, F9 — без комментария.", label);
        GUI.SetNextControlName("coop-report");
        comment = GUILayout.TextField(comment, 300, GUILayout.Height(label.fontSize * 1.8f));
        GUILayout.EndArea();
        if (!focusSet) { GUI.FocusControl("coop-report"); focusSet = true; }
    }
}
