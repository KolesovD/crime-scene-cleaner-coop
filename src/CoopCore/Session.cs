using System;
using System.Collections.Generic;
using System.IO;

namespace CoopCore;

public struct PlayerPose
{
    public V3 Pos;
    public float Yaw, Pitch;
    public short HeldItem;   // -1 = nothing; a body is its entity index (items first, then bodies)
    public short Tool;       // equipped tool: game tool id, -1 = none, -2 = power washer lance (ToolNone/ToolLance)
    public byte Flags;       // PoseUsing: the tool is being used right now
    public V3 ToolPos;       // the tool's root relative to the player (feet, yaw), as the owner's first-person view holds it
    public Q4 ToolRot;
    public float ToolScale;
    public const short ToolNone = -1, ToolLance = -2;
    public const byte PoseUsing = 1;
}

public struct ItemSample
{
    public V3 Pos;
    public Q4 Rot;
    public V3 Vel;
    public bool Held;       // held by its owner right now
    public bool Sleeping;   // rigidbody asleep (only meaningful on the owner)
}

// What the session needs from the game. The plugin implements it over Unity; tests use a fake.
public interface IWorld
{
    // False while there is no controllable player (menus, loading, cutscenes).
    bool LocalPose(out PlayerPose pose);
    // Identifies the loaded scene and its item table (scene name + item list hash); 0 = none.
    uint SceneKey { get; }
    int ItemCount { get; }
    ItemSample ReadItem(int index);
    // Remote-driven item: the newest state from its owner.
    void ApplyItem(int index, in ItemSample s);
    // true: this machine simulates the item (physics on). false: it follows ApplyItem.
    void SetItemAuthority(int index, bool local);
    // This machine is holding an item someone else got: let go of it.
    void DropLocal(int index);
}

public sealed class RemotePlayer
{
    public byte Number;
    public uint SceneKey;
    public double LastHeard;
    public float RttMs;   // host only: smoothed round trip to this player, 0 until measured
    ushort lastSeq;
    bool any;
    readonly (double t, PlayerPose p)[] buf = new (double, PlayerPose)[8];
    int count;

    public bool Push(double now, ushort seq, in PlayerPose p)
    {
        LastHeard = now;
        if (any && !Wire.Newer(seq, lastSeq)) return false;   // late or duplicate
        any = true; lastSeq = seq;
        if (count == buf.Length) { Array.Copy(buf, 1, buf, 0, buf.Length - 1); count--; }
        buf[count++] = (now, p);
        return true;
    }

    // Pose shown `delay` seconds in the past, interpolated between received samples.
    public bool Sample(double now, double delay, out PlayerPose p)
    {
        p = default;
        if (count == 0) return false;
        double t = now - delay;
        for (int i = count - 1; i >= 0; i--)
        {
            if (buf[i].t > t) continue;
            if (i == count - 1) { p = buf[i].p; return true; }
            var a = buf[i]; var b = buf[i + 1];
            float k = (float)((t - a.t) / (b.t - a.t));
            p = b.p;
            p.Pos = V3.Lerp(a.p.Pos, b.p.Pos, k);
            p.Yaw = LerpAngle(a.p.Yaw, b.p.Yaw, k);
            p.Pitch = a.p.Pitch + (b.p.Pitch - a.p.Pitch) * k;
            if (a.p.Tool == b.p.Tool) { p.ToolPos = V3.Lerp(a.p.ToolPos, b.p.ToolPos, k); p.ToolRot = Q4.Nlerp(a.p.ToolRot, b.p.ToolRot, k); }
            return true;
        }
        p = buf[0].p;
        return true;
    }

    static float LerpAngle(float a, float b, float t)
    {
        float d = ((b - a) % 360 + 540) % 360 - 180;
        return a + d * t;
    }
}

// One co-op session. The host (player 0) decides item owners; clients talk only to the host.
// Item rules (playbook/actor-ownership.md, applied to grabbable items):
//  - exactly one owner per item; it alone simulates and publishes the item;
//  - grab = claim to the host; every owner change raises the item's epoch;
//  - a state is accepted only from the owner at the current epoch;
//  - after release the owner keeps the item until it lies still, then yields it to the host.
public sealed class Session
{
    public const int MaxPlayers = 4;
    public const byte HostNumber = 0, NoPlayer = 255;
    const double PoseInterval = 1 / 20.0, ItemInterval = 1 / 20.0, Timeout = 10, HelloRetry = 1;
    const int RefreshPerSecond = 60;          // host re-sends resting items so lost packets heal
    const double YieldStill = 0.5, YieldMax = 8; // seconds still / seconds since release

