using System;
using System.IO;
using CoopCore;

namespace CscCoop;

// Event kinds carried by Session.SendEvent. One byte each; payload layouts live with their handlers.
public enum Ev : byte
{
    StainIntensity = 1,   // state: taint index, intensity (only ever goes down)
    StainDestroy = 2,     // state: taint index
    StainHits = 3,        // stream: batch of UV paint calls (decal index + PaintTexture arguments)
    BodyBones = 4,        // state per body: local pose of every bone, from the body's owner
    VanCollect = 5,       // state per van item: GarbageItem collected
    DoorPlain = 6,        // state per Door: isClosed
    DoorUnlock = 7,       // state per ShortcutDoors: unlocked
    DoorShortcut = 8,     // state per ShortcutDoors: open / closed
    KeyCollect = 9,       // state per DoorKey: collected (everyone gets the key)
    EvidenceCollect = 10, // state per InspectableItem: collected
    Cassette = 11,        // state per MusicCasette: unlocked
    Secret = 12,          // state per SecretArea: discovered
    Steal = 13,           // state per StealableItem: stolen (goes on everyone's report)
    Crack = 14,           // state per DestroyableItem: cracked, with the hit velocity
    BreakObject = 15,     // state per DestroyableObject: destroyed
    JobTicket = 16,       // global state: the host started a job (ticket, mission id, variant)
    HostJobs = 30,        // global state: the jobs the host has accepted on its laptop (mission ids)
    MyJobs = 31,          // global state per client: the jobs its own save can start (accepted or done)
    Leave = 17,           // stream: someone left the job, everyone leaves
    DynState = 18,        // state per dynamic object (bag, tool): kind, prefab, pose, scale, alive
    DynTake = 19,         // stream: a player took over a bag
    TrashCollect = 20,    // state per CollectableItem: picked up by a trash can
    Quest = 21,           // state per quest / quest entry: Dialogue System state string
    StainNew = 22,        // state per stain made during the job: how to make it and where it ended up
    Openable = 23,        // state per Openable (drawer, cabinet, room door): open
    Lift = 24,            // state per MovingPlatform: up or down
    PartCrack = 25,       // state per CrackedObject: cracked, with the hit direction
    Furniture = 26,       // state per furniture hologram: a piece fixed on it (or taken off)
    Action = 27,          // stream: a whitelisted world action replayed (Actions)
    PartCollect = 28,     // state per CrackedObject part: picked up by a trash can
    Heavy = 29,           // state per HeavyItemController: being pushed (10 Hz) or put down, with the pose
    LuaVar = 32,          // state per Dialogue System variable: counters, locations discovered, story flags (Objectives)
    ObjectiveCount = 33,  // unused (0.4.0 test build): objective counts crossing counted things twice
    ObjectivePercent = 34,// unused (0.4.0 test build): see Objectives
    ObjectiveAdd = 35,    // state per objective added during the job: name, location, localized text
    JobRestart = 36,      // stream: a player restarted the job, everyone restarts
    Report = 37,          // global stream: a player pressed F9, everyone saves a screenshot and state (Reports)
    ReportNote = 38,      // global stream: the comment typed for that report
    TableHashes = 39,     // global state per player: its job's table fingerprints (TableCheck)
    CctvHack = 40,        // state per CCTV laptop: hacked (Cctv)
    CctvDelete = 41,      // state per CCTV laptop and recording: deleted
    BotStep = 99,         // tests only: the other bot's step (lockstep, see Bot)
}

// Sends world events and routes received ones. Applying a received event runs game code that our own
// patches watch; `Applying` tells them it is remote, so nothing echoes back.
public static class WorldEvents
{
    public static bool Applying;
    static readonly MemoryStream buf = new();

    public static Session S => CoopRunner.Instance?.Session;
    public static bool Live => S != null && S.Synced && !Applying;
    // In a hub each player is in their own apartment (their own save): only poses go between them, no world
    // events either way, so nothing of one player's apartment ends up in the other's save.
    static bool InHub => CoopRunner.Instance != null && CoopRunner.Instance.World.InHub;
    static bool Global(Ev k) => k is Ev.JobTicket or Ev.HostJobs or Ev.MyJobs or Ev.BotStep or Ev.Report or Ev.ReportNote or Ev.TableHashes;

