using System;
using System.Globalization;
using System.IO;
using System.Text;
using CoopCore;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CscCoop;

// Test bot, active only when CSCCOOP_TEST is set. It plays instead of a human:
//  - from the main menu loads save slot 1 (CSCCOOP_SLOT), then in the hub starts job CSCCOOP_MISSION;
//  - CSCCOOP_TEST=probe logs scenes, the player and the job list;
//  - CSCCOOP_TEST=items runs the scripted two-player item test once both players share the job
//    and writes item tables to CSCCOOP_CSV (tools/item_report.py compares host and client);
//  - after CSCCOOP_QUIT_AFTER seconds in the shared job it ends the process.
public partial class Bot : MonoBehaviour
{
    public Bot(IntPtr p) : base(p) { }

    string test, scene = "";
    float sceneSince, readyAt = -1, nextLog;
    bool slotStarted, missionStarted, listed, couldMove;
    int step, sentStep = -1, transitions;
    public static int PartnerStep = -1;
    int myItem = -1, otherItem = -1;
    Vector3 walkFrom, walkDir;

    void Awake() { test = Env.Get("CSCCOOP_TEST"); Plugin.L.LogInfo($"BOT test={test}"); }

    void Update()
    {
        string s = SceneManager.GetActiveScene().name;
        if (s != scene) { scene = s; sceneSince = Time.unscaledTime; Plugin.L.LogInfo($"BOT scene {s}"); }
        float inScene = Time.unscaledTime - sceneSince;
        Watchdog.Where = $"bot step {step}";
        var w = CoopRunner.Instance?.World;

        if (test == "steam") { if (s.Contains("Menu") && inScene > 15) SteamTest(); return; }
        if (test == "slots") { SlotTest(s, inScene, w); return; }
        if (test == "menuprobe") { if (w != null) MenuProbe.Tick(w); return; }

        if (Env.Get("CSCCOOP_VIA_MENU") == "1") MenuFlow(s, inScene);
        else if (!slotStarted && s.Contains("Menu") && inScene > Env.Int("CSCCOOP_MENU_WAIT", 25) && SaveSystem.Instance != null)
        {
            slotStarted = true;
            int slot = Env.Int("CSCCOOP_SLOT", 1);
            Plugin.L.LogInfo($"BOT StartGame({slot})");
            SaveSystem.Instance.StartGame(slot);
        }

        // The test saves start in the intro hub, which drives straight into the first job: a manual test starts
        // its job from wherever the player first can move.
        bool startHere = s.Contains("Hub") || ((test == "manual" || test == "cctv" || Env.Get("CSCCOOP_TEST_SCENE") != "") && !s.Contains("Menu") && !s.Contains("Loading"));
        if (w?.Player != null && w.Player.CanMove && startHere && !missionStarted && inScene > 5)
        {
            if (!listed) { listed = true; ListMissions(); }
            int id = Env.Int("CSCCOOP_MISSION", -1);
            var mc = MissionController.Instance;
            if (id >= 0 && mc != null && GameManager.Instance != null)
            {
                missionStarted = true;
                var m = mc.GetMission(id);
                Plugin.L.LogInfo($"BOT StartMission({id}) -> {(m != null ? m.displayName : "null")}");
                if (m != null) GameManager.Instance.StartMission(m, 0);
            }
        }

        if (Time.unscaledTime >= nextLog && w?.Player != null)
        {
            nextLog = Time.unscaledTime + 15;
            Plugin.L.LogInfo($"BOT player at {w.Player.transform.position}, canMove {w.Player.CanMove}, items {w.ItemCount}, key {w.SceneKey:X8}");
        }

        // Manual two-window test (tools/run-manual.ps1 -Mission): only the save slot and the host's job are
        // started for the players; nothing else, and no quitting.
        if (test == "manual") return;

        bool canMove = w?.Player != null && w.Player.CanMove;
        if (canMove && !couldMove) Plugin.L.LogInfo($"BOT player can move, {inScene:F0} s after {s} loaded");
        couldMove = canMove;

        if (test == "census" && w != null) { Census(s, inScene, w); return; }
        if (test == "stainprobe" && w != null) { StainProbe.Tick(w); return; }
        if (test == "modelprobe" && w != null) { ModelProbe.TickReport(); ModelProbe.Tick(w); return; }
        if (test == "toolprobe" && w != null) { ToolProbe.Tick(w); return; }
        if (test == "shardprobe" && w != null) { ShardProbe(w); return; }
        if (test == "doorprobe" && w != null) { DoorProbe(s, inScene, w); return; }

        if (test == "jobs" && w != null) { JobTest(s, inScene, w); }
        if (test == "laptop" && w != null) { LaptopTest(s, inScene, w); return; }

        var ses = CoopRunner.Instance?.Session;
        // run-pair -Mission: the test runs in that job (CSCCOOP_TEST_SCENE), not in the first job on the way there.
        string only = Env.Get("CSCCOOP_TEST_SCENE");
        if (readyAt < 0 && ses != null && ses.Synced && w.SceneKey != 0 && (only == "" || s == only))
            foreach (var rp in ses.Players.Values)
                if (rp.SceneKey == w.SceneKey) { readyAt = Time.unscaledTime; Plugin.L.LogInfo("BOT ready: both players in the same job"); break; }
        if (readyAt < 0 && w?.SceneKey != 0 && inScene > 150)
        {
            Plugin.L.LogInfo("BOT never ready: the other player didn't share this job (different scene key?), quitting");
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }
        if (readyAt < 0) return;
        // Lockstep with the other bot: a step is left only once the partner has reached it too. Two copies on one
        // PC sometimes have one whole process frozen for 75-100 s (the watchdog thread stopped too, so not the
        // mod), and fixed timelines then dumped before the other side acted. While waiting, our clock stops.
        // Counted in step changes, not step numbers: world2 goes 0 -> 10 -> 11 -> 1, and "partner step >= mine"
        // let the client run ahead while the host was stuck at 11.
        if (step != sentStep) { sentStep = step; int n = ++transitions; WorldEvents.Send(Ev.BotStep, w => w.Write(n)); }
        if (test != "jobs" && PartnerStep < transitions) { readyAt += Time.unscaledDeltaTime; return; }
        float t = Time.unscaledTime - readyAt;
        if (test == "items") ItemTest(t, ses, w);
        if (test == "stains") StainTest(t, ses, w);
        if (test == "bodies") BodyTest(t, ses, w);
        if (test == "doors") DoorTest(t, ses, w);
        if (test == "trash") TrashTest(t, ses, w);
        if (test == "lift") LiftTest(t, ses);
        if (test == "cctv") CctvTest(t, ses);
        if (test == "follow") FollowTest(t, ses, s);
        if (test == "figure") FigureTest(t, ses, w);
        if (test == "breakhit") BreakHitTest(t, ses, w);
        if (test == "bump") BumpTest(t, ses, w);
        if (test == "toolcarry") ToolCarryTest(t, ses, w);
        if (test == "footsteps") FootstepsTest(t, ses, w);
        if (test == "doorsync") DoorSyncTest(t, ses, w);
        if (test == "products") ProductsTest(t, ses, w);
        if (test == "lamps") LampsTest(t, ses, w);
        if (test == "world2") { try { World2Test(t, ses, w); } catch (Exception e) { Plugin.L.LogError($"WORLD2 bot step {step} failed: {e}"); } }
        if (test != "jobs" && t > Env.Int("CSCCOOP_QUIT_AFTER", 40))
        {
            Plugin.L.LogInfo("BOT done, quitting");
            BepInEx.Logging.Logger.Listeners.Clear();   // flush by dropping writers below
            System.Diagnostics.Process.GetCurrentProcess().Kill();   // Application.Quit crashed in another IL2CPP game
        }
    }