    // Test-only: name of a rule to break on purpose (CoopCore.Tests --mutant). Empty in the game.
    public static string Mutant = "";

    public readonly bool IsHost;
    public byte Me { get; private set; }
    public Action<string> Log = _ => { };

    readonly ITransport net;
    readonly IWorld world;
    readonly ulong hostId;
    readonly MemoryStream ms = new();
    readonly List<Packet> inbox = new();
    readonly Dictionary<ulong, byte> peerToNum = new();   // host only
    readonly ulong[] numToPeer = new ulong[MaxPlayers];   // host only
    readonly HashSet<ulong> gone = new();                 // host only: peers that left (their late packets aren't malformed)
    public readonly Dictionary<byte, RemotePlayer> Players = new();

    // Item table for the current scene.
    uint sceneKey;
    bool synced;                 // client: has the host's table for this scene
    byte[] owner = Array.Empty<byte>();
    ushort[] epoch = Array.Empty<ushort>();
    bool[] ownerHeld = Array.Empty<bool>();
    double[] pendingClaim = Array.Empty<double>();
    double[] stillSince = Array.Empty<double>();
    double[] releasedAt = Array.Empty<double>();
    bool[] wasHeld = Array.Empty<bool>();
    int[] restSends = Array.Empty<int>();
    int refreshCursor;

    double now, nextPose, nextItems, nextHello, nextRtt;
    ushort poseSeq;
    public int Rx, Tx, Dropped;
    public float RttMs;   // client: smoothed round trip to the host, 0 until measured (shown in the corner)

    public Session(bool isHost, ITransport transport, IWorld world, ulong hostId = 0)
    {
        IsHost = isHost; net = transport; this.world = world; this.hostId = hostId;
        Me = isHost ? HostNumber : NoPlayer;
    }

    public bool HasPeer(ulong peer) => peerToNum.ContainsKey(peer);
    public bool Synced => IsHost ? sceneKey != 0 : synced;
    public byte OwnerOf(int i) => (uint)i < (uint)owner.Length ? owner[i] : NoPlayer;
    public ushort EpochOf(int i) => (uint)i < (uint)epoch.Length ? epoch[i] : (ushort)0;
    public IEnumerable<byte> ClientNumbers() { foreach (var kv in peerToNum) yield return kv.Value; }

    // ---- called by the game ----

    // A local grab is about to happen. Returns false if it must not (someone else holds it).
    public bool OnLocalGrab(int i)
    {
        if (!Synced || (uint)i >= (uint)owner.Length) return true;   // not sharing this scene: game as usual
        if (owner[i] == Me) return true;
        if (ownerHeld[i]) return false;
        if (IsHost) { SetOwner(i, HostNumber); return true; }
        pendingClaim[i] = now;
        world.SetItemAuthority(i, true);   // optimistic: the host normally agrees
        var w = Wire.Begin(ms, Msg.ItemClaim, Me);
        w.Write((ushort)i); w.Write(epoch[i]);
        SendHost(true);
        return true;
    }

    public void Tick(double time)
    {
        now = time;
        CheckScene();
        inbox.Clear();
        net.Poll(inbox);
        foreach (var p in inbox) Handle(p);
        if (IsHost) DropSilentPeers();
        else if (!synced && sceneKey != 0 && now >= nextHello) SendHello();

        if (now >= nextPose) { nextPose = now + PoseInterval; SendPose(); }
        if (now >= nextRtt) { nextRtt = now + 1; SendRttProbes(); }
        if (Synced)
        {
            TrackOwnedItems();
            if (now >= nextItems) { nextItems = now + ItemInterval; SendItems(); }
        }
    }

    // ---- world events ----
    // Doors, stains, bodies, quests... go as events through the host, reliable and in order.
    // A nonzero stateKey marks a state event ("door 12 is open"): the host keeps the newest payload per
    // (kind, stateKey) for this scene and replays them to anyone who joins the scene later.
    // OnEvent runs on every machine except the sender; on the host also for clients' events.
    public Action<byte, byte, BinaryReader> OnEvent = (_, _, _) => { };
    readonly Dictionary<(byte, uint), byte[]> eventState = new();
    readonly List<(byte kind, uint key)> eventOrder = new();
    public int EventsSent, EventsApplied;

