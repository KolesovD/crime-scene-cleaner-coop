using System;
using System.Collections.Generic;
using CoopCore;
using CscCoop.Net;
using UnityEngine;

namespace CscCoop;

// Owns the session: starts it, ticks it every frame, drives remote items and draws other players.
// Tests start it from env (CSCCOOP_ROLE=host|client, CSCCOOP_PORT, CSCCOOP_HOST = host's port).
public class CoopRunner : MonoBehaviour
{
    public CoopRunner(IntPtr p) : base(p) { }

    public static CoopRunner Instance;
    public readonly GameWorld World = new();
    public Session Session;
    ITransport transport;
    readonly Dictionary<byte, PlayerFigure> ghosts = new();
    public PlayerFigure Ghost;   // tests: the last other player drawn
    float nextStatus, nextAccept;
    public SteamDirectory Steam;
    SteamTransport steamTransport;
    public string Error = "";
    readonly System.Diagnostics.Stopwatch prof = new();
    ulong hostId;

    // Test and manual-test runs: the role comes from CSCCOOP_ROLE (localhost UDP), not from the panel.
    public bool TestMode => Env.Get("CSCCOOP_ROLE") is "host" or "client";

    void Awake()
    {
        Instance = this;
        Doors.Register(World);
        Collectibles.Register(World);
        Trash.Register(World);
        Furniture.Register(World);
        Cctv.Register(World);
        if (Env.Get("CSCCOOP_VIA_MENU") != "1") StartFromEnv();
    }

    public void StartFromEnv()
    {
        string role = Env.Get("CSCCOOP_ROLE");
        if (role == "host" || role == "client")
        {
            var udp = new UdpTransport(Env.Int("CSCCOOP_PORT", role == "host" ? 27901 : 27902))
            {
                LossUnreliable = Env.Int("CSCCOOP_LOSS_PCT", 0) / 100f,
                LatencyMs = Env.Int("CSCCOOP_LATENCY_MS", 0),
            };
            Begin(role == "host", udp, (ulong)Env.Int("CSCCOOP_HOST", 27901));
        }
    }

    public void Begin(bool host, ITransport t, ulong hostId)
    {
        Stop();
        transport = t;
        this.hostId = hostId;
        Session = new Session(host, t, World, hostId) { Log = m => Plugin.L.LogInfo("COOP " + m), OnEvent = WorldEvents.Dispatch };
        Plugin.L.LogInfo($"COOP session started as {(host ? "host" : "client")}, local id {t.LocalId}");
    }

    // Real play: Steam networking between friends, found through rich presence (SteamDirectory).
    public void HostSteam()
    {
        if (!SteamReady()) return;
        steamTransport = new SteamTransport();
        Begin(true, steamTransport, steamTransport.LocalId);
        Steam.Announce("host");
    }

    public void JoinSteam(ulong host)
    {
        if (!SteamReady()) return;
        steamTransport = new SteamTransport();
        Begin(false, steamTransport, host);
        Steam.Announce("join", host);
    }

    bool SteamReady()
    {
        try { Steam ??= new SteamDirectory(); Error = ""; return true; }
        catch (Exception e) { Error = "Steam not available: " + e.Message; Plugin.L.LogError("COOP " + Error); return false; }
    }

    // Host: open the session with every friend whose presence asks to join us, until we hear their Hello.
    // Accepting once was not enough (friend's two-account test: host "accepted", then rx 0 while the client
    // sent): AcceptSessionWithUser only works while their request is pending, and the host never sent them
    // anything. Sending to a user also accepts their session, so the host pings them every 2 s and retries.
    static readonly byte[] ping = { 0xC0, 0xC5, 9, 0 };   // Wire.Magic little-endian, Msg.Ping, from host
    readonly Dictionary<ulong, float> nextInviteLog = new();
    void AcceptJoiners()
    {
        if (steamTransport == null || !Session.IsHost || Time.unscaledTime < nextAccept) return;
        nextAccept = Time.unscaledTime + 2;
        foreach (var f in Steam.Joiners())
        {
            if (Session.HasPeer(f.Id)) continue;
            int st = steamTransport.State(f.Id);
            if (SteamTransport.Dead(st)) { steamTransport.Reset(f.Id); Plugin.L.LogInfo($"COOP steam session with {f.Name} was {SteamTransport.StateName(st)}: reset"); }
            bool acc = steamTransport.Accept(f.Id);
            steamTransport.Send(f.Id, ping, ping.Length, true);
            if (!nextInviteLog.TryGetValue(f.Id, out var t) || Time.unscaledTime >= t)
            {
                nextInviteLog[f.Id] = Time.unscaledTime + 10;
                Plugin.L.LogInfo($"COOP inviting friend {f.Name}: accept {acc}, steam session {SteamTransport.StateName(steamTransport.State(f.Id))}, send errors {steamTransport.SendErrors}");
            }
        }
    }

