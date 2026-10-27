using System;
using System.Collections.Generic;
using System.IO;
using CoopCore;

// Game-free checks of the sync core: fake items with toy physics, 1 host + 3 clients on a
// lossy in-memory network. Exit code 0 only if every check passes.
// `--mutant <name>` breaks one rule on purpose; that run must FAIL (the test catches it).
static class Program
{
    static int pass, fail;
    public static string Mutant = "";

    static void Check(bool ok, string what)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    }

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--mutant") Mutant = Session.Mutant = args[1];
        if (Mutant != "") Console.WriteLine($"mutant: {Mutant}");
        Codec();
        Interpolation();
        Scenario(seed: 1, loss: 0.2f);
        Scenario(seed: 7, loss: 0.35f);
        Console.WriteLine($"RESULT: {(fail == 0 ? "PASS" : "FAIL")} ({pass} passed, {fail} failed)");
        return fail == 0 ? 0 : 1;
    }

    static void Codec()
    {
        Check(Wire.Open(new byte[] { 1, 2, 3, 4, 5 }, out _, out _) == null, "codec: foreign packet rejected");
        Check(Wire.Open(new byte[] { 1 }, out _, out _) == null, "codec: short packet rejected");
        var ms = new MemoryStream();
        var w = Wire.Begin(ms, Msg.Pose, 2); w.Write(new V3(1, 2, 3));
        var r = Wire.Open(ms.ToArray(), out var id, out var from);
        var v = r.ReadV3();
        Check(id == Msg.Pose && from == 2 && v.X == 1 && v.Z == 3, "codec: round trip");
        Check(Wire.Newer(1, 65535) && !Wire.Newer(65535, 1) && !Wire.Newer(5, 5), "codec: sequence wraparound");
    }

    static void Interpolation()
    {
        var rp = new RemotePlayer();
        rp.Push(0.0, 1, new PlayerPose { Pos = new V3(0, 0, 0), Yaw = 350 });
        rp.Push(0.1, 2, new PlayerPose { Pos = new V3(1, 0, 0), Yaw = 10 });
        Check(!rp.Push(0.12, 1, new PlayerPose { Pos = new V3(9, 9, 9) }), "pose: stale sequence ignored");
        rp.Sample(0.15, 0.1, out var p);
        Check(MathF.Abs(p.Pos.X - 0.5f) < 1e-3f, $"pose: halfway position ({p.Pos.X:F3})");
        float yaw = ((p.Yaw % 360) + 360) % 360;
        Check(MathF.Abs(yaw) < 1e-3f || MathF.Abs(yaw - 360) < 1e-3f, $"pose: yaw wraps through 0 ({p.Yaw:F1})");
    }

    sealed class Peer
    {
        public FakeWorld World;
        public Session S;
        public ITransport T;
        public string Name;
        public readonly List<string> Events = new();
    }

    static void Scenario(int seed, float loss)
    {
        Console.WriteLine($"-- scenario seed {seed}, {loss:P0} loss of unreliable packets");
        var net = new FakeNet(seed) { Loss = loss };
        const int Items = 50;
        const uint Scene = 0xABCD1234;
        var peers = new List<Peer>();
        Peer Make(string name, ulong id, bool host)
        {
            var w = new FakeWorld(Items, seed) { Key = Scene };
            var t = net.Join(id);
            var s = new Session(host, t, w, hostId: 100) { Log = m => { if (Environment.GetEnvironmentVariable("CORE_LOG") == "1") Console.WriteLine($"   [{name}] {m}"); } };
            var p = new Peer { World = w, S = s, T = t, Name = name };
            s.OnEvent = (from, kind, r) => p.Events.Add($"{from}:{kind}:{r.ReadString()}");
            peers.Add(p);
            return p;
        }
        var host = Make("host", 100, true);
        var c1 = Make("c1", 101, false);
        var c2 = Make("c2", 102, false);
        var c3 = Make("c3", 103, false);

        void Run(double seconds, Action<double> each = null)
        {
            for (double t = 0; t < seconds; t += 1 / 60.0)
            {
                net.Advance(1 / 60.0);
                each?.Invoke(net.Now);
                foreach (var p in peers) { p.World.Step(1 / 60.0); p.S.Tick(net.Now); }
            }
        }

        Run(3);
        Check(c1.S.Synced && c2.S.Synced && c3.S.Synced, "join: all clients synced");
        Check(c1.S.Me == 1 && c2.S.Me == 2 && c3.S.Me == 3, $"join: player numbers 1,2,3 ({c1.S.Me},{c2.S.Me},{c3.S.Me})");

        // Every machine sees every other player near their real position.
        foreach (var p in peers) p.World.Tool = p.S.Me == 2 ? PlayerPose.ToolNone : (short)(20 + p.S.Me);
        Run(2, t => { foreach (var p in peers) p.World.Player = new V3(p.S.Me * 3 + MathF.Sin((float)t), 0, MathF.Cos((float)t)); });
        bool posesOk = true, toolsOk = true;
        foreach (var a in peers)
            foreach (var b in peers)
            {
                if (a == b) continue;
                if (!a.S.Players.TryGetValue(b.S.Me, out var rp) || !rp.Sample(net.Now, 0.1, out var pose)) { posesOk = false; continue; }
                if (pose.Pos.DistanceTo(b.World.Player) > 0.5f) posesOk = false;
                bool has = b.World.Tool >= 0;
                if (pose.Tool != b.World.Tool || (pose.Flags == PlayerPose.PoseUsing) != has || has && MathF.Abs(pose.ToolPos.Z - b.World.Tool) > 1e-3f) toolsOk = false;
            }
        Check(posesOk, "poses: every player sees the other three within 0.5 m");
        Check(toolsOk, "poses: equipped tool, its pose and the use flag arrive (and 'no tool' stays none)");

        // Each client grabs its own item, carries it 2 m up and sideways, then drops it.
        var picks = new[] { (c1, 5), (c2, 6), (c3, 7) };
        foreach (var (c, i) in picks) Check(c.World.Grab(c.S, i), $"grab: {c.Name} grabs item {i}");
        Run(1, t => { foreach (var (c, i) in picks) c.World.Hand = new V3(c.S.Me * 10, 2, 5); });
        bool heldSeen = true;
        foreach (var (c, i) in picks)
            foreach (var p in peers) if (p.World.Items[i].Pos.DistanceTo(new V3(c.S.Me * 10, 2, 5)) > 0.6f) heldSeen = false;
        Check(heldSeen, "carry: everyone sees carried items at the carrier's hand (< 0.6 m)");
        Check(host.S.OwnerOf(5) == 1 && host.S.OwnerOf(6) == 2 && host.S.OwnerOf(7) == 3, "carry: host table shows the carriers as owners");
        foreach (var (c, i) in picks) c.World.Release(i, new V3(1, 3, 0));   // a throw
        Run(4);
        bool settled = true;
        foreach (var (c, i) in picks)
            foreach (var p in peers) if (p.World.Items[i].Pos.DistanceTo(c.World.Items[i].Pos) > 0.01f) settled = false;
        Check(settled, "throw: every replica ends where the thrower's physics put it (< 1 cm)");
        Check(host.S.OwnerOf(5) == 0 && host.S.OwnerOf(6) == 0 && host.S.OwnerOf(7) == 0, "throw: resting items went back to the host");
        Check(Agree(peers, i => peers[0].S.EpochOf(i)), "throw: all four tables agree on owner and epoch");

        // Two clients grab the same item in the same frame: exactly one keeps it.
        c1.World.Grab(c1.S, 10); c2.World.Grab(c2.S, 10);
        Run(1);
        int holders = (c1.World.Items[10].HeldHere ? 1 : 0) + (c2.World.Items[10].HeldHere ? 1 : 0);
        Check(holders == 1, $"conflict: one of two simultaneous grabs wins ({holders} holding)");
        byte winner = host.S.OwnerOf(10);
        var winPeer = winner == 1 ? c1 : c2;
        Check(winner == 1 || winner == 2, $"conflict: host gave it to a grabber (owner {winner})");
        Check(winPeer.World.Items[10].HeldHere, "conflict: the owner is the one holding it");

        // The host can't take an item out of a client's hands.
        Check(!host.World.Grab(host.S, 10), "conflict: host can't grab an item a client holds");
        winPeer.World.Release(10, default);
        Run(3);
        Check(host.S.OwnerOf(10) == 0, "conflict: released item back with the host");
        Check(host.World.Grab(host.S, 10), "conflict: host can grab it once it is free");
        host.World.Hand = new V3(-5, 1, -5);
        Run(1);
        bool hostCarry = true;
        foreach (var p in peers) if (p.World.Items[10].Pos.DistanceTo(new V3(-5, 1, -5)) > 0.6f) hostCarry = false;
        Check(hostCarry, "host carry: clients see the host's carried item");
        host.World.Release(10, default);
        Run(2);

        // A replayed old ownership statement changes nothing (rule 5).
        ushort epBefore = c3.S.EpochOf(5);
        var ms = new MemoryStream();
        var w = Wire.Begin(ms, Msg.ItemOwner, 0);
        w.Write(Scene); w.Write((ushort)1); w.Write((ushort)5); w.Write((byte)1); w.Write((ushort)(epBefore - 1));
        net.Inject(100, c3.T.LocalId, ms.ToArray());   // as if the host had re-sent it
        Run(0.5);
        Check(c3.S.OwnerOf(5) == 0 && c3.S.EpochOf(5) == epBefore, "stale: replayed old owner message ignored");

        // A client leaves while holding an item: it goes back to the host.
        c3.World.Grab(c3.S, 20);
        c3.World.Hand = new V3(0, 3, 0);
        Run(1);
        Check(host.S.OwnerOf(20) == 3, "leave: c3 owns the item it holds");
        c3.S.Close(); peers.Remove(c3); c3.T.Dispose();
        Run(1);
        Check(host.S.OwnerOf(20) == 0 && c1.S.OwnerOf(20) == 0, "leave: leaver's item returned to the host");
        Check(!host.S.Players.ContainsKey(3), "leave: host forgot player 3");

        // World events: everyone but the sender gets them; state events are kept for late joiners.
        void Ev(Peer p, byte kind, string text, uint key)
        {
            var b = new MemoryStream(); new BinaryWriter(b).Write(text);
            p.S.SendEvent(kind, b.ToArray(), (int)b.Length, key);
        }
        foreach (var p in peers) p.Events.Clear();
        Ev(c1, 1, "door7-open", 7);
        Ev(c2, 2, "hit-stream", 0);
        Ev(host, 1, "door8-open", 8);
        Ev(c1, 1, "door7-closed", 7);   // replaces the first state of door 7
        Run(1);
        Check(host.Events.Count == 3 && c2.Events.Contains("1:1:door7-open") && c2.Events.Contains("0:1:door8-open")
              && c1.Events.Contains("2:2:hit-stream") && !c1.Events.Exists(e => e.StartsWith("1:")),
              $"events: delivered to all but the sender, sender stamped by host (host {host.Events.Count}, c1 [{string.Join(" ", c1.Events)}])");
        Check(c2.Events.IndexOf("1:1:door7-open") < c2.Events.IndexOf("1:1:door7-closed"), "events: order kept");

        // A late joiner in the same scene gets the current item positions.
        var late = Make("late", 104, false);
        Run(3);
        Check(late.S.Synced && late.S.Me == 3, $"late join: synced, reuses free number ({late.S.Me})");
        bool lateOk = true;
        for (int i = 0; i < Items; i++) if (late.World.Items[i].Pos.DistanceTo(host.World.Items[i].Pos) > 0.01f) { lateOk = false; Console.WriteLine($"   item {i}: late {late.World.Items[i].Pos} host {host.World.Items[i].Pos} owner {host.S.OwnerOf(i)} local {late.World.Items[i].Local}"); }
        Check(lateOk, "late join: all 50 items where the host has them");
        Check(late.Events.Count == 2 && late.Events.Contains("0:1:door8-open") && late.Events.Contains("1:1:door7-closed"),
              $"late join: newest state events replayed, stream events not ([{string.Join(" ", late.Events)}])");

        // Someone in another scene gets no items.
        var other = Make("other", 105, false);
        other.World.Key = 0x11111111;
        var before = other.World.Items[5].Pos;
        Run(2);
        Check(!other.S.Synced && other.World.Items[5].Pos.DistanceTo(before) < 1e-4f, "scene: client in another scene isn't synced");
        Ev(host, 1, "door9-open", 9);
        Run(1);
        Check(other.Events.Count == 0 && late.Events.Contains("0:1:door9-open"), "events: only players in the same scene get them");

        // Global events reach everyone connected, whatever their scene, and are replayed to newcomers.
        // The lobby is full (host + 3), so c2 moves to another scene and later makes room for a newcomer.
        c2.World.Key = 0x33333333;
        Run(1);
        foreach (var p in peers) p.Events.Clear();
        {
            var b = new MemoryStream(); new BinaryWriter(b).Write("job-3");
            host.S.SendEvent(20, b.ToArray(), (int)b.Length, 1, global: true);
            b = new MemoryStream(); new BinaryWriter(b).Write("job-4");
            host.S.SendEvent(20, b.ToArray(), (int)b.Length, 1, global: true);   // replaces job-3 for newcomers
        }
        Run(1);
        Check(c2.Events.Contains("0:20:job-3") && c2.Events.Contains("0:20:job-4") && c1.Events.Contains("0:20:job-4"),
              $"global: players in other scenes get it too (c2 [{string.Join(" ", c2.Events)}])");
        Check(!other.S.Synced && other.Events.Count == 0, "lobby: a fifth player is turned away");
        other.S.Close(); peers.Remove(other); other.T.Dispose();   // it retries Hello and would take the free slot
        c2.S.Close(); peers.Remove(c2); c2.T.Dispose();
        Run(1);
        var newcomer = Make("newcomer", 106, false);
        newcomer.World.Key = 0x22222222;
        Run(2);
        Check(newcomer.Events.Count == 1 && newcomer.Events[0] == "0:20:job-4", $"global: newest state replayed to a newcomer in another scene ([{string.Join(" ", newcomer.Events)}])");

        // Final: everyone in the scene agrees on every item.
        bool allAgree = true;
        foreach (var p in peers)
        {
            if (p == other || p == newcomer) continue;   // other: never got in (full); newcomer: in another scene
            for (int i = 0; i < Items; i++) if (p.World.Items[i].Pos.DistanceTo(host.World.Items[i].Pos) > 0.01f) allAgree = false;
        }
        Check(allAgree, "final: all replicas agree on all items");
        // Round trip: the fake net delays every packet 30 ms (+ up to 30 ms jitter), ticks are 1/60 s.
        float hostRtt = host.S.Players.TryGetValue(c1.S.Me, out var c1p) ? c1p.RttMs : 0;
        Check(c1.S.RttMs > 50 && c1.S.RttMs < 160 && hostRtt > 50 && hostRtt < 160, $"rtt: measured on both sides (client {c1.S.RttMs:F0} ms, host {hostRtt:F0} ms)");
        Check(host.S.Dropped == 0, $"final: host dropped no malformed packets ({host.S.Dropped})");
    }

    static bool Agree(List<Peer> peers, Func<int, ushort> _)
    {
        for (int i = 0; i < 50; i++)
            foreach (var p in peers)
                if (p.S.OwnerOf(i) != peers[0].S.OwnerOf(i) || p.S.EpochOf(i) != peers[0].S.EpochOf(i)) return false;
        return true;
    }
}

