using System;
using System.Linq;
using System.Text;
using CoopCore;
using UnityEngine;

namespace CscCoop;

// footsteps suite (-Test footsteps -Mission 11): the client steps with bloody shoes through the game's own
// FootprintSpawner.ProcessStep; every footprint must appear on the host too, the same decal at the same spot.
// doorsync suite (-Test doorsync -Mission 11): both players open and close room doors the way a click does
// (ShortcutDoors.Interact -> OnOpen -> Toggle) or a switch does (ExternalToggle); every door must end the same way on
// both, and each clicked door (two actions on each) as it started.
public partial class Bot
{
    int fsSteps;

    void FootstepsTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        switch (step)
        {
            case 0 when t > 4:
                if (!host)
                {
                    var fs = w.Player.GetComponentInChildren<FootprintSpawner>();
                    if (fs != null) fs.Intensity = 1f;
                    Plugin.L.LogInfo($"FOOTSTEPS client shoes bloody: {(fs != null ? fs.Intensity.ToString("F2") : "NO SPAWNER")}");
                    walkFrom = w.Player.transform.position; walkDir = w.Player.transform.forward;
                }
                step = 1; break;
            case 1 when t > 5 + fsSteps * 0.5f && fsSteps < 12:
                if (!host)
                {
                    Walk(w, (fsSteps + 1) * 0.45f);
                    var fs = w.Player.GetComponentInChildren<FootprintSpawner>();
                    if (fs != null) { if (fs.Intensity < 0.3f) fs.Intensity = 1f; fs.ProcessStep(fsSteps % 2 == 0); }
                }
                fsSteps++;
                if (fsSteps == 12) step = 2;
                break;
            case 2 when t > 20:
                var sb = new StringBuilder();
                int n = 0;
                foreach (var kv in Stains.NewStains.OrderBy(k => k.Key))
                {
                    var tt = kv.Value;
                    if (tt == null) continue;
                    var p = tt.transform.position;
                    sb.Append(FormattableString.Invariant($"{kv.Key:X}={tt.gameObject.name.Replace("(Clone)", "").Trim()}@{p.x:F2}/{p.y:F2}/{p.z:F2}~{tt.Intensity:F2};"));
                    n++;
                }
                Plugin.L.LogInfo($"FOOTSTEPS final made {Stains.Made} copied {Stains.Copied} new {n} {sb}");
                Plugin.L.LogInfo("FOOTSTEPS dump done");
                step = 3; break;
        }
    }

    // Door roles: the first two unlocked doors with nothing required (the same on both: scene table order).
    int[] dsDoors = Array.Empty<int>();

    void DoorSyncTest(float t, Session ses, GameWorld w)
    {
        bool host = ses.IsHost;
        switch (step)
        {
            case 0 when t > 3:
                dsDoors = Enumerable.Range(0, Doors.Shortcuts.Count).Where(i => Doors.Shortcuts[i] != null && Doors.Shortcuts[i].gameObject.activeInHierarchy && !Doors.Shortcuts[i].IsLocked && Doors.Shortcuts[i].RequiredKeys.Count == 0).Take(3).ToArray();
                Plugin.L.LogInfo($"DOORSYNC roles {string.Join(",", dsDoors)} of {Doors.Shortcuts.Count}; {DoorStates()}");
                step = 1; break;
            case 1 when t > 5:   // host: door 0 by a switch (ExternalToggle), door 1 by a click
                if (host && dsDoors.Length > 1)
                {
                    var d0 = Doors.Shortcuts[dsDoors[0]]; bool b0 = d0.isClosed;
                    d0.ExternalToggle();
                    Plugin.L.LogInfo($"DOORSYNC host clicked door {dsDoors[0]}: closed {b0} -> {d0.isClosed} (switch)");
                    Click(dsDoors[1], "host");
                }
                step = 2; break;
            case 2 when t > 9:   // client clicks door 1 (back) and 2
                if (!host) foreach (var i in dsDoors.Skip(1)) Click(i, "client");
                step = 3; break;
            case 3 when t > 13:  // host clicks door 0 again (back), client door 2 again
                if (host && dsDoors.Length > 0) Click(dsDoors[0], "host");
                if (!host && dsDoors.Length > 2) Click(dsDoors[2], "client");
                step = 4; break;
            case 4 when t > 22:
                Plugin.L.LogInfo($"DOORSYNC final {DoorStates()}");
                Plugin.L.LogInfo("DOORSYNC dump done");
                step = 5; break;
        }
    }

    static void Click(int i, string who)
    {
        var d = Doors.Shortcuts[i];
        bool before = d.isClosed;
        d.Toggle();   // what a click ends in (Interact -> OnOpen -> Toggle)
        Plugin.L.LogInfo($"DOORSYNC {who} clicked door {i}: closed {before} -> {d.isClosed}");
    }

    static string DoorStates()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Doors.Shortcuts.Count; i++) { var d = Doors.Shortcuts[i]; sb.Append(d == null ? '-' : d.isClosed ? 'C' : 'O'); }
        return "doors " + sb;
    }
}