    // global: for everyone connected, whatever scene they are in (scene key 0 on the wire), e.g. "the host
    // started job 3"; global state survives scene changes and is replayed to everyone who connects.
    public void SendEvent(byte kind, byte[] payload, int length, uint stateKey = 0, bool global = false)
    {
        if (global ? !IsHost && Me == NoPlayer : !Synced) return;
        var w = Wire.Begin(ms, Msg.Event, Me);
        w.Write(global ? 0u : sceneKey); w.Write(kind); w.Write(stateKey); w.Write(payload, 0, length);
        EventsSent++;
        if (IsHost)
        {
            if (global) { RememberGlobal(kind, stateKey, ms.GetBuffer(), (int)ms.Length); foreach (var p in peerToNum.Keys) Send(p, true); }
            else { Remember(kind, stateKey, ms.GetBuffer(), (int)ms.Length); ForEachPeerInScene(p => Send(p, true)); }
        }
        else SendHost(true);
    }

    readonly Dictionary<(byte, uint), byte[]> globalState = new();
    readonly HashSet<ulong> helloed = new();   // peers that already got the global state
    void RememberGlobal(byte kind, uint stateKey, byte[] buf, int len)
    {
        if (stateKey == 0) return;
        var copy = new byte[len];
        Buffer.BlockCopy(buf, 0, copy, 0, len);
        globalState[(kind, stateKey)] = copy;
    }

    void OnEventMsg(byte from, BinaryReader r, byte[] raw)
    {
        uint key = r.ReadUInt32();
        byte kind = r.ReadByte(); uint stateKey = r.ReadUInt32();
        bool global = key == 0;
        if (!global && (key != sceneKey || sceneKey == 0)) return;
        int at = (int)r.BaseStream.Position;
        if (IsHost)
        {
            raw[3] = from;   // stamped by the host, like poses
            if (global) RememberGlobal(kind, stateKey, raw, raw.Length); else Remember(kind, stateKey, raw, raw.Length);
            foreach (var kv in peerToNum)
                if (kv.Value != from && (global || Players.TryGetValue(kv.Value, out var rp) && rp.SceneKey == sceneKey))
                    net.Send(kv.Key, raw, raw.Length, true);
        }
        EventsApplied++;
        OnEvent(from, kind, new BinaryReader(new MemoryStream(raw, at, raw.Length - at, false)));
    }

    void Remember(byte kind, uint stateKey, byte[] buf, int len)
    {
        if (stateKey == 0) return;
        var copy = new byte[len];
        Buffer.BlockCopy(buf, 0, copy, 0, len);
        if (!eventState.ContainsKey((kind, stateKey))) eventOrder.Add((kind, stateKey));
        eventState[(kind, stateKey)] = copy;
    }

    void ForEachPeerInScene(Action<ulong> a)
    {
        foreach (var kv in peerToNum)
            if (Players.TryGetValue(kv.Value, out var rp) && rp.SceneKey == sceneKey) a(kv.Key);
    }

    public void Close()
    {
        Wire.Begin(ms, Msg.Bye, Me);
        if (IsHost) foreach (var p in peerToNum.Keys) Send(p, true); else SendHost(true);
    }

    // ---- scene ----

    void CheckScene()
    {
        uint key = world.SceneKey;
        if (key == sceneKey) return;
        sceneKey = key;
        synced = false;
        int n = key == 0 ? 0 : world.ItemCount;
        owner = new byte[n]; epoch = new ushort[n]; ownerHeld = new bool[n];
        pendingClaim = new double[n]; stillSince = new double[n]; releasedAt = new double[n]; wasHeld = new bool[n]; restSends = new int[n];
        for (int i = 0; i < n; i++) { epoch[i] = 1; pendingClaim[i] = -1; }
        eventState.Clear(); eventOrder.Clear();
        refreshCursor = 0;
        nextHello = now;
        Log($"scene key {key:X8}, {n} items");
        // Host: every peer already in this scene gets the table (they asked or will ask).
    }

