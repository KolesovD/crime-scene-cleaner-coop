using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CscCoop;

// Playing a run of jobs together without "everyone load the same job, then connect":
//  - the host starting a job (GameManager.StartMission) sends a job ticket as global state: a counter, the
//    mission id and variant. A client in its hub (Kowalski's apartment) starts the same job by itself; a
//    client that joins later gets the newest ticket on connect.
//  - leaving the job (MissionLeaveController.LeaveMissionWithReport) is an event: everyone leaves. Each
//    machine shows its own report and closing it pays into its own save (GameManager.CompleteMission,
//    PerksController.AddMoney), so money and skill points stay per player.
// A ticket is only followed once and only if it is newer than the last one this client followed (PR #176's
// bug: a client still on the previous report screen took a new ticket for the old job).
// Saves stay whole (each player's story moves by its own mails and jobs):
//  - a client follows only into a job its own save has (accepted on its laptop, or done before: a replay);
//  - the host accepting a job on its laptop (GameManager.AddMission) is mirrored: a client whose own laptop
//    offers the same job accepts it too (HostJobs, the host's accepted jobs as global state);
//  - every client reports the jobs its save can start (MyJobs); the host can't start a job at the truck
//    (MissionsMapController.StartMission) until every player in the session has it;
//  - a client can't start a job at its truck at all: the host starts, the client follows.
[HarmonyPatch]
public static class Jobs
{
    static int sentTickets, lastFollowed = -1;
    static (int ticket, int mission, int variant) pending = (-1, 0, 0);
    public static int Left;

    // The intro (HubResidence-GameIntro, a new game's prologue) is not the hub: nobody is taken out of it.
    static bool InHub { get { var n = SceneManager.GetActiveScene().name; return n.Contains("Hub") && !n.Contains("GameIntro"); } }
    static bool InIntro => SceneManager.GetActiveScene().name.Contains("GameIntro");

    // Jobs this save can start: accepted on the laptop or already done (replays).
    public static HashSet<int> OwnJobs()
    {
        var set = new HashSet<int>();
        var mc = MissionController.Instance;
        if (mc == null) return set;
        if (mc.ActiveMissions != null) foreach (var m in mc.ActiveMissions) if (m != null) set.Add(m.id);
        if (mc.CompletedMissions != null) foreach (var m in mc.CompletedMissions) if (m != null) set.Add(m.id);
        return set;
    }

    static List<int> Accepted()
    {
        var l = new List<int>();
        var mc = MissionController.Instance;
        if (mc?.ActiveMissions != null) foreach (var m in mc.ActiveMissions) if (m != null) l.Add(m.id);
        l.Sort();
        return l;
    }

    static string Name(int id) { var m = MissionController.Instance?.GetMission(id); return m != null ? m.displayName : $"#{id}"; }
    static bool AnyJob => Env.Get("CSCCOOP_ANY_JOB") == "1";   // manual test: the copies' saves lack the job

    // Host: what each client's save can start (MyJobs), by player number.
    static readonly Dictionary<byte, HashSet<int>> clientJobs = new();
    public static HashSet<int> ClientJobsOf(byte n) => clientJobs.TryGetValue(n, out var set) ? set : null;
    static List<int> hostJobs;           // client: the host's accepted jobs, to mirror
    static string sentJobs = "";        // what was last sent (HostJobs on the host, MyJobs on a client)
    static object sentOn;               // ... on which session and as which player
    static float nextJobsCheck;
    static int warnedTicket = -1;

    public static void OnHostJobs(BinaryReader r)
    {
        int n = r.ReadInt32();
        var l = new List<int>(n);
        for (int i = 0; i < n; i++) l.Add(r.ReadInt32());
        if (CoopRunner.Instance?.Session?.IsHost == true) return;
        hostJobs = l;
        Plugin.L.LogInfo($"COOP the host has accepted jobs [{string.Join(",", l)}]");
    }

    public static void OnClientJobs(byte from, BinaryReader r)
    {
        int n = r.ReadInt32();
        var set = new HashSet<int>();
        for (int i = 0; i < n; i++) set.Add(r.ReadInt32());
        if (CoopRunner.Instance?.Session?.IsHost != true) return;
        clientJobs[from] = set;
        Plugin.L.LogInfo($"COOP player {from} can start jobs [{string.Join(",", set)}]");
    }

