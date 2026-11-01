using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CscCoop;

// doorprobe (tools/run-probe.ps1 -Test doorprobe -Mission 11): what doors a job has. Lists every component type
// whose name says door/hinge/openable/gate, how many, and which of them the mod shares (its tables).
public partial class Bot
{
    bool dpDone;

    void DoorProbe(string scene, float inScene, GameWorld w)
    {
        string only = Env.Get("CSCCOOP_TEST_SCENE");
        if (dpDone || w.Player == null || !w.Player.CanMove || inScene < 8 || (only != "" && scene != only)) return;
        dpDone = true;
        var counts = new Dictionary<string, int>();
        foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            string n = mb.GetIl2CppType().Name;
            if (!System.Text.RegularExpressions.Regex.IsMatch(n, "door|hinge|openable|gate|hatch|trapdoor", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            counts.TryGetValue(n, out var c); counts[n] = c + 1;
        }
        Plugin.L.LogInfo($"DOORPROBE {scene}: {string.Join(", ", counts.OrderByDescending(k => k.Value).Select(k => k.Key + "=" + k.Value))}");
        Plugin.L.LogInfo($"DOORPROBE shared: openables {Furniture.Openables.Count}, shortcut doors {Doors.Shortcuts.Count}, plain doors {Doors.Plain.Count}");
        foreach (var o in Object.FindObjectsOfType<Openable>())
            if (o.name.ToLowerInvariant().Contains("door")) Plugin.L.LogInfo($"DOORPROBE openable door '{o.name}' at {P3(o.transform.position)} open {o.isOpen} index {Furniture.Openables.IndexOf(o)}");
        Plugin.L.LogInfo("DOORPROBE done");
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
}