    void SendHello()
    {
        nextHello = now + HelloRetry;
        var w = Wire.Begin(ms, Msg.Hello, Me);
        w.Write(Wire.Version); w.Write(sceneKey); w.Write(owner.Length);
        SendHost(true);
    }

    // Host: full table and item states to one peer, then Welcome closes the snapshot.
    void SendSnapshot(ulong peer, byte num)
    {
        for (int start = 0; start < owner.Length; start += 200)
        {
            var w = Wire.Begin(ms, Msg.ItemOwner, Me);
            w.Write(sceneKey);
            int end = Math.Min(owner.Length, start + 200);
            w.Write((ushort)(end - start));
            for (int i = start; i < end; i++) { w.Write((ushort)i); w.Write(owner[i]); w.Write(epoch[i]); }
            Send(peer, true);
        }
        var all = new List<int>(owner.Length);
        for (int i = 0; i < owner.Length; i++) if (owner[i] == HostNumber) all.Add(i);
        WriteStates(all, peer, true);
        foreach (var k in eventOrder) net.Send(peer, eventState[k], eventState[k].Length, true);   // state events, oldest first
        var wel = Wire.Begin(ms, Msg.Welcome, Me);
        wel.Write(num); wel.Write(sceneKey);
        Send(peer, true);
    }

    // ---- packets ----

    void Handle(Packet p)
    {
        var r = Wire.Open(p.Data, out var id, out var from);
        if (r == null) { Dropped++; return; }
        Rx++;
        try
        {
            if (IsHost)
            {
                if (id == Msg.Hello) { OnHello(p.From, r); return; }
                if (!peerToNum.TryGetValue(p.From, out var num)) { if (id != Msg.Bye && !gone.Contains(p.From)) Dropped++; return; }   // a turned-away player's Bye, a leaver's packets still in flight are fine
                Players.TryGetValue(num, out var rp);
                if (rp != null) rp.LastHeard = now;
                switch (id)
                {
                    case Msg.Pose: OnPose(num, r, p.Data); break;
                    case Msg.ItemStates: OnStates(num, r, p.Data.Length); break;
                    case Msg.ItemClaim: OnClaim(num, r); break;
                    case Msg.ItemYield: OnYield(num, r); break;
                    case Msg.Event: OnEventMsg(num, r, p.Data); break;
                    case Msg.Rtt: OnRtt(p.From, r, rp); break;
                    case Msg.Bye: RemovePeer(p.From); break;
                }
            }
            else
            {
                if (p.From != hostId) { Dropped++; return; }
                switch (id)
                {
                    case Msg.Welcome:
                        Me = r.ReadByte();
                        if (r.ReadUInt32() == sceneKey && sceneKey != 0) { synced = true; Log($"synced as player {Me}"); }
                        break;
                    case Msg.Pose: OnPose(from, r, null); break;
                    case Msg.ItemStates: OnStates(from, r, 0); break;
                    case Msg.ItemOwner: OnOwnerTable(r); break;
                    case Msg.Event: OnEventMsg(from, r, p.Data); break;
                    case Msg.Rtt: OnRtt(p.From, r, null); break;
                    case Msg.Bye: Players.Clear(); synced = false; RttMs = 0; break;
                }
            }
        }
        catch (EndOfStreamException) { Dropped++; }
    }

    // ---- round trip ----
    // Each side probes the other once a second with its own clock and the other echoes the probe straight
    // back, so the time is measured on one clock. Smoothed (1/4 of each new sample) for the corner label.
    void SendRttProbes()
    {
        if (IsHost) { foreach (var p in peerToNum.Keys) { RttMsg(0, now); Send(p, false); } }
        else if (Me != NoPlayer) { RttMsg(0, now); SendHost(false); }
    }

    void RttMsg(byte kind, double t0) { var w = Wire.Begin(ms, Msg.Rtt, Me); w.Write(kind); w.Write(t0); }

    void OnRtt(ulong peer, BinaryReader r, RemotePlayer rp)
    {
        byte kind = r.ReadByte(); double t0 = r.ReadDouble();
        if (kind == 0) { RttMsg(1, t0); Send(peer, false); return; }
        float ms1 = (float)((now - t0) * 1000);
        if (ms1 < 0 || ms1 > 10000) return;
        if (IsHost) { if (rp != null) rp.RttMs = rp.RttMs == 0 ? ms1 : rp.RttMs + (ms1 - rp.RttMs) / 4; }
        else RttMs = RttMs == 0 ? ms1 : RttMs + (ms1 - RttMs) / 4;
    }

