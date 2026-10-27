using System.Collections.Generic;
using System.IO;

namespace CscCoop;

// Each player's table fingerprints (GameWorld.Parts) for the job it is in, sent to everyone; a table whose
// fingerprint differs from another player's in the same job is switched off (GameWorld.Off) and named in the log,
// and everything else stays shared (friend's test of 0.4.1, Villa: one differing table used to cut the whole job).
public static class TableCheck
{
    static readonly Dictionary<byte, (string scene, Dictionary<string, uint> parts)> theirs = new();

    static uint sentKey; static int sentPlayers = -1;

    public static void OnBuilt(GameWorld w) { Compare(w); sentKey = 0; Tick(w); }

    // Sent once per table build, and again whenever the players change (a joiner needs ours).
    public static void Tick(GameWorld w)
    {
        var s = CoopRunner.Instance?.Session;
        if (s == null || !s.Synced || w.Parts.Count == 0 || w.SceneKey == 0) return;
        if (sentKey == w.SceneKey && sentPlayers == s.Players.Count) return;
        sentKey = w.SceneKey; sentPlayers = s.Players.Count;
        string scene = w.Scene;
        var parts = new List<KeyValuePair<string, uint>>(w.Parts);
        WorldEvents.SendGlobal(Ev.TableHashes, wr =>
        {
            wr.Write(scene); wr.Write((ushort)parts.Count);
            foreach (var kv in parts) { wr.Write(kv.Key); wr.Write(kv.Value); }
        }, 0x7B000000u | s.Me);
    }

    public static void OnHashes(byte from, BinaryReader r)
    {
        string scene = r.ReadString(); int n = r.ReadUInt16();
        var parts = new Dictionary<string, uint>();
        for (int i = 0; i < n; i++) parts[r.ReadString()] = r.ReadUInt32();
        theirs[from] = (scene, parts);
        var w = CoopRunner.Instance?.World;
        if (w != null) Compare(w);
    }

    static void Compare(GameWorld w)
    {
        if (w.Parts.Count == 0) return;
        foreach (var kv in theirs)
        {
            if (kv.Value.scene != w.Scene) continue;
            foreach (var p in w.Parts)
                if (kv.Value.parts.TryGetValue(p.Key, out var h) && h != p.Value && w.Off.Add(p.Key))
                    Plugin.L.LogWarning($"COOP table '{p.Key}' differs from player {kv.Key}'s: not shared in this job, everything else is");
        }
    }
}