sealed class FakeItem
{
    public V3 Pos, Vel;
    public bool HeldHere, Local = true, Sleeping = true;
}

sealed class FakeWorld : IWorld
{
    public uint Key;
    public V3 Player, Hand;
    public FakeItem[] Items;

    public FakeWorld(int n, int seed)
    {
        Items = new FakeItem[n];
        for (int i = 0; i < n; i++) Items[i] = new FakeItem { Pos = new V3(i, 0, 0) };
    }

    public short Tool = PlayerPose.ToolNone;
    public bool LocalPose(out PlayerPose pose)
    {
        pose = new PlayerPose { Pos = Player, HeldItem = -1, Tool = Tool, Flags = (byte)(Tool >= 0 ? PlayerPose.PoseUsing : 0), ToolPos = new V3(0.3f, 1.1f, Tool), ToolRot = Q4.Identity, ToolScale = 1 };
        return true;
    }
    public uint SceneKey => Key;
    public int ItemCount => Items.Length;

    public ItemSample ReadItem(int i) => new() { Pos = Items[i].Pos, Rot = Q4.Identity, Vel = Items[i].Vel, Held = Items[i].HeldHere, Sleeping = Items[i].Sleeping };

    public void ApplyItem(int i, in ItemSample s)
    {
        if (!Items[i].Local) { Items[i].Pos = s.Pos; Items[i].Vel = s.Vel; }
    }