    static void WriteList(BinaryWriter w, ICollection<int> l) { w.Write(l.Count); foreach (var i in l) w.Write(i); }

    // Every 2 s: the host sends its accepted jobs when they change; a client accepts the host's jobs its own
    // laptop offers, then sends what it can start when that changes.
    static void SyncJobs(CoopCore.Session s)
    {
        if (Time.unscaledTime < nextJobsCheck || MissionController.Instance == null) return;
        nextJobsCheck = Time.unscaledTime + 2;
        if (!s.IsHost && s.Me == CoopCore.Session.NoPlayer) return;
        object on = (s, s.Me);
        if (!s.IsHost && hostJobs != null) MirrorLaptop(hostJobs);
        var list = s.IsHost ? Accepted() : new List<int>(OwnJobs());
        list.Sort();
        string text = string.Join(",", list);
        if (text == sentJobs && Equals(on, sentOn)) return;
        sentJobs = text; sentOn = on;
        if (s.IsHost) WorldEvents.SendGlobal(Ev.HostJobs, w => WriteList(w, list), 1);
        else WorldEvents.SendGlobal(Ev.MyJobs, w => WriteList(w, list), 100u + s.Me);
        Plugin.L.LogInfo($"COOP sent {(s.IsHost ? "my accepted jobs" : "the jobs I can start")} [{text}]");
    }

    // Client: the host's accepted jobs that this save's own laptop offers and hasn't accepted yet are accepted
    // the way the laptop does it. A job this save isn't offered yet (its story is behind) is left alone.
    static void MirrorLaptop(List<int> jobs)
    {
        var mc = MissionController.Instance;
        var gm = GameManager.Instance;
        if (mc?.AvailableMails == null || gm == null) return;
        var own = OwnJobs();
        foreach (int id in jobs)
        {
            if (own.Contains(id)) continue;
            MailData mail = null;
            foreach (var m in mc.AvailableMails) if (m != null && m.hasMission && m.missionId == id && !m.HasBeenAccepted) { mail = m; break; }
            if (mail == null) continue;
            Plugin.L.LogInfo($"COOP the host accepted job {id} '{Name(id)}': accepting it on my laptop too (mail {mail.id})");
            WorldEvents.Applying = true;
            try { gm.AddMission(mail); }
            catch (System.Exception e) { Plugin.L.LogError($"COOP accepting mail {mail.id} failed: {e}"); }
            finally { WorldEvents.Applying = false; }
            CoopMenu.Toast($"Хост принял заказ «{Name(id)}»: у тебя на ноутбуке он тоже принят");
        }
    }

    // The truck's "start job" button. The client never starts one; the host only when every player has it.
    [HarmonyPatch(typeof(MissionsMapController), nameof(MissionsMapController.StartMission))]
    [HarmonyPrefix]
    static bool TruckStart(MissionData mission)
    {
        if (WorldEvents.Applying || mission == null || TruckAllowed(mission, out var why)) return true;
        Plugin.L.LogInfo($"COOP truck start of job {mission.id} refused: {why}");
        CoopMenu.Toast(why, 8);
        return false;
    }