    // Client: while not connected, a dead session to the host is closed so the next Hello opens a new one.
    float nextHostCheck;
    void ResetDeadHostSession()
    {
        if (steamTransport == null || Session.IsHost || Session.Me != Session.NoPlayer && Session.Synced) return;
        if (Time.unscaledTime < nextHostCheck) return;
        nextHostCheck = Time.unscaledTime + 3;
        int st = steamTransport.State(hostId);
        if (!SteamTransport.Dead(st)) return;
        steamTransport.Reset(hostId);
        Plugin.L.LogInfo($"COOP steam session to host was {SteamTransport.StateName(st)}: reset, retrying");
    }

    // Short state for the panel and the corner label.
    public string ConnectionText()
    {
        if (Session == null) return "кооп выключен";
        if (Session.IsHost)
            return Session.Players.Count == 0 ? "хост: ждём друзей" : $"хост, игроков: {Session.Players.Count + 1}" + (Session.Synced ? "" : ", ждём один заказ");
        if (Session.Me == Session.NoPlayer)
        {
            if (steamTransport == null) return "подключаемся…";
            int st = steamTransport.State(hostId);
            return st == 3 ? "соединено, ждём ответ хоста…" : SteamTransport.Dead(st) ? "Steam не соединил, пробую ещё раз…" : "подключаемся через Steam…";
        }
        bool hostHere = Session.Players.TryGetValue(Session.HostNumber, out var h) && h.SceneKey == World.SceneKey && World.SceneKey != 0;
        return $"игрок {Session.Me}, игроков: {Session.Players.Count + 1}" + (Session.Synced && hostHere ? (World.InHub ? ", вместе в квартире" : "")
            : World.InHub ? ", хост не в этой квартире (на заказе или в хабе другого акта)" : ", ждём один заказ");
    }

    // ", пинг 42 мс" (client: to the host; host: each player's).
    public string PingText()
    {
        var s = Session;
        if (s == null) return "";
        if (!s.IsHost) return s.RttMs > 0 ? $", пинг {s.RttMs:F0} мс" : "";
        var parts = new List<string>();
        foreach (var p in s.Players.Values) if (p.RttMs > 0) parts.Add(s.Players.Count == 1 ? $"{p.RttMs:F0} мс" : $"{p.Number}: {p.RttMs:F0} мс");
        return parts.Count == 0 ? "" : ", пинг " + string.Join(", ", parts);
    }

    // A line per other player for the panel: where they are and their ping.
    public string PlayerLine(CoopCore.RemotePlayer p)
    {
        bool here = p.SceneKey == World.SceneKey && p.SceneKey != 0;
        float ms = Session.IsHost ? p.RttMs : p.Number == CoopCore.Session.HostNumber ? Session.RttMs : 0;
        return $"{(p.Number == CoopCore.Session.HostNumber ? "хост" : $"игрок {p.Number}")}{(here ? "" : " (в другом месте)")}{(ms > 0 ? $", пинг {ms:F0} мс" : "")}";
    }

    public void Stop()
    {
        if (Session == null) return;
        Plugin.L.LogInfo($"COOP session stopped ({(Session.IsHost ? "host" : "client")})");
        if (steamTransport != null) { Steam?.Announce(""); steamTransport = null; nextInviteLog.Clear(); }
        Session.Close();
        transport.Dispose();
        Session = null; transport = null;
        foreach (var g in ghosts.Values) g?.Destroy();
        ghosts.Clear();
    }

    void LateUpdate() => HandsOff.FreeCursor();

