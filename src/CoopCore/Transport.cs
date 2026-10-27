using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace CoopCore;

public struct Packet
{
    public ulong From;
    public byte[] Data;
}

// Peers are addressed by a 64-bit id: a SteamID on Steam, the UDP port on the local transport.
public interface ITransport : IDisposable
{
    ulong LocalId { get; }
    void Send(ulong to, byte[] data, int length, bool reliable);
    void Poll(List<Packet> into);
}

// Localhost UDP, for two game copies on one PC. Loopback doesn't drop, so "reliable" isn't
// emulated; the chaos settings drop and delay unreliable packets for tests.
public sealed class UdpTransport : ITransport
{
    readonly UdpClient udp;
    readonly Random rng = new();
    readonly List<(double due, ulong to, byte[] data)> delayed = new();

    public ulong LocalId { get; }
    public float LossUnreliable;
    public float LatencyMs;

    public UdpTransport(int port)
    {
        udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        udp.Client.ReceiveBufferSize = 4 << 20;
        // Windows reports ICMP port-unreachable as a receive error; switch that off.
        const int SIO_UDP_CONNRESET = -1744830452;
        if (OperatingSystem.IsWindows()) udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        LocalId = (ulong)port;
    }

    public void Send(ulong to, byte[] data, int length, bool reliable)
    {
        if (!reliable && LossUnreliable > 0 && rng.NextDouble() < LossUnreliable) return;
        var copy = new byte[length];
        Buffer.BlockCopy(data, 0, copy, 0, length);
        if (LatencyMs > 0) { delayed.Add((Clock.Now + LatencyMs / 1000.0, to, copy)); return; }
        udp.Send(copy, copy.Length, new IPEndPoint(IPAddress.Loopback, (int)to));
    }

    public void Poll(List<Packet> into)
    {
        // Oldest first: the delay is the same for every packet, so this keeps send order
        // (reliable messages are ordered on Steam too; sending newest first broke the join snapshot).
        int sent = 0;
        while (sent < delayed.Count && delayed[sent].due <= Clock.Now)
        {
            udp.Send(delayed[sent].data, delayed[sent].data.Length, new IPEndPoint(IPAddress.Loopback, (int)delayed[sent].to));
            sent++;
        }
        delayed.RemoveRange(0, sent);
        while (udp.Available > 0)
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            byte[] d;
            try { d = udp.Receive(ref ep); } catch (SocketException) { continue; }
            into.Add(new Packet { From = (ulong)ep.Port, Data = d });
        }
    }

    public void Dispose() => udp.Dispose();
}

// In-memory network for game-free tests: per-link loss and delay, reliable packets never dropped.
public sealed class FakeNet
{
    readonly Random rng;
    readonly List<(double due, ulong from, ulong to, byte[] data)> inFlight = new();
    readonly Dictionary<ulong, Queue<Packet>> inbox = new();
    public double Now;
    public float Loss;
    public double Delay = 0.03;

    public FakeNet(int seed) { rng = new Random(seed); }

    public ITransport Join(ulong id) { inbox[id] = new Queue<Packet>(); return new End(this, id); }
    public void Drop(ulong id) => inbox.Remove(id);
    // Delivers a packet now, with any sender id: replays and forged packets in tests.
    public void Inject(ulong from, ulong to, byte[] data) { if (inbox.TryGetValue(to, out var q)) q.Enqueue(new Packet { From = from, Data = data }); }

    public void Advance(double dt)
    {
        Now += dt;
        for (int i = 0; i < inFlight.Count; i++)
        {
            var p = inFlight[i];
            if (p.due > Now) continue;
            if (inbox.TryGetValue(p.to, out var q)) q.Enqueue(new Packet { From = p.from, Data = p.data });
            inFlight.RemoveAt(i--);
        }
    }

    sealed class End : ITransport
    {
        readonly FakeNet net;
        public ulong LocalId { get; }
        public End(FakeNet n, ulong id) { net = n; LocalId = id; }
        public void Send(ulong to, byte[] data, int length, bool reliable)
        {
            if (!reliable && net.rng.NextDouble() < net.Loss) return;
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            // Unreliable packets get jitter, so they can arrive out of order.
            double jitter = reliable ? 0 : net.rng.NextDouble() * net.Delay;
            net.inFlight.Add((net.Now + net.Delay + jitter, LocalId, to, copy));
        }
        public void Poll(List<Packet> into)
        {
            if (!net.inbox.TryGetValue(LocalId, out var q)) return;
            while (q.Count > 0) into.Add(q.Dequeue());
        }
        public void Dispose() => net.Drop(LocalId);
    }
}

public static class Clock
{
    // Not DateTime seconds as float: that has 128 s resolution (bug seen in the Shadows of Doubt mod).
    static readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
    public static double Now => sw.Elapsed.TotalSeconds;
}