    public static void Send(Ev kind, Action<BinaryWriter> write, uint stateKey = 0)
    {
        var s = S;
        if (s == null || !s.Synced || InHub && !Global(kind)) return;
        buf.SetLength(0);
        var w = new BinaryWriter(buf);
        write(w);
        w.Flush();
        Watchdog.Where = $"sending event {kind}";
        s.SendEvent((byte)kind, buf.GetBuffer(), (int)buf.Length, stateKey);
        Watchdog.Where = $"sent event {kind}";
    }

    // For everyone connected, whatever scene they are in (Session global events).
    public static void SendGlobal(Ev kind, Action<BinaryWriter> write, uint stateKey = 0)
    {
        var s = S;
        if (s == null) return;
        buf.SetLength(0);
        var w = new BinaryWriter(buf);
        write(w);
        w.Flush();
        s.SendEvent((byte)kind, buf.GetBuffer(), (int)buf.Length, stateKey, global: true);
    }

    public static void Dispatch(byte from, byte kind, BinaryReader r)
    {
        Watchdog.Where = $"applying event {(Ev)kind} from {from}";
        if (InHub && !Global((Ev)kind)) { Plugin.L.LogInfo($"COOP event {(Ev)kind} from {from} ignored in the hub"); return; }
        Applying = true;
        try
        {
            switch ((Ev)kind)
            {
                case Ev.StainIntensity: Stains.OnIntensity(r); break;
                case Ev.StainDestroy: Stains.OnDestroy(r); break;
                case Ev.StainHits: Stains.OnHits(r); break;
                case Ev.StainNew: Stains.OnNew(r); break;
                case Ev.BodyBones: Bodies.OnBones(r); break;
                case Ev.VanCollect: Bodies.OnCollect(r); break;
                case Ev.DoorPlain: Doors.OnPlain(r); break;
                case Ev.DoorUnlock: Doors.OnUnlock(r); break;
                case Ev.DoorShortcut: Doors.OnShortcut(r); break;
                case Ev.KeyCollect: Doors.OnKey(r); break;
                case Ev.EvidenceCollect: Doors.OnEvidence(r); break;
                case Ev.JobTicket: Jobs.OnTicket(r); break;
                case Ev.HostJobs: Jobs.OnHostJobs(r); break;
                case Ev.MyJobs: Jobs.OnClientJobs(from, r); break;
                case Ev.DynState: Dyn.OnState(r); break;
                case Ev.DynTake: Dyn.OnTake(from, r); break;
                case Ev.TrashCollect: Trash.OnCollect(r); break;
                case Ev.Quest: Quests.OnQuest(r); break;
                case Ev.LuaVar: Objectives.OnVar(r); break;
                case Ev.ObjectiveAdd: Objectives.OnAdd(r); break;
                case Ev.JobRestart: Objectives.OnRestart(r); break;
                case Ev.Report: Reports.OnReport(r); break;
                case Ev.TableHashes: TableCheck.OnHashes(from, r); break;
                case Ev.CctvHack: case Ev.CctvDelete: Cctv.Apply((Ev)kind, r); break;
                case Ev.ReportNote: Reports.OnNote(r); break;
                case Ev.BotStep: Bot.PartnerStep = r.ReadInt32(); break;
                case Ev.Action: Actions.Apply(r); break;
                case Ev.PartCollect: Trash.OnPartCollect(r); break;
                case Ev.Openable: case Ev.Lift: case Ev.PartCrack: case Ev.Furniture: case Ev.Heavy: Furniture.Apply((Ev)kind, r); break;
                case Ev.Leave: Jobs.OnLeave(); break;
                case Ev.Cassette: case Ev.Secret: case Ev.Steal: case Ev.Crack: case Ev.BreakObject: Collectibles.Apply((Ev)kind, r); break;
                default: Plugin.L.LogWarning($"COOP unknown event {kind} from {from}"); break;
            }
        }
        catch (Exception e) { Plugin.L.LogError($"COOP event {(Ev)kind} from {from} failed: {e}"); }
        finally { Applying = false; }
    }
}