    void Update()
    {
        Watchdog.Frame(); Watchdog.Where = "frame start";
        World.Refresh();
        IntroSkip.Tick(World);
        WindowPlacer.Tick();
        Quiet.Tick();
        HandsOff.Tick();
        if (Session == null) return;
        // An unfocused Unity window pauses (no Update, no network): an alt-tabbed player froze the session for
        // everyone, and in the two-window test the unfocused copy stood still (world2 suite: the host's steps ran
        // 70 s late; owner's manual test: positions from the other window arrived late).
        if (!Application.runInBackground && Env.Get("CSCCOOP_NO_BG") != "1") { Application.runInBackground = true; Plugin.L.LogInfo("COOP game keeps running when not focused"); }
        // Per-part timing; a frame where the mod takes > 50 ms is logged with the breakdown.
        prof.Restart(); long t0, t1, t2, t3, t4, t5;
        try
        {
            Watchdog.Where = "track"; World.Track(); t0 = prof.ElapsedTicks;
            Watchdog.Where = "session"; AcceptJoiners(); ResetDeadHostSession(); Session.Tick(CoopCore.Clock.Now); t1 = prof.ElapsedTicks;
            bool job = !World.InHub;   // in a hub only poses are shared (WorldEvents)
            Watchdog.Where = "stains/bodies"; if (job) { Stains.Tick(); Bodies.Tick(); } t2 = prof.ElapsedTicks;
            Watchdog.Where = "jobs/dyn"; Jobs.Tick(World); if (job) { Dyn.Tick(); Furniture.Tick(); TableCheck.Tick(World); Cctv.Tick(); } t3 = prof.ElapsedTicks;
        }
        catch (Exception e) { Plugin.L.LogError($"COOP tick failed: {e}"); t0 = t1 = t2 = t3 = prof.ElapsedTicks; }
        Watchdog.Where = "smooth"; World.Smooth(Time.deltaTime); t4 = prof.ElapsedTicks;
        Watchdog.Where = "ghosts"; DrawGhosts(); t5 = prof.ElapsedTicks;
        Watchdog.Where = "after mod update";
        double ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (ms(0, t5) > 50 || Time.unscaledDeltaTime > 2)
            Plugin.L.LogWarning($"COOP slow frame: mod {ms(0, t5):F0} ms (track {ms(0, t0):F0}, session {ms(t0, t1):F0}, stains+bodies {ms(t1, t2):F0}, jobs+dyn {ms(t2, t3):F0}, smooth {ms(t3, t4):F0}, ghosts {ms(t4, t5):F0}), frame {Time.unscaledDeltaTime * 1000:F0} ms");
        if (Time.unscaledTime >= nextStatus)
        {
            nextStatus = Time.unscaledTime + 10;
            string steam = steamTransport == null ? "" : Session.IsHost ? $", send errors {steamTransport.SendErrors}"
                : $", steam session to host {SteamTransport.StateName(steamTransport.State(hostId))}, send errors {steamTransport.SendErrors}";
            Plugin.L.LogInfo($"COOP status: me {Session.Me}, synced {Session.Synced}, players {Session.Players.Count}, rx {Session.Rx}, tx {Session.Tx}, dropped {Session.Dropped}{steam}, ping '{PingText()}', scene {World.Scene}, frame {Time.frameCount}, t {Time.unscaledTime:F0}, focus {Application.isFocused}, timeScale {Time.timeScale}");
        }
    }

    void OnApplicationQuit() => Stop();

    // Other players (PlayerFigure), 100 ms in the past, interpolated; a body one of them carries goes on its shoulder.
    void DrawGhosts()
    {
        double now = CoopCore.Clock.Now;
        Bodies.BeginShoulders();
        foreach (var rp in Session.Players.Values)
        {
            bool show = rp.SceneKey != 0 && rp.SceneKey == World.SceneKey && rp.Sample(now, 0.1, out var pose);
            if (!ghosts.TryGetValue(rp.Number, out var g) || g == null || g.Root == null)
            {
                if (!show) continue;
                ghosts[rp.Number] = g = new PlayerFigure(rp.Number, colors[rp.Number % colors.Length]);
            }
            g.SetActive(show);
            if (!show) continue;
            rp.Sample(now, 0.1, out pose);
            int held = pose.HeldItem;
            bool body = held >= World.Items.Count && held < World.ItemCount && Session.OwnerOf(held) == rp.Number && World.IsRemoteDriven(held);
            var carry = body ? PlayerFigure.Carry.Body : held >= 0 ? PlayerFigure.Carry.Item : PlayerFigure.Carry.None;
            g.Pose(GameWorld.U(pose.Pos), pose.Yaw, pose.Pitch, carry, pose.Tool, GameWorld.U(pose.ToolPos), GameWorld.U(pose.ToolRot), pose.ToolScale,
                (pose.Flags & CoopCore.PlayerPose.PoseUsing) != 0, Time.deltaTime);
            if (body) Bodies.Shoulder(held, rp.Number, g);
            Ghost = g;
        }
        foreach (var kv in ghosts) if (!Session.Players.ContainsKey(kv.Key) && kv.Value?.Root != null) kv.Value.SetActive(false);
    }

    static readonly Color[] colors = { Color.cyan, new(1, 0.5f, 0), Color.magenta, Color.green };
}
