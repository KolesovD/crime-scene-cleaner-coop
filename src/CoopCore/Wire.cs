using System;
using System.IO;
using System.Text;

namespace CoopCore;

public struct V3
{
    public float X, Y, Z;
    public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static V3 Lerp(V3 a, V3 b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    public float DistanceTo(V3 o) { float dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z; return MathF.Sqrt(dx * dx + dy * dy + dz * dz); }
    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
}

public struct Q4
{
    public float X, Y, Z, W;
    public Q4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
    public static readonly Q4 Identity = new(0, 0, 0, 1);
    // Normalized lerp along the shorter arc.
    public static Q4 Nlerp(Q4 a, Q4 b, float t)
    {
        if (a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W < 0) b = new(-b.X, -b.Y, -b.Z, -b.W);
        var q = new Q4(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
        float n = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return n > 1e-6f ? new(q.X / n, q.Y / n, q.Z / n, q.W / n) : Identity;
    }
}

// Message ids. Every message starts with: magic (2 bytes), id (1 byte), sender player (1 byte).
public enum Msg : byte
{
    Hello = 1,        // client -> host: protocol version, scene table hash
    Welcome = 2,      // host -> client: assigned player number
    Pose = 3,         // anyone: player pose (host relays clients' poses)
    ItemStates = 4,   // owner -> host -> others: positions of items the sender owns
    ItemClaim = 5,    // client -> host: wants to grab item
    ItemOwner = 6,    // host -> all: owner and epoch of items (idempotent, epoch ordered)
    ItemYield = 7,    // owner -> host: done with item (it came to rest)
    Bye = 8,
    Ping = 9,         // host -> invited friend: opens the Steam session from the host side; ignored otherwise
    Event = 10,       // anyone -> host -> others: a world event (door, stain, body...), see Session.SendEvent
    Rtt = 11,         // host <-> client, once a second: 0 = probe with the sender's clock, 1 = the probe echoed back
}

public static class Wire
{
    public const ushort Magic = 0xC5C0;
    public const byte Version = 1;
    public const int MaxPacket = 1100;   // stays under Steam's unreliable MTU

    public static BinaryWriter Begin(MemoryStream ms, Msg id, byte from)
    {
        ms.SetLength(0);
        var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic); w.Write((byte)id); w.Write(from);
        return w;
    }

    // Returns null for anything that isn't ours (wrong magic, too short).
    public static BinaryReader Open(byte[] data, out Msg id, out byte from)
    {
        id = 0; from = 0;
        if (data == null || data.Length < 4) return null;
        var r = new BinaryReader(new MemoryStream(data, false));
        if (r.ReadUInt16() != Magic) return null;
        id = (Msg)r.ReadByte(); from = r.ReadByte();
        return r;
    }

    public static void Write(this BinaryWriter w, V3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    public static V3 ReadV3(this BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    public static void Write(this BinaryWriter w, Q4 q) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
    public static Q4 ReadQ4(this BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    // Sequence numbers wrap; a is newer than b if it is ahead by less than half the range.
    public static bool Newer(ushort a, ushort b) => a != b && (ushort)(a - b) < 0x8000;
}