    public static bool TruckAllowed(MissionData mission, out string why)
    {
        why = "";
        var s = CoopRunner.Instance?.Session;
        if (s == null) return true;
        if (!s.IsHost) { why = "В коопе заказ запускает хост: ты поедешь вместе с ним. Играть одному: выйди из коопа (F8)."; return false; }
        if (AnyJob) return true;
        foreach (var rp in s.Players.Values)
        {
            if (!clientJobs.TryGetValue(rp.Number, out var set)) { why = $"Игрок {rp.Number} ещё не прислал свои заказы, подожди пару секунд."; return false; }
            if (!set.Contains(mission.id)) { why = $"У игрока {rp.Number} ещё нет заказа «{mission.displayName}». Ему нужно поспать, ответить на телефон и дождаться письма: если ты заказ уже принял, у него он примется сам (или пусть примет в почте). Без этого его сохранение сломается."; return false; }
        }
        return true;
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartMission))]
    [HarmonyPostfix]
    static void StartMission(MissionData mission, int variantOption)
    {
        var s = CoopRunner.Instance?.Session;
        if (s == null || !s.IsHost || mission == null || WorldEvents.Applying) return;
        int ticket = ++sentTickets;
        int id = mission.id;
        announcedMission = id;
        Plugin.L.LogInfo($"COOP job ticket {ticket}: mission {id} '{mission.displayName}' variant {variantOption}");
        WorldEvents.SendGlobal(Ev.JobTicket, w => { w.Write(ticket); w.Write(id); w.Write(variantOption); }, 1);
    }

    public static void OnTicket(BinaryReader r)
    {
        int ticket = r.ReadInt32(), id = r.ReadInt32(), variant = r.ReadInt32();
        if (ticket <= lastFollowed) return;
        pending = (ticket, id, variant);
        Plugin.L.LogInfo($"COOP host started job {id} (ticket {ticket})");
    }

    static uint announcedKey;
    static int announcedMission = -1;

    // Every frame. Host: a job entered without StartMission (a save that loads straight into a job) is announced
    // too. Client: start the pending job from the hub, or switch to it from another job: two saves at different
    // missions put the players in different jobs (owner's manual test: mission 2 on one copy, 1 on the other).
    public static void Tick(GameWorld w)
    {
        var s = CoopRunner.Instance?.Session;
        var gm = GameManager.Instance;
        if (s == null || gm == null) return;
        SyncJobs(s);
        if (s.IsHost)
        {
            foreach (var num in new List<byte>(clientJobs.Keys)) if (!s.Players.ContainsKey(num)) clientJobs.Remove(num);
            if (w.SceneKey == 0 || w.SceneKey == announcedKey || w.InHub) return;
            announcedKey = w.SceneKey;
            var cur = gm.ActiveMission;
            if (cur == null || cur.id == announcedMission) return;
            announcedMission = cur.id;
            int ticket = ++sentTickets, id = cur.id;
            Plugin.L.LogInfo($"COOP job ticket {ticket}: in mission {id} '{cur.displayName}' (loaded)");
            WorldEvents.SendGlobal(Ev.JobTicket, wr => { wr.Write(ticket); wr.Write(id); wr.Write(0); }, 1);
            return;
        }
        if (pending.ticket <= lastFollowed) return;
        var active = gm.ActiveMission;
        bool inJob = !InHub && active != null;
        if (inJob && active.id == pending.mission)
        {
            lastFollowed = pending.ticket;   // already in that job (e.g. both started it by hand)
            return;
        }
        if (w.Player == null || !w.Player.CanMove) return;
        if (!InHub && !inJob || InIntro) return;   // menus, loading, a new game's prologue
        if (!AnyJob && !OwnJobs().Contains(pending.mission))
        {
            // Not followed until this save has the job (the player may still accept it on the laptop).
            if (warnedTicket != pending.ticket)
            {
                warnedTicket = pending.ticket;
                Plugin.L.LogInfo($"COOP the host is in job {pending.mission} '{Name(pending.mission)}', which this save doesn't have: staying");
                CoopMenu.Toast($"Хост на заказе «{Name(pending.mission)}», которого у тебя ещё нет: поспи, ответь на телефон и прими письмо на ноутбуке, и поедешь к нему.", 10);
            }
            return;
        }
        if (inJob) Plugin.L.LogInfo($"COOP in job {active.id}, the host is in job {pending.mission}: switching");
        var m = MissionController.Instance?.GetMission(pending.mission);
        if (m == null) { Plugin.L.LogWarning($"COOP job {pending.mission} unknown in this save"); lastFollowed = pending.ticket; return; }
        lastFollowed = pending.ticket;
        Plugin.L.LogInfo($"COOP following the host into job {pending.mission} '{m.displayName}'");
        WorldEvents.Applying = true;
        try { gm.StartMission(m, pending.variant); }
        finally { WorldEvents.Applying = false; }
    }

    [HarmonyPatch(typeof(MissionLeaveController), nameof(MissionLeaveController.LeaveMissionWithReport))]
    [HarmonyPrefix]
    static void Leave()
    {
        if (!WorldEvents.Live) return;   // also false while applying someone else's leave
        Plugin.L.LogInfo("COOP leaving the job: everyone leaves");
        WorldEvents.Send(Ev.Leave, w => w.Write((byte)0));   // sent before the scene starts to end
    }

    public static void OnLeave()
    {
        var c = Object.FindObjectOfType<MissionLeaveController>();
        if (c == null) return;
        Plugin.L.LogInfo("COOP another player left the job: leaving too");
        c.LeaveMissionWithReport();
        Left++;
    }
}