    // Stains: the first four active stains in the table play fixed roles.
    //  A: client paints 30 recognisable strokes on it and lowers its intensity to 0.4
    //  B: host lowers it to 0.6; later the client tries to raise it to 0.9 (must not spread)
    //  C: host destroys it.  D: client destroys it.
    int[] roles;
    bool newCleaned;
    void StainTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        if (roles == null)
        {
            var l = new System.Collections.Generic.List<int>();
            for (int i = 0; i < w.Taints.Count && l.Count < 4; i++) if (w.Taints[i] != null && w.Taints[i].gameObject.activeInHierarchy) l.Add(i);
            roles = l.ToArray();
            Plugin.L.LogInfo($"STAINTEST roles A {roles[0]} B {roles[1]} C {roles[2]} D {roles[3]}");
        }
        switch (step)
        {
            case 0 when t > 4: DumpStains(w, "start"); step = 1; break;
            case 1 when t > 5:
                if (!host)
                {
                    var cd = w.Taints[roles[0]].GetComponent<CleanableDecal>();
                    for (int k = 0; k < 30; k++) cd.PaintTexture(0.77f, 0.2f, new Vector2(0.1f + k * 0.025f, 0.5f), 0.33f, 1);
                    w.Taints[roles[0]].SetIntensity(0.4f);
                    w.Taints[roles[3]].DestroyDecal();
                }
                else { w.Taints[roles[1]].SetIntensity(0.6f); w.Taints[roles[2]].DestroyDecal(); }
                Plugin.L.LogInfo($"STAINTEST acted at {Time.unscaledTime:F1}");
                step = 2; break;
            case 2 when t > 11:   // the two bots get ready up to a few seconds apart
                DumpStains(w, "mid");
                // New stain: the client bleeds on the floor under itself; later the host cleans it.
                if (!host && Physics.Raycast(w.Player.transform.position + Vector3.up, Vector3.down, out var hit, 5f))
                {
                    var go = BloodSpreaderController.Instance.ProjectBlood(BloodSplatterType.directBig, hit.collider.gameObject, hit.point, hit.normal, Vector3.forward, false);
                    Plugin.L.LogInfo($"STAINTEST client made a stain on '{hit.collider.name}': {(go != null ? go.transform.position.ToString("F3") : "null")}");
                }
                if (!host) w.Taints[roles[1]].SetIntensity(0.9f);
                step = 3; break;
            case 3 when t > 15:   // its own step: the client's final dump waits until the host has cleaned
                if (host)
                    foreach (var kv in Stains.NewStains)
                    {
                        var cd = kv.Value.GetComponent<CleanableDecal>();
                        for (int k = 0; k < 20; k++) cd.PaintTexture(0.55f, 0.2f, new Vector2(0.2f + k * 0.03f, 0.4f), 0.44f, 1);
                        kv.Value.SetIntensity(0.3f);
                        Plugin.L.LogInfo($"STAINTEST host cleaned new stain {kv.Key:X} at {kv.Value.transform.position.ToString("F3")}");
                        break;
                    }
                step = 30; break;
            case 30 when t > 21: DumpStains(w, "final"); Plugin.L.LogInfo($"STAINTEST hits sent {Stains.HitsSent} applied {Stains.HitsApplied}"); step = 4; break;
        }
    }

    // Bodies and the van. Client: walks to body 0, grabs it the way the game does, backs away 1.5 m,
    // lets go. Host: tries to grab the same body while the client holds it (must be refused).
    // Then the host collects van item G0 and the client van item G1 (as the van's trigger would).
    int g0 = -1, g1 = -1;
    void BodyTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        var body = w.Bodies.Count > 0 ? w.Bodies[0] : null;
        if (body == null) { if (step == 0) Plugin.L.LogInfo("BODYTEST no bodies in this job"); step = 99; return; }
        if (g0 < 0)
        {
            // Van items other than body 0 (in Dealer's Apartment all 5 van items are bodies).
            for (int i = 0; i < w.Garbage.Count; i++)
                if (w.Garbage[i] != null && (w.Garbage[i].bodyController == null || w.Garbage[i].bodyController.Pointer != body.Pointer)) { if (g0 < 0) g0 = i; else if (g1 < 0) g1 = i; }
            if (g1 < 0) g1 = g0;
            Plugin.L.LogInfo($"BODYTEST body 0 = {body.name}, {body.RbToBones?.Count} bones; van items G0 {g0} G1 {g1} of {w.Garbage.Count}");
        }
        var part = body.GetComponentInChildren<BodyPartController>();
        switch (step)
        {
            case 0 when t > 4:
                DumpBodies(w, ses, "start");
                if (!host) { var root = body.RbToBones[0].bone.transform.position; ApproachPoint(w, root); }
                // The client opens the attic (the story object that adds "get rid of the victims' bodies"), so the
                // van body it collects later counts on that objective, on both (owner's test: 2/2 on the host).
                if (!host)
                {
                    var panels = new System.Collections.Generic.List<(string, InteractablePanel)>();
                    foreach (var ip in UnityEngine.Object.FindObjectsOfType<InteractablePanel>()) if (ip != null) panels.Add((GameWorld.IndexedPathOf(ip.transform), ip));
                    panels.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
                    if (panels.Count > 1) { panels[1].Item2.Interact(); Plugin.L.LogInfo($"BODYTEST client opened '{panels[1].Item2.name}' (story object 1 of {panels.Count})"); }
                }
                step = 1; break;
            case 1 when t > 5:
                if (!host)
                {
                    w.Grabber.BodyGrabber.GrabBody(part, w.Grabber.animator);
                    Plugin.L.LogInfo($"BODYTEST client grab body: {(w.Grabber.IsGrabbingBody ? "holding" : "NOT holding")}, {Vector3.Distance(w.Player.transform.position, body.RbToBones[0].bone.transform.position):F2} m away");
                }
                walkFrom = w.Player.transform.position; walkDir = -w.Player.transform.forward;
                step = 2; break;
            case 2 when t > 7.5f:
                DumpBodies(w, ses, "carry");
                if (host)
                {
                    Plugin.L.LogInfo($"BODYTEST host sees body 0 on the client's shoulder: {Bodies.IsShouldered(0)} gap {Bodies.LastShoulderGap.ToString("F2", CultureInfo.InvariantCulture)} m, bones ignored {Bodies.BonesIgnored}");
                    w.Grabber.BodyGrabber.GrabBody(part, w.Grabber.animator);
                    bool refused = !w.Grabber.IsGrabbingBody;
                    Plugin.L.LogInfo($"BODYTEST host steal of body 0: {(refused ? "refused" : "ALLOWED")}");
                    if (!refused) w.Grabber.TryReleaseBody();
                }
                step = 3; break;
            case 3 when t > 9:
                // A throw, the way the player's button does it (owner's test: bodies the client threw hung in the air).
                // body.IsGrabbed: IsGrabbingBody is still false right after GrabBody (the grab tweens in).
                if (!host && body.IsGrabbed)
                {
                    float force = Env.Int("CSCCOOP_THROW", 8);
                    var rootT = body.RbToBones[0].bone.transform;
                    Plugin.L.LogInfo($"BODYTEST client throws the body (force {force}) from y {rootT.position.y:F2}");
                    w.Grabber.TryReleaseBody(force);
                    throwWatch = 30;
                }
                if (host) throwWatch = 30;
                step = 4; break;
            case 4 when t > 12:
                int mine = host ? g0 : g1;
                if (mine >= 0) { w.Garbage[mine].CollectAndDestroy(); Plugin.L.LogInfo($"BODYTEST {(host ? "host" : "client")} collected van item {mine}"); }
                step = 5; break;
            case 5 when t > 24:
                // The game's second CollectAndDestroy after a replayed van collect (friend's test of 0.4.1: one body
                // counted 2, both ways): the host gets it for the client's body.
                if (host && g1 >= 0 && w.Garbage[g1] != null) { w.Garbage[g1].CollectAndDestroy(); Plugin.L.LogInfo($"BODYTEST host: the game's second collect of van item {g1}"); }
                // The bodies objectives' counts (BodyQuest keeps them in Lua as "<quest>.BodiesMin"): one body must
                // count once on both (owner's test: the client's one body counted 2/2 on the host).
                foreach (var st in new[] { PixelCrushers.DialogueSystem.QuestState.Active, PixelCrushers.DialogueSystem.QuestState.Success })
                    foreach (var q in PixelCrushers.DialogueSystem.QuestLog.GetAllQuests(st) ?? new string[0])
                    {
                        var v = PixelCrushers.DialogueSystem.DialogueLua.GetVariable(q + ".BodiesMin");
                        if (v.hasReturnValue && v.isNumber) Plugin.L.LogInfo($"BODYTEST objective '{q}' bodies {v.asFloat:F0}");
                    }
                DumpBodies(w, ses, "final"); Plugin.L.LogInfo($"BODYTEST bones sent {Bodies.BonesSent} applied {Bodies.BonesApplied}, van collected remotely {Bodies.Collected}"); step = 6; break;
        }
        if (!host && step >= 2 && step <= 3 && t < 8) Walk(w, (t - 5) / 3f * 1.5f);
        // After the throw: the body's height every 0.2 s for 6 s on both sides (the owner's test: it hung 1-2 s in).
        if (step >= 4 && throwWatch > 0 && Time.unscaledTime >= nextWatch && body.RbToBones?.Count > 0)
        {
            nextWatch = Time.unscaledTime + 0.2f; throwWatch--;
            var rb0 = body.RbToBones[0].rb;
            int kin = 0; float minY = 99;
            foreach (var bn in body.RbToBones) { if (bn.rb != null && bn.rb.isKinematic) kin++; if (bn.bone != null) minY = Mathf.Min(minY, bn.bone.transform.position.y); }
            // What is under it: the first collider below the root that isn't the body itself.
            // What it lies on: the nearest surface under any of its bones (a body on a shelf may hang its hips over
            // the edge, so the root alone sees the floor far below).
            string under = "nothing";
            float best = 99;
            foreach (var bn in body.RbToBones)
            {
                if (bn.bone == null) continue;
                foreach (var h in Physics.RaycastAll(bn.bone.transform.position, Vector3.down, 5f))
                    if (h.collider != null && h.collider.GetComponentInParent<BodyRagdollController>() == null && h.distance < best)
                    { best = h.distance; under = $"'{h.collider.name}' {h.distance:F2} m below (y {h.point.y:F2})"; }
            }
            Plugin.L.LogInfo($"BODYTEST watch y {body.RbToBones[0].bone.transform.position.y:F2} kinematic {rb0 != null && rb0.isKinematic} owner {ses.OwnerOf(w.Items.Count)} animator {(body.animator != null && body.animator.enabled)} kinematic bones {kin} lowest bone y {minY:F2}, under: {under}");
        }
    }

    int throwWatch; float nextWatch;

    void ApproachPoint(GameWorld w, Vector3 target)
    {
        var me = w.Player.transform.position;
        var flat = new Vector3(target.x - me.x, 0, target.z - me.z);
        if (flat.magnitude > 1.0f)
        {
            var p = new Vector3(target.x, me.y, target.z) - flat.normalized * 1.0f;
            if (w.Player.rb != null) w.Player.rb.position = p;
            w.Player.transform.position = p;
        }
        w.Player.transform.rotation = Quaternion.LookRotation(flat.normalized);
    }

    // Rows: label,kind,index,x,y,z,a,b. kind "root"/"bone": world position (a = owner, b = held);
    // kind "van": a = collected, b = active.
    static void DumpBodies(GameWorld w, Session ses, string label)
    {
        string path = Env.Get("CSCCOOP_CSV");
        if (path == "") return;
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        for (int b = 0; b < w.Bodies.Count; b++)
        {
            var body = w.Bodies[b];
            if (body?.RbToBones == null) continue;
            int e = w.Items.Count + b;
            for (int k = 0; k < body.RbToBones.Count; k++)
            {
                var bone = body.RbToBones[k].bone;
                if (bone == null) continue;
                var p = bone.transform.position;
                sb.Append(string.Format(ci, "{0},{1},{2},{3:F3},{4:F3},{5:F3},{6},{7}\n", label, k == 0 ? "root" : "bone", b * 100 + k, p.x, p.y, p.z, ses.OwnerOf(e), body.IsGrabbed ? 1 : 0));
            }
        }
        for (int i = 0; i < w.Garbage.Count; i++)
        {
            var g = w.Garbage[i];
            if (g == null) continue;
            sb.Append(string.Format(ci, "{0},van,{1},0,0,0,{2},{3}\n", label, i, g.isCollected ? 1 : 0, g.gameObject.activeInHierarchy ? 1 : 0));
        }
        File.AppendAllText(path, sb.ToString());
        Plugin.L.LogInfo($"BODYTEST dump {label} at {Time.unscaledTime:F1}");
    }

    // Doors, keys, evidence. S0 = first locked door: the host unlocks and opens it. S1 = another door:
    // the client opens it, later closes it. The client picks up key 0 and evidence 0.
    int s0 = -1, s1 = -1;
    string questName;
    void DoorTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        if (s0 < 0)
        {
            for (int i = 0; i < Doors.Shortcuts.Count; i++)
            {
                var d = Doors.Shortcuts[i];
                if (d == null) continue;
                if (s0 < 0 && d.IsLocked) s0 = i; else if (s1 < 0) s1 = i;
            }
            Plugin.L.LogInfo($"DOORTEST S0 {s0} S1 {s1} of {Doors.Shortcuts.Count}, keys {Doors.Keys.Count}, evidence {Doors.Evidence.Count}");
        }
        switch (step)
        {
            case 0 when t > 4: DumpDoors(w, "start"); step = 1; break;
            case 1 when t > 5:
                if (host)
                {
                    Doors.Shortcuts[s0].UnlockDoors(); Doors.Shortcuts[s0].OpenDoors();
                    var active = PixelCrushers.DialogueSystem.QuestLog.GetAllQuests(PixelCrushers.DialogueSystem.QuestState.Active);
                    if (active != null && active.Length > 0)
                    {
                        questName = active[0];
                        PixelCrushers.DialogueSystem.QuestLog.SetQuestEntryState(questName, 1, PixelCrushers.DialogueSystem.QuestState.Success);
                        Plugin.L.LogInfo($"QUESTTEST host set '{questName}' entry 1 to success ({active.Length} active quests)");
                    }
                    if (Collectibles.BreakableObjects.Count > 0) Collectibles.BreakableObjects[0].Destroy();
                    for (int i = 0; i < Collectibles.Breakables.Count; i++)
                        if (Collectibles.Breakables[i] != null && Collectibles.Breakables[i].gameObject.activeInHierarchy) { Collectibles.Breakables[i].CrackItem(Vector3.down * 3); Plugin.L.LogInfo($"DOORTEST host cracked breakable {i}"); break; }
                }
                else
                {
                    Doors.Shortcuts[s1].OpenDoors();
                    if (Doors.Keys.Count > 0) { KeyHolder.Instance.CollectKey(Doors.Keys[0]); Doors.Keys[0].CollectKey(); }   // the game's pickup path
                    if (Doors.Evidence.Count > 0) Doors.Evidence[0].Collect();
                    if (Collectibles.Secrets.Count > 0) Collectibles.Secrets[0].UnlockSecret();
                    if (Collectibles.Valuables.Count > 0) MissionStealingController.Instance.AddStolenItemFunds(Collectibles.Valuables[0]);
                    if (Collectibles.Cassettes.Count > 0) Collectibles.Cassettes[0].UnlockMusic();
                }
                Plugin.L.LogInfo($"DOORTEST {(host ? "host" : "client")} acted");
                step = 2; break;
            // The close is a step of its own: in the same step the client closed right after its own dump, and the
            // host (lockstep waits only for the partner to reach a step) dumped "mid" after the close arrived.
            case 2 when t > 12: DumpDoors(w, "mid"); step = 3; break;
            case 3 when t > 13: if (!host) Doors.Shortcuts[s1].CloseDoors(); step = 4; break;
            case 4 when t > 20:
                var anyQ = PixelCrushers.DialogueSystem.QuestLog.GetAllQuests(PixelCrushers.DialogueSystem.QuestState.Active);
                string q = questName ?? (anyQ != null && anyQ.Length > 0 ? anyQ[0] : null);
                if (q != null) Plugin.L.LogInfo($"QUESTTEST '{q}' entry 1 is {PixelCrushers.DialogueSystem.QuestLog.GetQuestEntryState(q, 1)}");
                DumpDoors(w, "final"); Plugin.L.LogInfo($"DOORTEST applied {Doors.Applied}"); step = 5; break;
        }
    }

    // Rows: label,kind,index,a,b,c,d. door: locked, x/y/z sum of its moving parts' world position and euler.
    // key: in my KeyHolder, key object active. evidence: collected, active.
    static void DumpDoors(GameWorld w, string label)
    {
        string path = Env.Get("CSCCOOP_CSV");
        if (path == "") return;
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < Doors.Shortcuts.Count; i++)
        {
            var d = Doors.Shortcuts[i];
            if (d == null) continue;
            float px = 0, py = 0, ex = 0;
            if (d.animationsList != null)
                foreach (var a in d.animationsList)
                {
                    if (a?.gameObject == null) continue;
                    var tr = a.gameObject.transform;
                    px += tr.position.x + tr.position.z; py += tr.position.y;
                    var e = tr.rotation; ex += Mathf.Abs(e.x) + Mathf.Abs(e.y) + Mathf.Abs(e.z);
                }
            sb.Append(string.Format(ci, "{0},door,{1},{2},{3:F3},{4:F3},{5:F3}{6}", label, i, d.IsLocked ? 1 : 0, px, py, ex, "\n"));
        }
        var held = KeyHolder.Instance?.keys;
        for (int i = 0; i < Doors.Keys.Count; i++)
        {
            var k = Doors.Keys[i];
            if (k == null) continue;
            sb.Append(string.Format(ci, "{0},key,{1},{2},{3},0,0{4}", label, i, held != null && held.Contains(k) ? 1 : 0, k.gameObject.activeInHierarchy ? 1 : 0, "\n"));
        }
        for (int i = 0; i < Doors.Evidence.Count; i++)
        {
            var e = Doors.Evidence[i];
            if (e == null) continue;
            sb.Append(string.Format(ci, "{0},evidence,{1},{2},{3},0,0{4}", label, i, e.isCollected ? 1 : 0, e.gameObject.activeInHierarchy ? 1 : 0, "\n"));
        }
        var stolen = MissionStealingController.Instance?.stolenItems;
        for (int i = 0; i < Collectibles.Valuables.Count; i++)
        {
            var v = Collectibles.Valuables[i];
            if (v != null) sb.Append(string.Format(ci, "{0},valuable,{1},{2},{3},0,0{4}", label, i, v.wasStolen ? 1 : 0, stolen != null && stolen.Contains(v) ? 1 : 0, "\n"));
        }
        for (int i = 0; i < Collectibles.Secrets.Count; i++)
        {
            var x = Collectibles.Secrets[i];
            if (x != null) sb.Append(string.Format(ci, "{0},secret,{1},{2},0,0,0{3}", label, i, x.isDiscovered ? 1 : 0, "\n"));
        }
        for (int i = 0; i < Collectibles.Breakables.Count; i++)
        {
            var x = Collectibles.Breakables[i];
            if (x != null) sb.Append(string.Format(ci, "{0},crack,{1},{2},{3},0,0{4}", label, i, x.WasCracked ? 1 : 0, x.gameObject.activeInHierarchy ? 1 : 0, "\n"));
        }
        for (int i = 0; i < Collectibles.BreakableObjects.Count; i++)
        {
            var x = Collectibles.BreakableObjects[i];
            if (x != null) sb.Append(string.Format(ci, "{0},broken,{1},{2},0,0,0{3}", label, i, x.wasDestroyed ? 1 : 0, "\n"));
        }
        File.AppendAllText(path, sb.ToString());
        Plugin.L.LogInfo($"DOORTEST dump {label} at {Time.unscaledTime:F1}, cassettes {Collectibles.Cassettes.Count}, collectibles applied {Collectibles.Applied}");
    }

    // Jobs: both start in the job (slot 1). The host leaves (the client must leave too), both close the report,
    // land in the hub; the host starts the same job again and the client must follow by itself.
    int jstep, jobId = -1, readyCount;

    static bool PartnerHere(Session ses, GameWorld w)
    {
        foreach (var rp in ses.Players.Values) if (rp.SceneKey == w.SceneKey && w.SceneKey != 0) return true;
        return false;
    }

    // A job this save knows that the client's save can't start (-1: none, or nothing reported yet).
    static int Lacking(System.Collections.Generic.HashSet<int> theirs)
    {
        if (theirs == null) return -1;
        for (int id = 1; id < 60; id++) if (MissionController.Instance.GetMission(id) != null && !theirs.Contains(id)) return id;
        return -1;
    }

    // The truck's job button, the way the map panel calls it.
    static void TryTruck(int id)
    {
        var m = MissionController.Instance?.GetMission(id);
        var maps = Resources.FindObjectsOfTypeAll<MissionsMapController>();
        if (m == null || maps.Length == 0) { Plugin.L.LogInfo($"JOBTEST no truck map or job {id} ({maps.Length} maps)"); return; }
        maps[0].StartMission(m);
        Plugin.L.LogInfo($"JOBTEST truck call for job {id} returned");
    }
    float reportSince = -1, jAt;
    void JobTest(string scene, float inScene, GameWorld w)
    {
        var ses = CoopRunner.Instance?.Session;
        if (ses == null) return;
        bool host = ses.IsHost;
        bool shared = false;
        if (ses.Synced && w.SceneKey != 0)
            foreach (var rp in ses.Players.Values) if (rp.SceneKey == w.SceneKey) shared = true;
        var report = UnityEngine.Object.FindObjectOfType<MissionReportFinalView>();
        if (report != null && report.isActiveAndEnabled) { if (reportSince < 0) { reportSince = Time.unscaledTime; Plugin.L.LogInfo("JOBTEST report screen open"); } }
        else reportSince = -1;

        switch (jstep)
        {
            case 0 when shared && !scene.Contains("Hub"):
                readyCount++; jobId = GameManager.Instance.ActiveMission != null ? GameManager.Instance.ActiveMission.id : -1;
                Plugin.L.LogInfo($"JOBTEST in job {jobId} together (#{readyCount})");
                jstep = 1; jAt = Time.unscaledTime + 6; break;
            case 1 when Time.unscaledTime > jAt:
                if (host) { Plugin.L.LogInfo("JOBTEST host leaves"); UnityEngine.Object.FindObjectOfType<MissionLeaveController>().LeaveMissionWithReport(); }
                jstep = 2; break;
            case 2 when reportSince > 0 && Time.unscaledTime - reportSince > 10:
                Plugin.L.LogInfo("JOBTEST closing the report"); report.OnCloseReport(); jstep = 3; break;
            case 3 when scene.Contains("Hub") && w.Player != null && w.Player.CanMove:
                Plugin.L.LogInfo($"JOBTEST in the hub ({scene})"); jstep = 30; jAt = Time.unscaledTime + 60; break;
            // In the hub: each in their own apartment, seeing the other (poses only), ping measured.
            case 30 when PartnerHere(ses, w) && CoopRunner.Instance.Ghost != null || Time.unscaledTime > jAt:
                jAt = Time.unscaledTime + 3; jstep = 31; break;
            case 31 when Time.unscaledTime > jAt:
            {
                var g = CoopRunner.Instance.Ghost;
                bool vis = PartnerHere(ses, w) && g != null && g.Root != null && g.Root.activeInHierarchy;
                float d = vis ? Vector3.Distance(g.Root.transform.position, w.Player.transform.position) : -1;
                Plugin.L.LogInfo($"JOBTEST hub: partner visible {vis}, {d:F1} m away");
                float ms = host ? (ses.Players.TryGetValue(1, out var c1) ? c1.RttMs : 0) : ses.RttMs;
                Plugin.L.LogInfo($"JOBTEST ping {ms:F0} ms, corner '{CoopRunner.Instance.ConnectionText()}{CoopRunner.Instance.PingText()}'");
                if (!host) { Plugin.L.LogInfo("JOBTEST client tries the truck with job 0"); TryTruck(0); }
                jstep = 32; jAt = Time.unscaledTime + 10; break;
            }
            case 32 when Time.unscaledTime > jAt:
                if (host)
                {
                    var theirs = Jobs.ClientJobsOf(1);
                    int lacking = Lacking(theirs);
                    Plugin.L.LogInfo($"JOBTEST host tries the truck with job {lacking} (the client lacks it)");
                    if (lacking >= 0) TryTruck(lacking);
                }
                else Plugin.L.LogInfo($"JOBTEST client own jobs [{string.Join(",", Jobs.OwnJobs())}]");
                jstep = 4; jAt = Time.unscaledTime + 6; break;
            case 4 when host && Time.unscaledTime > jAt:
                var m = MissionController.Instance.GetMission(jobId);
                Plugin.L.LogInfo($"JOBTEST host starts job {jobId} again"); GameManager.Instance.StartMission(m, 0); jstep = 5; break;
            case 4 when !host && !scene.Contains("Hub"):   // the client went into a job by itself: following
                Plugin.L.LogInfo($"JOBTEST client followed into {scene}"); jstep = 5; break;
            case 5 when !scene.Contains("Hub") && shared:
                Plugin.L.LogInfo($"JOBTEST in job {GameManager.Instance.ActiveMission?.id} together again (#2), scene {scene}");
                jstep = 6; jAt = Time.unscaledTime + 5; break;
            case 6 when Time.unscaledTime > jAt:
                Plugin.L.LogInfo("BOT done, quitting");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                break;
        }
        if (!host && jstep == 2 && scene.Contains("Hub")) jstep = 3;   // client may skip the report if it never shows
    }

    // Trash, bags, tools. Client: the can picks up trash item T (Collect). Host: a bag appears in front of it.
    // Client: grabs the host's bag (takes it over), carries it, lets go, then the bag goes into the van.
    int trashT = -1;
    GameObject hostBag;
    void TrashTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        if (trashT < 0)
        {
            for (int i = 0; i < Trash.Items.Count; i++) if (Trash.Items[i] != null && Trash.Items[i].gameObject.activeInHierarchy && !Trash.Items[i].isCollected) { trashT = i; break; }
            Plugin.L.LogInfo($"TRASHTEST trash T {trashT} of {Trash.Items.Count}");
        }
        switch (step)
        {
            case 0 when t > 4:
                if (!host && trashT >= 0) { Trash.Items[trashT].Collect(); Plugin.L.LogInfo($"TRASHTEST client collected trash {trashT}"); }
                if (host)
                {
                    GameObject prefab = null;
                    foreach (var gc in Resources.FindObjectsOfTypeAll<GarbageCollector>()) if (gc.garbagePrefab != null) { prefab = gc.garbagePrefab; break; }
                    var p = w.Player.transform.position + w.Player.transform.forward * 1.5f + Vector3.up * 1f;
                    hostBag = prefab != null ? UnityEngine.Object.Instantiate(prefab, p, Quaternion.identity) : null;
                    Plugin.L.LogInfo($"TRASHTEST host made a bag at {p}: {(hostBag != null ? hostBag.name : "NO PREFAB")}");
                    // Put the host's own bucket down next to it, the way a placed tool is: active, in the world.
                    // A bucket if there is one, with water and dirt in it: the client's copy must show the same.
                    PortableItem placed = null;
                    foreach (var pi in Resources.FindObjectsOfTypeAll<PortableItem>())
                        if (pi != null && pi.gameObject.scene.name == UnityEngine.SceneManagement.SceneManager.GetActiveScene().name && (placed == null || pi.GetComponentInChildren<LiquidContainer>(true) != null))
                            placed = pi;
                    foreach (var pi in new[] { placed })
                        if (pi != null)
                        {
                            var lc = pi.GetComponentInChildren<LiquidContainer>(true);
                            if (lc != null) { lc.SetWaterLevel(0.7f); lc.SetTaintLevel(0.4f); }
                            pi.transform.SetParent(null);
                            pi.transform.position = p + w.Player.transform.right * 1.0f;
                            pi.gameObject.SetActive(true);
                            Plugin.L.LogInfo($"TRASHTEST host placed its tool '{pi.name}' at {pi.transform.position}");
                            break;
                        }
                }
                step = 1; break;
            case 1 when t > 9:
                LogDyn(w, "early");
                if (!host)
                {
                    GrabableItemController bag = null;
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GrabableItemController>()) if (g.name.Contains("(other player)") && g.GetComponent<GarbageBagConnector>() != null) { bag = g; break; }
                    if (bag != null) { ApproachPoint(w, bag.transform.position); }
                    Plugin.L.LogInfo($"TRASHTEST client sees the host's bag: {(bag != null ? bag.transform.position.ToString() : "NO")}");
                }
                step = 2; break;
            case 2 when t > 10:
                if (!host)
                {
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GrabableItemController>())
                        if (g.name.Contains("(other player)") && g.GetComponent<GarbageBagConnector>() != null) { w.Grabber.GrabItem(g); break; }
                    Plugin.L.LogInfo($"TRASHTEST client grab of the bag: {(w.Grabber.GrabbedRb != null ? "holding " + w.Grabber.GrabbedRb.name : "NOT holding")}");
                }
                walkFrom = w.Player.transform.position; walkDir = -w.Player.transform.forward;
                step = 3; break;
            case 3 when t > 13.5f:
                LogDyn(w, "carry");
                step = 4; break;
            case 4 when t > 14:
                if (!host && w.Grabber.GrabbedRb != null) w.Grabber.TryRelease();
                step = 5; break;
            case 5 when t > 19:
                LogDyn(w, "rest");
                step = 50; break;
            case 50 when t > 21:   // a step of its own: the host's rest dump must not race the van
                if (!host)
                {
                    foreach (var gi in UnityEngine.Object.FindObjectsOfType<GarbageItem>())
                        if (gi.GetComponent<GarbageBagConnector>() != null && gi.name.Contains("(other player)")) { gi.CollectAndDestroy(); Plugin.L.LogInfo("TRASHTEST client put the bag in the van"); break; }
                }
                step = 6; break;
            case 6 when t > 25:
                LogDyn(w, "final");
                var tt = trashT >= 0 ? Trash.Items[trashT] : null;
                Plugin.L.LogInfo($"TRASHTEST final trash {trashT}: collected {tt != null && tt.isCollected}, active {tt != null && tt.gameObject.activeInHierarchy}; host bag {(hostBag == null ? "gone" : hostBag.activeInHierarchy ? "active" : "inactive")}" +
                                 $" collected {(hostBag != null && hostBag.GetComponent<GarbageItem>() != null && hostBag.GetComponent<GarbageItem>().isCollected)}; dyn spawned {Dyn.Spawned} puppets {Dyn.Puppets} taken {Dyn.Taken}");
                step = 7; break;
        }
        if (!host && step >= 3 && step <= 4 && t < 13.5f) Walk(w, (t - 10) / 3f * 1.5f);
    }

    static void LogDyn(GameWorld w, string label)
    {
        foreach (var g in UnityEngine.Object.FindObjectsOfType<GarbageBagConnector>())
            Plugin.L.LogInfo($"TRASHTEST {label} bag '{g.name}' at {g.transform.position.x:F2},{g.transform.position.y:F2},{g.transform.position.z:F2} active {g.gameObject.activeInHierarchy}");
        foreach (var p in UnityEngine.Object.FindObjectsOfType<PortableItem>())
        {
            var lc = p.GetComponentInChildren<LiquidContainer>(true);
            Plugin.L.LogInfo($"TRASHTEST {label} tool '{p.name}' at {p.transform.position.x:F2},{p.transform.position.y:F2},{p.transform.position.z:F2}{(lc != null ? $" water {lc.WaterLevel:F2} dirt {lc.TaintLevel:F2}" : "")}");
        }
    }

    // Things the owner's manual test found unshared. Client: opens drawer O, breaks board part P, steals
    // valuable V the real way (OnSteal), hits item J with the mop (OnToolPunch). Host: moves lift L, fixes
    // furniture piece F on hologram H, cracks breakable B, then moves its shard 0 and collects its shard 1.
    int wO = -1, wP = -1, wV = -1, wJ = -1, wL = -1, wH = -1, wF = -1, wB = -1, wO2 = -1, wD = -1, wP2 = -1;
    FixableObject wX; string wXBefore;   // knocked-over furniture the client stands up (OnFix)
    InteractablePanel wS; string wQ;     // a story object the client uses; an active quest whose counter it raises
    Interactable wI;
    string doorBefore = "";
    static string DoorPose(ShortcutDoors d)
    {
        float r = 0;
        if (d?.animationsList != null) foreach (var a in d.animationsList) if (a?.gameObject != null) { var q = a.gameObject.transform.rotation; r += Mathf.Abs(q.x) + Mathf.Abs(q.y) + Mathf.Abs(q.z); }
        return r.ToString("F3");
    }
    void World2Test(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        if (wO < 0)
        {
            for (int i = 0; i < Furniture.Openables.Count && wO < 0; i++) if (Furniture.Openables[i] != null && !Furniture.Openables[i].isOpen) wO = i;
            for (int i = 0; i < Furniture.Parts.Count && wP < 0; i++) if (Furniture.Parts[i] != null && !Furniture.Parts[i].IsCracked) wP = i;
            for (int i = 1; i < Collectibles.Valuables.Count && wV < 0; i++) if (Collectibles.Valuables[i] != null && !Collectibles.Valuables[i].wasStolen) wV = i;
            for (int i = 0; i < w.Items.Count && wJ < 0; i++) if (w.Items[i] != null && w.Items[i].gameObject.activeInHierarchy && w.Items[i].Rb != null) wJ = i;
            wL = Furniture.Lifts.Count > 0 ? 0 : -1;
            for (int h = 0; h < Furniture.Holograms.Count && wH < 0; h++)
            {
                var holo = Furniture.Holograms[h];
                if (holo == null || holo.IsCorrect) continue;
                for (int f = 0; f < Furniture.Pieces.Count; f++)
                    if (Furniture.Pieces[f] != null && Furniture.Pieces[f].FurnitureMesh == holo.FurnitureMesh && !Furniture.Pieces[f].IsFixed) { wH = h; wF = f; break; }
            }
            for (int i = 0; i < Collectibles.Breakables.Count && wB < 0; i++) if (Collectibles.Breakables[i] != null && Collectibles.Breakables[i].gameObject.activeInHierarchy) wB = i;
            for (int i = 0; i < Furniture.Openables.Count && wO2 < 0; i++) if (i != wO && Furniture.Openables[i] != null && !Furniture.Openables[i].isOpen) wO2 = i;
            for (int i = 0; i < Doors.Shortcuts.Count && wD < 0; i++) if (Doors.Shortcuts[i] != null && !Doors.Shortcuts[i].IsLocked) wD = i;
            for (int i = 0; i < Furniture.Parts.Count && wP2 < 0; i++) if (i != wP && Furniture.Parts[i] != null && !Furniture.Parts[i].IsCracked && Furniture.Parts[i].makeCollectable) wP2 = i;
            foreach (var it in UnityEngine.Object.FindObjectsOfType<Interactable>()) if (it != null && it.GetIl2CppType().Name == "Interactable") { wI = it; break; }
            var fixables = new System.Collections.Generic.List<(string, FixableObject)>();
            foreach (var fo in UnityEngine.Object.FindObjectsOfType<FixableObject>())
                if (fo != null && fo.GetIl2CppType().Name == "FixableObject" && fo.isFixable) fixables.Add((GameWorld.IndexedPathOf(fo.transform), fo));
            fixables.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            if (fixables.Count > 0) { wX = fixables[0].Item2; wXBefore = FixPose(wX); }
            Plugin.L.LogInfo($"WORLD2 fixables {fixables.Count}, X '{wX?.name}'");
            var panels = new System.Collections.Generic.List<(string, InteractablePanel)>();
            foreach (var ip in UnityEngine.Object.FindObjectsOfType<InteractablePanel>())
                if (ip != null && !ip.wasUsed) panels.Add((GameWorld.IndexedPathOf(ip.transform), ip));
            panels.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            if (panels.Count > 0) wS = panels[0].Item2;
            var aq = PixelCrushers.DialogueSystem.QuestLog.GetAllQuests(PixelCrushers.DialogueSystem.QuestState.Active);
            if (aq != null && aq.Length > 0) wQ = aq[aq.Length - 1];
            Plugin.L.LogInfo($"WORLD2 story panels {panels.Count}, S '{wS?.name}', quest '{wQ}'");
            Plugin.L.LogInfo($"WORLD2 roles O {wO} P {wP} V {wV} J {wJ} L {wL} H {wH} F {wF} B {wB} O2 {wO2} D {wD} P2 {wP2} I '{wI?.name}' (openables {Furniture.Openables.Count}, parts {Furniture.Parts.Count}, lifts {Furniture.Lifts.Count}, holograms {Furniture.Holograms.Count})");
        }
        switch (step)
        {
            case 0 when t > 5:
                if (!host)
                {
                    if (wO >= 0) Furniture.Openables[wO].Open();
                    if (wP >= 0) Furniture.Parts[wP].CrackItem(Vector3.forward);
                    // What Steal's tween callback does (ISIL): OnSteal, then the report list.
                    if (wV >= 0) { Collectibles.Valuables[wV].OnSteal(); MissionStealingController.Instance.stolenItems.Add(Collectibles.Valuables[wV]); }
                    if (wJ >= 0) { w.Items[wJ].OnToolPunch(); Plugin.L.LogInfo($"WORLD2 client hit item {wJ} with the mop"); }
                    if (wO2 >= 0) Furniture.Openables[wO2].Interact();   // a click
                    if (wD >= 0) { doorBefore = DoorPose(Doors.Shortcuts[wD]); Doors.Shortcuts[wD].Toggle(); }
                    if (wI != null) { wI.Interact(); Plugin.L.LogInfo($"WORLD2 client used '{wI.name}'"); }
                    if (wX != null) { wX.OnFix(); Plugin.L.LogInfo($"WORLD2 client stood up '{wX.name}'"); }
                    if (wS != null) { wS.Interact(); Plugin.L.LogInfo($"WORLD2 client used story object '{wS.name}'"); }
                    Reports.Instance?.TestReport("тест: стол стоит криво");
                    PixelCrushers.DialogueSystem.DialogueLua.SetVariable("CoopTest_Flag", Objectives.Box(true));
                }
                else
                {
                    if (wB >= 0) { Collectibles.Breakables[wB].CrackItem(Vector3.down * 3); Plugin.L.LogInfo($"WORLD2 host cracked at {Time.realtimeSinceStartup:F1}"); }
                    if (wP2 >= 0) Furniture.Parts[wP2].CrackItem(Vector3.forward);
                }
                Plugin.L.LogInfo($"WORLD2 {(host ? "host" : "client")} acted");
                step = 10; break;
            // The host's other actions a few seconds apart: one of them froze the host's game for 79 s.
            case 10 when t > 6.5f:
                if (host && wL >= 0) { Plugin.L.LogInfo($"WORLD2 host lift at {Time.realtimeSinceStartup:F1}"); Furniture.Lifts[wL].Interact(); Plugin.L.LogInfo($"WORLD2 host lift done at {Time.realtimeSinceStartup:F1}"); }
                step = 11; break;
            case 11 when t > 8:
                if (host && wH >= 0) { Plugin.L.LogInfo($"WORLD2 host furniture at {Time.realtimeSinceStartup:F1}"); Furniture.Holograms[wH].LoadItem(Furniture.Pieces[wF]); Plugin.L.LogInfo($"WORLD2 host furniture done at {Time.realtimeSinceStartup:F1}"); }
                step = 1; break;
            case 1 when t > 9:
                if (host && wP2 >= 0)
                {
                    var ci = Furniture.Parts[wP2] != null ? Furniture.Parts[wP2].GetComponentInChildren<CollectableItem>(true) : null;
                    Plugin.L.LogInfo($"WORLD2 host collects part {wP2}: {(ci != null ? "has trash" : "NO CollectableItem")}");
                    if (ci != null) ci.Collect();
                }
                if (host && wB >= 0)
                {
                    var shards = Collectibles.Breakables[wB].CreatedCracks;
                    Plugin.L.LogInfo($"WORLD2 host breakable {wB} made {shards?.Count} shards");
                    if (shards != null && shards.Count > 0) { var tr = shards[0].transform; tr.position += Vector3.up * 0.8f; if (shards[0].Rb != null) shards[0].Rb.isKinematic = true; }
                    if (shards != null && shards.Count > 1) { var ci = shards[1].GetComponent<CollectableItem>(); if (ci != null) { ci.Collect(); shards[1].gameObject.SetActive(false); } Plugin.L.LogInfo($"WORLD2 host collected shard 1: {(ci != null ? "yes" : "no CollectableItem")}"); }
                }
                step = 2; break;
            case 2 when t > 18:
                step = 3;   // once, even if a line below throws
                Plugin.L.LogInfo($"WORLD2 dumping at t {t:F1}");
                string f3(Vector3 v) => $"{v.x:F2}/{v.y:F2}/{v.z:F2}";
                if (wO >= 0) Plugin.L.LogInfo($"WORLD2 openable {wO} open {Furniture.Openables[wO].isOpen}");
                if (wP >= 0) { var part = Furniture.Parts[wP]; Plugin.L.LogInfo($"WORLD2 part {wP} cracked {(part == null || part.IsCracked)}"); }   // a broken part may be destroyed
                if (wV >= 0) Plugin.L.LogInfo($"WORLD2 valuable {wV} stolen {Collectibles.Valuables[wV].wasStolen} listed {MissionStealingController.Instance.stolenItems.Contains(Collectibles.Valuables[wV])}");
                if (wJ >= 0) Plugin.L.LogInfo($"WORLD2 item {wJ} owner {ses.OwnerOf(wJ)}");
                if (wL >= 0) Plugin.L.LogInfo($"WORLD2 lift {wL} up {Furniture.Lifts[wL].isInUpperPosition} platform y {Furniture.Lifts[wL].platform.position.y:F2}");
                if (wH >= 0) Plugin.L.LogInfo($"WORLD2 hologram {wH} correct {Furniture.Holograms[wH].IsCorrect} piece fixed {Furniture.Pieces[wF].IsFixed}");
                if (wB >= 0)
                {
                    var shards = Collectibles.Breakables[wB].CreatedCracks;
                    if (shards != null && shards.Count > 1)
                        Plugin.L.LogInfo($"WORLD2 shards {shards.Count}: shard0 at {f3(shards[0].transform.position)}, shard1 active {shards[1].gameObject.activeInHierarchy}");
                    else Plugin.L.LogInfo($"WORLD2 shards {shards?.Count ?? 0}");
                }
                if (wO2 >= 0) Plugin.L.LogInfo($"WORLD2 openable2 {wO2} open {Furniture.Openables[wO2].isOpen}");
                if (wD >= 0) Plugin.L.LogInfo($"WORLD2 door {wD} pose {DoorPose(Doors.Shortcuts[wD])}{(host ? "" : $" before {doorBefore}")}");
                if (wV >= 0) Plugin.L.LogInfo($"WORLD2 valuable {wV} visible {Collectibles.Valuables[wV].gameObject.activeInHierarchy}");
                if (wP2 >= 0) { var pp = Furniture.Parts[wP2]; var ci = pp != null ? pp.GetComponentInChildren<CollectableItem>(true) : null; Plugin.L.LogInfo($"WORLD2 part2 {wP2} collected {(pp == null || ci == null || ci.isCollected || !pp.gameObject.activeInHierarchy)}"); }
                if (wX != null) Plugin.L.LogInfo($"WORLD2 fixable pose {FixPose(wX)} before {wXBefore}");
                if (wS != null) Plugin.L.LogInfo($"WORLD2 story object used {wS.wasUsed}");
                var flag = PixelCrushers.DialogueSystem.DialogueLua.GetVariable("CoopTest_Flag");
                Plugin.L.LogInfo($"WORLD2 story flag {(flag.hasReturnValue && flag.isBool && flag.asBool)}");
                Plugin.L.LogInfo($"WORLD2 actions sent {Actions.Sent} applied {Actions.Applied}");
                Plugin.L.LogInfo("WORLD2 dump done");
                step = 3; break;
        }
    }

    static string FixPose(FixableObject f)
    {
        var t = f.transform; var p = t.localPosition; var e = t.localEulerAngles;
        return string.Format(CultureInfo.InvariantCulture, "{0:F2}/{1:F2}/{2:F2}/{3:F0}/{4:F0}/{5:F0}", p.x, p.y, p.z, e.x, e.y, e.z);
    }

    // Both in the job from the save; the host moves on to another job (Spa, id 2) from inside the job; the
    // client, in the old job, must switch to it by itself.
    string followFrom; float followAt;
    void FollowTest(float t, Session ses, string scene)
    {
        if (step == 0 && t > 5)
        {
            step = 1;
            if (ses.IsHost) { var m = MissionController.Instance.GetMission(Env.Int("CSCCOOP_FOLLOW_MISSION", 2)); Plugin.L.LogInfo($"FOLLOWTEST host starts {m?.displayName} from '{scene}'"); GameManager.Instance.StartMission(m, 0); }
        }
        if (step == 1 && scene != followFrom && followFrom != null && ses.Synced && CoopRunner.Instance.World.SceneKey != 0)
            foreach (var rp in ses.Players.Values)
                if (rp.SceneKey == CoopRunner.Instance.World.SceneKey) { Plugin.L.LogInfo($"FOLLOWTEST together in '{scene}'"); step = 2; followAt = Time.unscaledTime + 5; break; }
        if (step == 0) followFrom = scene;
        if (step == 2 && Time.unscaledTime > followAt) { Plugin.L.LogInfo("BOT done, quitting"); System.Diagnostics.Process.GetCurrentProcess().Kill(); }
    }

    // CCTV laptop (Smart House, job 5: run-pair sets CSCCOOP_MISSION=5 for the host, CSCCOOP_ANY_JOB=1): the client
    // hacks it and deletes a recording; both log hacked and whether that recording is still in the folder.
    string ctName; float ctAt;

    // Deletes a recording through the laptop's presenter, as a click in its folder does.
    static void DeleteInUi(HackableComputerModel m, string name)
    {
        HackableComputerPresenter presenter = null;
        foreach (var p in Resources.FindObjectsOfTypeAll<HackableComputerPresenter>()) if (p.model != null && p.model.Pointer == m.Pointer) presenter = p;
        CCTVRecord record = null;
        if (m.listOfRecordsInFolder != null) foreach (var rec in m.listOfRecordsInFolder) if (Cctv.IsRecord(rec) && rec.recordName == name) record = rec;
        if (presenter != null && Cctv.IsRecord(record)) presenter.HandleFileDelete(record);
        else Plugin.L.LogWarning($"CCTVTEST cannot delete '{name}' in the UI (presenter {presenter != null}, record {Cctv.IsRecord(record)})");
    }
    void CctvTest(float t, Session ses)
    {
        var m = Cctv.Laptops != null && Cctv.Laptops.Count > 0 ? Cctv.Laptops[0] : null;
        if (m == null) return;   // still on the way to the Smart House (the save starts in the first job)
        switch (step)
        {
            case 0 when t > 5:
                var list = m.listOfRecordsInFolder;
                ctName = null;
                if (list != null) foreach (var rec in list) if (Cctv.IsRecord(rec) && !string.IsNullOrEmpty(rec.recordName)) { ctName = rec.recordName; break; }
                ctName ??= "CCTV_TEST_RECORD";
                Plugin.L.LogInfo($"CCTVTEST laptop '{m.name}', {list?.Count ?? 0} recordings, role record '{ctName}'");
                // the way the laptop's UI deletes: HackableComputerPresenter.HandleFileDelete
                if (!ses.IsHost) { m.Hack(true); DeleteInUi(m, ctName); Plugin.L.LogInfo("CCTVTEST client hacked and deleted"); }
                ctAt = Time.unscaledTime + 8; step = 1; break;
            case 1 when Time.unscaledTime > ctAt:
                bool present = false;
                if (m.listOfRecordsInFolder != null) foreach (var rec in m.listOfRecordsInFolder) if (Cctv.IsRecord(rec) && rec.recordName == ctName) present = true;
                Plugin.L.LogInfo($"CCTVTEST hacked {m.IsHacked} record present {present}");
                // then the client deletes all the others: the job's "delete the recordings" objective
                if (!ses.IsHost && m.listOfRecordsInFolder != null)
                {
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var rec in m.listOfRecordsInFolder) if (Cctv.IsRecord(rec) && rec.recordName != null) names.Add(rec.recordName);
                    foreach (var n in names) DeleteInUi(m, n);
                    Plugin.L.LogInfo($"CCTVTEST client deleted the other {names.Count}");
                }
                ctAt = Time.unscaledTime + 8; step = 2; break;
            case 2 when Time.unscaledTime > ctAt:
                int left = 0;
                if (m.listOfRecordsInFolder != null) foreach (var rec in m.listOfRecordsInFolder) if (Cctv.IsRecord(rec)) left++;
                string quest = null;
                foreach (var p in Resources.FindObjectsOfTypeAll<HackableComputerPresenter>())
                    if (p.model != null && p.model.Pointer == m.Pointer) quest = p.questName;
                string qs = string.IsNullOrEmpty(quest) ? "-" : PixelCrushers.DialogueSystem.QuestLog.GetQuestState(quest).ToString();
                Plugin.L.LogInfo($"CCTVTEST recordings left {left} quest '{quest}' {qs}");
                step = 3; break;
        }
    }

    // Only the lift: the host toggles it; both log the platform and the frame times.
    void LiftTest(float t, Session ses)
    {
        var lift = Furniture.Lifts.Count > 0 ? Furniture.Lifts[0] : null;
        if (lift == null) return;
        switch (step)
        {
            case 0 when t > 5:
                if (ses.IsHost && Env.Get("CSCCOOP_LIFTMODE") != "none") { Plugin.L.LogInfo($"LIFTTEST host toggles at {Time.realtimeSinceStartup:F1}"); lift.Interact(); Plugin.L.LogInfo($"LIFTTEST host toggled at {Time.realtimeSinceStartup:F1}"); }
                step = 1; break;
            case >= 1 and < 12 when t > 5 + 2 * step:
                Plugin.L.LogInfo($"LIFTTEST up {lift.isInUpperPosition} moving {lift.isMoving} y {lift.platform.position.y:F2} enabled {lift.enabled} active {lift.gameObject.activeInHierarchy} timeScale {Time.timeScale} lower {lift.lowerPosition:F2} upper {lift.upperPosition:F2} applied {Furniture.Applied} at {Time.realtimeSinceStartup:F1}");
                step++; break;
        }
    }

    static void DumpStains(GameWorld w, string label)
    {
        string path = Env.Get("CSCCOOP_CSV");
        if (path == "") return;
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < w.Taints.Count; i++)
        {
            var t = w.Taints[i];
            var cd = t != null ? t.GetComponent<CleanableDecal>() : null;
            var last = cd?.hits != null && cd.hits.Count > 0 ? cd.hits[cd.hits.Count - 1] : null;
            sb.Append(string.Format(ci, "{0},{1},{2:F4},{3},{4},{5},{6:F3},{7:F3}\n", label, i, t != null ? t.Intensity : -1,
                t != null && t.gameObject.activeInHierarchy ? 1 : 0, Stains.IsDestroyed(i) ? 1 : 0, cd?.hits?.Count ?? -1, last?.strength ?? 0, last?.effectiveness ?? 0));
        }
        File.AppendAllText(path, sb.ToString());
        foreach (var kv in Stains.NewStains)
        {
            var cd = kv.Value != null ? kv.Value.GetComponent<CleanableDecal>() : null;
            Plugin.L.LogInfo($"STAINTEST {label} new stain {kv.Key:X} at {kv.Value?.transform.position.ToString("F3")} intensity {(kv.Value != null ? kv.Value.Intensity : -1):F3} hits {cd?.hits?.Count}");
        }
        Plugin.L.LogInfo($"STAINTEST dump {label} at {Time.unscaledTime:F1}");
    }

    // Main menu: the panel lists the save slots; picking slot 1 and hosting loads it by itself.
    int slstep; float slAt;
    void SlotTest(string scene, float inScene, GameWorld w)
    {
        float now = Time.unscaledTime;
        if (now < slAt) return;
        switch (slstep)
        {
            case 0 when scene.StartsWith("MenuScreen") && inScene > 15:
                CoopMenu.Instance.Toggle(); slstep = 1; slAt = now + 2; break;
            case 1:
                if (Env.Get("CSCCOOP_SHOT") != "") ScreenCapture.CaptureScreenshot(Env.Get("CSCCOOP_SHOT"));
                for (int i = 1; i <= 3; i++) Plugin.L.LogInfo($"SLOTTEST slot {i} available {SaveSystem.Instance.IsFileAvailable(i)}");
                slstep = 2; slAt = now + 2; break;
            case 2:
                Plugin.LoadSlot.Value = 1;
                CoopRunner.Instance.HostSteam();
                CoopMenu.Instance.LoadChosenSlot();
                Plugin.L.LogInfo($"SLOTTEST hosted with slot 1 from '{scene}'");
                slstep = 3; break;
            case 3 when !scene.StartsWith("MenuScreen") && w.Player != null && w.Player.CanMove:
                Plugin.L.LogInfo($"SLOTTEST loaded into '{scene}', player can move, session {(CoopRunner.Instance.Session != null ? "on" : "off")}");
                Plugin.LoadSlot.Value = 0; Plugin.AutoHost.Value = false;   // leave the copy's settings as they were
                CoopRunner.Instance.Stop();
                slstep = 4; slAt = now + 2; break;
            case 4:
                Plugin.L.LogInfo("BOT done, quitting");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                break;
        }
    }

    // Single copy, no session: does cracking a breakable and collecting a shard freeze the game by itself?
    int spStep; float spAt;
    void ShardProbe(GameWorld w)
    {
        if (w.Player == null || !w.Player.CanMove || Time.unscaledTime < spAt) return;
        var mode = Env.Get("CSCCOOP_SHARDMODE");   // "collect" (default), "move", "hide"
        switch (spStep)
        {
            case 0 when mode == "lift":
                Plugin.L.LogInfo($"SHARDPROBE lift {Furniture.Lifts.Count} at {Time.realtimeSinceStartup:F1}, up {Furniture.Lifts[0].isInUpperPosition}");
                Furniture.Lifts[0].Interact();
                Plugin.L.LogInfo($"SHARDPROBE lift toggled at {Time.realtimeSinceStartup:F1}");
                spStep = 2; spAt = Time.unscaledTime + 8; break;
            case 0:
                for (int i = 0; i < Collectibles.Breakables.Count; i++)
                    if (Collectibles.Breakables[i] != null && Collectibles.Breakables[i].gameObject.activeInHierarchy)
                    { Collectibles.Breakables[i].CrackItem(Vector3.down * 3); Plugin.L.LogInfo($"SHARDPROBE cracked {i} at {Time.realtimeSinceStartup:F1}"); spB = i; break; }
                spStep = 1; spAt = Time.unscaledTime + 3; break;
            case 1:
                var shards = Collectibles.Breakables[spB].CreatedCracks;
                Plugin.L.LogInfo($"SHARDPROBE {shards?.Count} shards, mode '{mode}' at {Time.realtimeSinceStartup:F1}");
                if (shards != null && shards.Count > 1)
                {
                    if (mode == "move") { shards[0].transform.position += Vector3.up * 0.8f; if (shards[0].Rb != null) shards[0].Rb.isKinematic = true; }
                    else if (mode == "hide") shards[1].gameObject.SetActive(false);
                    else if (mode == "collecthide") { var ci = shards[1].GetComponent<CollectableItem>(); if (ci != null) ci.Collect(); shards[1].gameObject.SetActive(false); }
                    else { var ci = shards[1].GetComponent<CollectableItem>(); if (ci != null) ci.Collect(); }
                }
                Plugin.L.LogInfo($"SHARDPROBE acted at {Time.realtimeSinceStartup:F1}");
                spStep = 2; spAt = Time.unscaledTime + 5; break;
            case 2:
                Plugin.L.LogInfo($"SHARDPROBE after: {Time.realtimeSinceStartup:F1}, last frame {Time.unscaledDeltaTime * 1000:F0} ms");
                Plugin.L.LogInfo("BOT done, quitting");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                break;
        }
    }
    int spB;

    // One-account smoke test of the Steam path (a second account is needed for a real join).
    int sstep; float sAt; CscCoop.Net.SteamTransport st; CscCoop.Net.SteamDirectory sd;
    void SteamTest()
    {
        float now = Time.unscaledTime;
        if (now < sAt) return;
        try
        {
            switch (sstep)
            {
                case 0:
                    sd = new CscCoop.Net.SteamDirectory();
                    Plugin.L.LogInfo($"STEAMTEST me {(sd.Me != 0 ? "nonzero" : "ZERO")}, name {(sd.MyName.Length > 0 ? "read" : "EMPTY")}, friends in game {sd.FriendsInGame().Count}");
                    st = new CscCoop.Net.SteamTransport();
                    st.Send(st.LocalId, new byte[] { 0xC0, 0xC5, 0x42 }, 3, true);
                    sstep = 1; sAt = now + 3; break;
                case 1:
                    var got = new System.Collections.Generic.List<CoopCore.Packet>();
                    st.Poll(got);
                    bool echo = got.Count == 1 && got[0].Data.Length == 3 && got[0].Data[2] == 0x42 && got[0].From == st.LocalId;
                    Plugin.L.LogInfo($"STEAMTEST self message: {(echo ? "came back, sender = me" : $"FAILED ({got.Count} packets)")}");
                    Plugin.L.LogInfo($"STEAMTEST session state with myself: {CscCoop.Net.SteamTransport.StateName(st.State(st.LocalId))}, accept returns {st.Accept(st.LocalId)}");
                    st.Dispose(); st = null;
                    sd.Announce("host");
                    sstep = 2; sAt = now + 2; break;
                case 2:
                    Plugin.L.LogInfo($"STEAMTEST own presence csccoop='{sd.Presence(sd.Me, "csccoop")}' (expect host)");
                    CoopRunner.Instance.HostSteam();
                    var ses = CoopRunner.Instance.Session;
                    Plugin.L.LogInfo($"STEAMTEST host session: {(ses != null && ses.IsHost ? "started" : "FAILED " + CoopRunner.Instance.Error)}");
                    CoopMenu.Instance.Toggle();
                    sstep = 3; sAt = now + 2; break;
                case 3:
                    string shot = Env.Get("CSCCOOP_SHOT");
                    if (shot != "") { ScreenCapture.CaptureScreenshot(shot); Plugin.L.LogInfo("STEAMTEST screenshot of the panel taken"); }
                    sstep = 4; sAt = now + 2; break;
                case 4:
                    CoopMenu.Instance.Toggle();
                    CoopRunner.Instance.Stop();
                    sstep = 5; sAt = now + 2; break;
                case 5:
                    Plugin.L.LogInfo($"STEAMTEST after leaving csccoop='{sd.Presence(sd.Me, "csccoop")}' (expect empty)");
                    Plugin.L.LogInfo("BOT done, quitting");
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    break;
            }
        }
        catch (Exception e) { Plugin.L.LogError($"STEAMTEST exception: {e}"); sstep = 5; }
    }

    // Census (owner: "go through the whole game"): one copy, no session. From the hub it starts every mission
    // in turn and writes each scene's component types with their counts (CSCCOOP_CSV: scene,type,count), so the
    // mod's coverage can be checked against what the jobs really contain, not only the first one.
    readonly System.Collections.Generic.List<MissionData> censusQueue = new();
    readonly System.Collections.Generic.HashSet<string> censusDone = new();
    bool censusListed; float censusAt;
    MissionData censusPending; string censusFrom; int censusTries; bool censusLoaded;
    void Census(string scene, float inScene, GameWorld w)
    {
        if (!censusListed && scene.Contains("Hub") && MissionController.Instance?.missions?.data != null && inScene > 5)
        {
            censusListed = true;
            foreach (var m in MissionController.Instance.missions.data) if (m != null) censusQueue.Add(m);
            Plugin.L.LogInfo($"CENSUS {censusQueue.Count} missions");
        }
        if (scene.Contains("Loading")) censusLoaded = true;   // the asked-for start is under way
        if (!censusListed || scene.Contains("Loading") || scene.Contains("Menu")) return;
        bool ready = (w.Player != null && w.Player.CanMove && inScene > 8) || inScene > 60;
        if (!ready || Time.unscaledTime < censusAt) return;
        // A start asked for while the scene was still settling can be lost (the old job reloaded instead):
        // still on the scene it was started from, start it again (twice at most).
        if (censusPending != null)
        {
            if (!censusLoaded && scene == censusFrom && censusTries < 3)
            {
                censusTries++;
                Plugin.L.LogInfo($"CENSUS mission {censusPending.id} didn't load, again ({censusTries})");
                GameManager.Instance.StartMission(censusPending, 0);
                censusAt = Time.unscaledTime + 30;
                return;
            }
            censusPending = null;
        }
        if (!censusDone.Contains(scene))
        {
            censusDone.Add(scene);
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
            {
                if (mb == null || mb.gameObject.scene.name != scene) continue;
                var n = mb.GetIl2CppType().FullName;
                counts[n] = counts.TryGetValue(n, out var c) ? c + 1 : 1;
            }
            var sb = new StringBuilder();
            foreach (var kv in counts) sb.Append($"{scene},{kv.Key},{kv.Value}\n");
            var path = Env.Get("CSCCOOP_CSV");
            if (path != "") File.AppendAllText(path, sb.ToString());
            Plugin.L.LogInfo($"CENSUS scene '{scene}': {counts.Count} types, table line follows");
        }
        while (censusQueue.Count > 0)
        {
            var m = censusQueue[0]; censusQueue.RemoveAt(0);
            if (censusDone.Contains("scene:" + m.scene)) continue;   // another mission on a scene already counted
            censusDone.Add("scene:" + m.scene);
            Plugin.L.LogInfo($"CENSUS starting mission {m.id} '{m.displayName}'");
            GameManager.Instance.StartMission(m, 0);
            censusPending = m; censusFrom = scene; censusTries = 0; censusLoaded = false;
            censusAt = Time.unscaledTime + 30;
            return;
        }
        Plugin.L.LogInfo("CENSUS done");
        BepInEx.Logging.Logger.Listeners.Clear();
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    // From the main menu the way a player goes: the Co-op button, then host: Host -> slot; client: Join -> slot ->
    // "Играть у ..." (one click on the host). The localhost session starts from the screen (CSCCOOP_VIA_MENU).
    // CSCCOOP_SLOT picks the slot, CSCCOOP_MENU_NEW=1 starts a new game in it (it must be empty).
    int mfStep; float mfAt; bool mfShot;
    void MenuFlow(string scene, float inScene)
    {
        if (mfStep >= 9 || !scene.StartsWith("MenuScreen") || inScene < Env.Int("CSCCOOP_MENU_WAIT", 25) || Time.unscaledTime < mfAt) return;
        mfAt = Time.unscaledTime + 2;
        var cs = CoopScreen.Instance;
        bool host = Env.Get("CSCCOOP_ROLE") == "host";
        string role = host ? "host" : "client";
        string dir = Env.Get("CSCCOOP_SHOT_DIR");
        void Shot(string page) { if (dir != "") ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"shot_menu_{role}_{page}.png")); }
        int slot = Env.Int("CSCCOOP_SLOT", 1);
        bool fresh = Env.Get("CSCCOOP_MENU_NEW") == "1";
        // A screenshot is taken at the end of the frame, after anything clicked in it: each page is shot on a tick
        // of its own, and clicked on the next.
        if (mfStep is 1 or 2 or 3 && !mfShot) { Shot(mfStep == 1 ? "root" : mfStep == 2 ? "slots" : "friends"); mfShot = true; return; }
        mfShot = false;
        switch (mfStep)
        {
            case 0:
                if (!cs.ButtonAdded) return;
                Shot("main");
                mfStep = 5; break;
            case 5:
                cs.PressMenuButton();
                Plugin.L.LogInfo($"MENUFLOW pressed the main menu's Co-op button: page {cs.Current}");
                mfStep = cs.Current == CoopScreen.Page.Root ? 1 : 9; break;
            case 1:
                if (host) cs.Host(); else cs.Join();
                mfStep = 2; break;
            case 2:
                Plugin.L.LogInfo($"MENUFLOW {role} chose slot {slot}{(fresh ? " (new game)" : "")}");
                cs.Slot(slot, fresh);
                mfStep = host ? 9 : 3; break;
            case 3:
                var f = cs.Friends;
                if (f.Count == 0) return;
                Plugin.L.LogInfo($"MENUFLOW client pressed '{(f[0].Hosting ? "Играть у" : "Ждать")} {f[0].Name}'");
                cs.Play(f[0]);
                mfStep = 9; break;
        }
    }

    void ListMissions()
    {
        var mc = MissionController.Instance;
        if (mc?.missions == null) { Plugin.L.LogInfo("BOT no MissionController"); return; }
        foreach (var src in new[] { mc.ActiveMissions, mc.CompletedMissions })
        {
            if (src == null) continue;
            foreach (var m in src) Plugin.L.LogInfo($"BOT mission {m.id} '{m.displayName}' scene {m.scene} completed {m.completed} active {m.active}");
        }
        if (mc.missions?.data != null)
            foreach (var m in mc.missions.data) if (m != null) Plugin.L.LogInfo($"BOT known mission {m.id} '{m.displayName}' scene {m.scene}");
    }

    // Client: walk up to the nearest loose item, grab it, back away with it, throw it. Host: try to take
    // it out of the client's hands (must be refused), then carry and drop an item of its own.
    // Both dump the item table at fixed points; tools/item_report.py compares the dumps.
    void ItemTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        bool actor = step < 6 ? !host : host;   // the client acts first, then the host
        switch (step)
        {
            case 0 when t > 4:
                Dump(w, ses, "start");
                if (!host) { myItem = Nearest(w, -1); Approach(w, myItem); }
                step = 1; break;
            case 1 when t > 5:
                if (!host) Grab(w, myItem, "client");
                walkFrom = w.Player.transform.position; walkDir = -w.Player.transform.forward;
                step = 2; break;
            case 2 when t > 7: step = 21; break;          // the client has stopped walking: a step of its own,
            case 21 when t > 7.5f:                        // so the other bot dumps only after that
                Dump(w, ses, "carry");
                if (host) TrySteal(ses, w);
                step = 3; break;
            case 3 when t > 9:
                if (!host && w.Grabber.GrabbedRb != null) { w.Grabber.TryRelease(w.Player.transform.forward * 3 + Vector3.up * 2); Plugin.L.LogInfo($"ITEMTEST client threw {myItem}"); }
                step = 4; break;
            case 4 when t > 13:
                if (host) { myItem = Nearest(w, otherItem); Approach(w, myItem); }
                step = 5; break;
            case 5 when t > 14:
                if (host) Grab(w, myItem, "host");
                walkFrom = w.Player.transform.position; walkDir = -w.Player.transform.forward;
                step = 6; break;
            case 6 when t > 16: step = 61; break;          // the host has stopped walking
            case 61 when t > 16.5f:
                Dump(w, ses, "hostcarry");
                step = 7; break;
            case 7 when t > 17:
                if (host && w.Grabber.GrabbedRb != null) { w.Grabber.TryRelease(); Plugin.L.LogInfo($"ITEMTEST host dropped {myItem}"); }
                step = 8; break;
            case 8 when t > 28:
                Dump(w, ses, "final");
                step = 9; break;
        }
        // Carrying: back away 1.5 m over 2 s (client 5..7 s, host 14..16 s), then stand still holding it, so the
        // carry dumps (7.5 s, 16.5 s) compare a held item at rest: the bots' clocks can differ by seconds since
        // they wait for each other.
        if (!host && step == 2) { Walk(w, Mathf.Min(1f, (t - 5) / 2f) * 1.5f); Trace(w, t); }
        if (host && step == 6) { Walk(w, Mathf.Min(1f, (t - 14) / 2f) * 1.5f); Trace(w, t); }
    }

    void TrySteal(Session ses, GameWorld w)
    {
        for (int i = 0; i < w.Items.Count; i++) if (ses.OwnerOf(i) == 1) otherItem = i;
        if (otherItem < 0) { Plugin.L.LogInfo("ITEMTEST host sees no item owned by player 1"); return; }
        w.Grabber.GrabItem(w.Items[otherItem]);
        bool refused = w.Grabber.GrabbedRb == null || w.IndexOf(w.Grabber.GrabbedRb) != otherItem;
        Plugin.L.LogInfo($"ITEMTEST host steal of {otherItem}: {(refused ? "refused" : "ALLOWED")}");
    }

    // Puts the player 1.2 m from the item, facing it. The grab comes a second later: grabbing in the
    // same frame as a teleport makes the game fling the item (its speed comes from the last frame).
    void Approach(GameWorld w, int i)
    {
        if (i < 0) return;
        var item = w.Items[i].transform.position;
        var me = w.Player.transform.position;
        var flat = new Vector3(item.x - me.x, 0, item.z - me.z);
        if (flat.magnitude > 1.2f)
        {
            var p = new Vector3(item.x, me.y, item.z) - flat.normalized * 1.2f;
            if (w.Player.rb != null) w.Player.rb.position = p;
            w.Player.transform.position = p;
        }
        w.Player.transform.rotation = Quaternion.LookRotation(flat.normalized);
    }

    void Grab(GameWorld w, int i, string who)
    {
        if (i < 0) { Plugin.L.LogInfo($"ITEMTEST {who}: no item in reach"); return; }
        w.Grabber.GrabItem(w.Items[i]);
        bool held = w.Grabber.GrabbedRb != null && w.IndexOf(w.Grabber.GrabbedRb) == i;
        Plugin.L.LogInfo($"ITEMTEST {who} grab {i} ({w.Items[i].name}) at {w.Items[i].transform.position}, {Vector3.Distance(w.Player.transform.position, w.Items[i].transform.position):F2} m away: {(held ? "held" : "NOT held")}");
    }

    float nextTrace;
    void Trace(GameWorld w, float t)
    {
        if (t < nextTrace) return;
        nextTrace = t + 0.5f;
        var g = w.Grabber.GrabbedRb;
        Plugin.L.LogInfo($"ITEMTEST trace t={t:F1} player {w.Player.transform.position} held {(g != null ? w.IndexOf(g) : -1)} " +
            (g != null ? $"at {g.transform.position} kinematic {g.Rb?.isKinematic} grabPoint {w.Grabber.GrabPoint?.position} " +
                $"vel {g.Rb?.velocity} constraints {g.Rb?.constraints} sleeping {g.Rb?.IsSleeping()} rbIsOwn {(g.Rb != null && g.Rb.gameObject == g.gameObject)} " +
                $"grabberOn {w.Grabber.enabled} dist {w.Grabber.currentDist:F2} target {w.Grabber.targetPosition} parent {g.transform.parent?.name}" : ""));
    }

    void Walk(GameWorld w, float dist)
    {
        var p = walkFrom + walkDir * dist;
        var rb = w.Player.rb;
        if (rb != null) rb.position = p;
        w.Player.transform.position = p;
    }

    static int Nearest(GameWorld w, int except)
    {
        // Not filtered on isKinematic: items start frozen, and on a client every host item is kinematic.
        int best = -1; float bd = 8f;
        var me = w.Player.transform.position;
        for (int i = 0; i < w.Items.Count; i++)
        {
            var g = w.Items[i];
            if (i == except || g == null || !g.gameObject.activeInHierarchy || g.Rb == null || g.IsHeld) continue;
            float d = Vector3.Distance(me, g.transform.position);
            if (d < bd) { bd = d; best = i; }
        }
        Plugin.L.LogInfo($"ITEMTEST nearest item {best} at {bd:F2} m");
        return best;
    }

    static void Dump(GameWorld w, Session ses, string label)
    {
        string path = Env.Get("CSCCOOP_CSV");
        if (path == "") return;
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < w.Items.Count; i++)
        {
            var g = w.Items[i];
            if (g == null) continue;
            var p = g.transform.position;
            sb.Append(string.Format(ci, "{0},{1},{2:F3},{3:F3},{4:F3},{5},{6},{7}\n", label, i, p.x, p.y, p.z, ses.OwnerOf(i), ses.EpochOf(i), g.IsHeld ? 1 : 0));
        }
        File.AppendAllText(path, sb.ToString());
        Plugin.L.LogInfo($"ITEMTEST dump {label}");
    }
}