    void OnHello(ulong peer, BinaryReader r)
    {
        if (r.ReadByte() != Wire.Version) { Dropped++; return; }
        uint key = r.ReadUInt32();
        if (!peerToNum.TryGetValue(peer, out var num))
        {
            num = NoPlayer;
            for (byte n = 1; n < MaxPlayers; n++) if (numToPeer[n] == 0) { num = n; break; }
            if (num == NoPlayer) { Log("lobby full, hello ignored"); return; }
            peerToNum[peer] = num; numToPeer[num] = peer; gone.Remove(peer);
            Players[num] = new RemotePlayer { Number = num, LastHeard = now };
            Log($"player {num} joined");
        }
        Players[num].SceneKey = key;
        if (!helloed.Contains(peer)) { helloed.Add(peer); foreach (var g in globalState.Values) net.Send(peer, g, g.Length, true); }
        if (key != 0 && key == sceneKey) SendSnapshot(peer, num);
        else { var w = Wire.Begin(ms, Msg.Welcome, Me); w.Write(num); w.Write(0u); Send(peer, true); }
    }

    void OnPose(byte num, BinaryReader r, byte[] raw)
    {
        if (num == Me) return;
        ushort seq = r.ReadUInt16();
        uint key = r.ReadUInt32();
        var pose = new PlayerPose { Pos = r.ReadV3(), Yaw = r.ReadSingle(), Pitch = r.ReadSingle(), HeldItem = r.ReadInt16(), Tool = PlayerPose.ToolNone, ToolRot = Q4.Identity };
        // 0.3.3 and later: the equipped tool. Older senders stop after HeldItem.
        if (r.BaseStream.Length - r.BaseStream.Position >= ToolBytes)
        {
            pose.Tool = r.ReadInt16(); pose.Flags = r.ReadByte();
            if (pose.Tool != PlayerPose.ToolNone) { pose.ToolPos = r.ReadV3(); pose.ToolRot = r.ReadQ4(); pose.ToolScale = r.ReadSingle(); }
        }
        if (!Players.TryGetValue(num, out var rp)) Players[num] = rp = new RemotePlayer { Number = num };
        rp.SceneKey = key;
        if (!rp.Push(now, seq, pose)) return;
        if (IsHost && raw != null)
        {
            raw[3] = num;   // the host stamps the sender: clients can't speak for others
            foreach (var kv in peerToNum) if (kv.Value != num) net.Send(kv.Key, raw, raw.Length, false);
        }
    }

    const int ToolBytes = 2 + 1;
    const int StateBytes = 2 + 2 + 1 + 12 + 16 + 12;

    void OnStates(byte from, BinaryReader r, int rawLen)
    {
        uint key = r.ReadUInt32();
        // A client takes states before Welcome too: the join snapshot comes ahead of it (owner table first).
        if (key != sceneKey || sceneKey == 0 || (IsHost && !Synced)) return;
        int n = r.ReadUInt16();
        List<int> relay = IsHost ? new List<int>() : null;
        for (int k = 0; k < n; k++)
        {
            int i = r.ReadUInt16(); ushort ep = r.ReadUInt16(); byte flags = r.ReadByte();
            var s = new ItemSample { Pos = r.ReadV3(), Rot = r.ReadQ4(), Vel = r.ReadV3(), Held = (flags & 1) != 0 };
            if ((uint)i >= (uint)owner.Length) { Dropped++; continue; }
            // Rule 4: only the owner at the current epoch; clients: the host relays, so `from` is the host.
            if (ep != epoch[i] && Mutant != "any-epoch") continue;
            if (IsHost && owner[i] != from) continue;
            if (owner[i] == Me || pendingClaim[i] >= 0) continue;
            ownerHeld[i] = s.Held;
            world.ApplyItem(i, s);
            relay?.Add(i);
            if (IsHost) lastRelayed[i] = s;
        }
        if (relay != null && relay.Count > 0)
            foreach (var kv in peerToNum) if (kv.Value != from) WriteStates(relay, kv.Key, false, lastRelayed);
    }

    readonly Dictionary<int, ItemSample> lastRelayed = new();