    public void SetItemAuthority(int i, bool local)
    {
        if (Program.Mutant == "no-authority") return;
        Items[i].Local = local;
        if (!local) Items[i].Sleeping = true;
    }

    public void DropLocal(int i) { if (Program.Mutant != "no-drop") Items[i].HeldHere = false; }

    // Like the game's grab patch: ask the session first.
    public bool Grab(Session s, int i)
    {
        if (Program.Mutant != "grab-always" && !s.OnLocalGrab(i)) return false;
        Items[i].HeldHere = true; Items[i].Sleeping = false;
        return true;
    }

    public void Release(int i, V3 vel) { Items[i].HeldHere = false; Items[i].Vel = vel; Items[i].Sleeping = false; }

    public void Step(double dt)
    {
        foreach (var it in Items)
        {
            if (it.HeldHere) { it.Pos = Hand; it.Vel = default; continue; }
            if (!it.Local || it.Sleeping) continue;
            float d = (float)dt;
            it.Vel.Y -= 9.8f * d;
            it.Pos = new V3(it.Pos.X + it.Vel.X * d, it.Pos.Y + it.Vel.Y * d, it.Pos.Z + it.Vel.Z * d);
            if (it.Pos.Y <= 0) { it.Pos.Y = 0; it.Vel = default; it.Sleeping = true; }
        }
    }
}