    void OnClaim(byte from, BinaryReader r)
    {
        int i = r.ReadUInt16(); r.ReadUInt16();
        if ((uint)i >= (uint)owner.Length || !Synced || Players[from].SceneKey != sceneKey) return;
        if (owner[i] == from) { BroadcastOwner(i); return; }
        bool free = !ownerHeld[i] && !(owner[i] == HostNumber && world.ReadItem(i).Held);
        if (free)
        {
            SetOwner(i, from);
            ownerHeld[i] = true;   // a claim is a grab; until its states say otherwise, it's in their hands
        }
        else { Log($"claim of {i} by {from} refused (held by {owner[i]})"); BroadcastOwner(i); }
    }

    void OnYield(byte from, BinaryReader r)
    {
        int i = r.ReadUInt16(); ushort ep = r.ReadUInt16();
        var s = new ItemSample { Pos = r.ReadV3(), Rot = r.ReadQ4(), Vel = default };
        if ((uint)i >= (uint)owner.Length || owner[i] != from || ep != epoch[i]) return;
        world.ApplyItem(i, s);
        SetOwner(i, HostNumber);
    }

    void OnOwnerTable(BinaryReader r)
    {
        uint key = r.ReadUInt32();
        if (key != sceneKey) return;
        int n = r.ReadUInt16();
        for (int k = 0; k < n; k++)
        {
            int i = r.ReadUInt16(); byte o = r.ReadByte(); ushort ep = r.ReadUInt16();
            if ((uint)i >= (uint)owner.Length) { Dropped++; continue; }
            bool fresh = Wire.Newer(ep, epoch[i]) || (!synced && ep == epoch[i]) || Mutant == "any-epoch";
            bool answer = pendingClaim[i] >= 0 && ep == epoch[i];   // a refusal repeats the current epoch
            if (!fresh && !answer) continue;   // rule 5: old statements change nothing
            ApplyOwner(i, o, ep);
        }
    }

    // ---- ownership ----

    void SetOwner(int i, byte o)
    {
        epoch[i]++;
        if (epoch[i] == 0) epoch[i] = 1;
        ApplyOwner(i, o, epoch[i]);
        BroadcastOwner(i);
    }

    void ApplyOwner(int i, byte o, ushort ep)
    {
        bool hadIt = owner[i] == Me;
        owner[i] = o; epoch[i] = ep;
        pendingClaim[i] = -1;
        ownerHeld[i] = false;
        stillSince[i] = -1; releasedAt[i] = now; wasHeld[i] = false;
        if (o == Me) world.SetItemAuthority(i, true);
        else
        {
            if (world.ReadItem(i).Held) world.DropLocal(i);
            world.SetItemAuthority(i, false);
        }
        if (hadIt != (o == Me)) Log($"item {i}: owner {o} epoch {ep}");
    }

    void BroadcastOwner(int i)
    {
        var w = Wire.Begin(ms, Msg.ItemOwner, Me);
        w.Write(sceneKey); w.Write((ushort)1);
        w.Write((ushort)i); w.Write(owner[i]); w.Write(epoch[i]);
        foreach (var kv in peerToNum)
            if (Players.TryGetValue(kv.Value, out var rp) && rp.SceneKey == sceneKey) Send(kv.Key, true);
    }

    // Owner side: notice when a released item has come to rest and hand it back to the host.
    void TrackOwnedItems()
    {
        if (IsHost) return;
        for (int i = 0; i < owner.Length; i++)
        {
            if (owner[i] != Me) continue;
            var s = world.ReadItem(i);
            if (s.Held) { wasHeld[i] = true; stillSince[i] = -1; continue; }
            if (wasHeld[i]) { wasHeld[i] = false; releasedAt[i] = now; }
            if (s.Sleeping) { if (stillSince[i] < 0) stillSince[i] = now; }
            else stillSince[i] = -1;
            bool rested = stillSince[i] >= 0 && now - stillSince[i] >= YieldStill;
            if (!rested && now - releasedAt[i] < YieldMax) continue;
            var w = Wire.Begin(ms, Msg.ItemYield, Me);
            w.Write((ushort)i); w.Write(epoch[i]); w.Write(s.Pos); w.Write(s.Rot);
            SendHost(true);
            releasedAt[i] = now + 1e9;   // once; the host's ItemOwner answer takes it from here
            stillSince[i] = -1;
        }
    }

    readonly List<int> sendList = new();

    void SendItems()
    {
        sendList.Clear();
        for (int i = 0; i < owner.Length; i++)
        {
            if (owner[i] != Me) continue;
            var s = world.ReadItem(i);
            // After it stops, the final state goes out a few more times: otherwise only the slow round-robin
            // refresh (all items, ~10 s) corrected the resting spot (owner's manual test: positions arrived late).
            if (s.Held || !s.Sleeping) { sendList.Add(i); restSends[i] = 3; }
            else if (restSends[i] > 0) { sendList.Add(i); restSends[i]--; }
        }
        if (IsHost)
        {
            // Round-robin refresh of resting items, so a lost final packet heals within seconds.
            int budget = (int)(RefreshPerSecond * ItemInterval) + 1;
            for (int k = 0; k < owner.Length && budget > 0; k++)
            {
                int i = (refreshCursor + k) % owner.Length;
                if (owner[i] == Me && !sendList.Contains(i)) { sendList.Add(i); budget--; }
            }
            if (owner.Length > 0) refreshCursor = (refreshCursor + (int)(RefreshPerSecond * ItemInterval) + 1) % owner.Length;
        }
        if (sendList.Count == 0) return;
        if (IsHost)
        {
            foreach (var kv in peerToNum)
                if (Players.TryGetValue(kv.Value, out var rp) && rp.SceneKey == sceneKey) WriteStates(sendList, kv.Key, false);
        }
        else WriteStates(sendList, hostId, false);
    }

    void WriteStates(List<int> items, ulong peer, bool reliable, Dictionary<int, ItemSample> source = null)
    {
        int perPacket = (Wire.MaxPacket - 12) / StateBytes;
        for (int start = 0; start < items.Count; start += perPacket)
        {
            int end = Math.Min(items.Count, start + perPacket);
            var w = Wire.Begin(ms, Msg.ItemStates, Me);
            w.Write(sceneKey); w.Write((ushort)(end - start));
            for (int k = start; k < end; k++)
            {
                int i = items[k];
                var s = source != null && source.TryGetValue(i, out var rs) ? rs : world.ReadItem(i);
                w.Write((ushort)i); w.Write(epoch[i]); w.Write((byte)(s.Held ? 1 : 0));
                w.Write(s.Pos); w.Write(s.Rot); w.Write(s.Vel);
            }
            Send(peer, reliable);
        }
    }

    void SendPose()
    {
        if (!IsHost && Me == NoPlayer) return;
        if (!world.LocalPose(out var pose)) return;
        var w = Wire.Begin(ms, Msg.Pose, Me);
        w.Write(++poseSeq); w.Write(sceneKey);
        w.Write(pose.Pos); w.Write(pose.Yaw); w.Write(pose.Pitch); w.Write(pose.HeldItem);
        w.Write(pose.Tool); w.Write(pose.Flags);
        if (pose.Tool != PlayerPose.ToolNone) { w.Write(pose.ToolPos); w.Write(pose.ToolRot); w.Write(pose.ToolScale); }
        if (IsHost) foreach (var p in peerToNum.Keys) Send(p, false); else SendHost(false);
    }

    // ---- peers ----

    void DropSilentPeers()
    {
        foreach (var kv in new List<KeyValuePair<ulong, byte>>(peerToNum))
            if (Players.TryGetValue(kv.Value, out var rp) && now - rp.LastHeard > Timeout) { Log($"player {kv.Value} timed out"); RemovePeer(kv.Key); }
    }

    void RemovePeer(ulong peer)
    {
        if (!peerToNum.TryGetValue(peer, out var num)) return;
        peerToNum.Remove(peer); numToPeer[num] = 0; Players.Remove(num); helloed.Remove(peer); gone.Add(peer);
        Log($"player {num} left");
        // Rule 8: a leaver's items go back to the host at a higher epoch.
        for (int i = 0; i < owner.Length; i++) if (owner[i] == num) SetOwner(i, HostNumber);
    }

    void Send(ulong peer, bool reliable) { Tx++; net.Send(peer, ms.GetBuffer(), (int)ms.Length, reliable); }
    void SendHost(bool reliable) => Send(hostId, reliable);
}
