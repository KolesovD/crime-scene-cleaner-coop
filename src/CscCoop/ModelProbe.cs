using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

namespace CscCoop;

// One-off investigation (CSCCOOP_TEST=modelprobe): where is a full-body player model for showing other
// players instead of a capsule? First probe (job scene, human Animators only): only the 3 bodies. The owner
// remembers a cutscene with the full Kowalski model, so this lists every Animator with skinned meshes
// (human or generic rig), everything bound to cutscene timelines, and runs in the job and in the hub.
static class ModelProbe
{
    static readonly HashSet<string> done = new();
    static float at;

    public static void Tick(GameWorld w)
    {
        string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (done.Contains(scene) || w.Player == null || !w.Player.CanMove) { at = Time.unscaledTime + 3; return; }
        if (Time.unscaledTime < at) return;
        done.Add(scene);
        Plugin.L.LogInfo($"MODELPROBE scene '{scene}'");
        foreach (var a in Resources.FindObjectsOfTypeAll<Animator>())
        {
            if (a == null || a.gameObject.scene.name == null) continue;
            int skinned = a.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            if (skinned == 0) continue;
            int bones = 0;
            foreach (var smr in a.GetComponentsInChildren<SkinnedMeshRenderer>(true)) bones = Mathf.Max(bones, smr.bones.Length);
            Plugin.L.LogInfo($"MODELPROBE animator '{Path(a.transform)}' human {a.isHuman} avatar {a.avatar?.name} controller {a.runtimeAnimatorController?.name} skinned {skinned} bones {bones} active {a.gameObject.activeInHierarchy} scene '{a.gameObject.scene.name}'");
        }
        foreach (var d in Resources.FindObjectsOfTypeAll<PlayableDirector>())
        {
            if (d == null || d.playableAsset == null || d.gameObject.scene.name == null) continue;
            var en = d.playableAsset.outputs.GetEnumerator();   // an Il2Cpp enumerator: MoveNext is on the base interface
            var move = en.Cast<Il2CppSystem.Collections.IEnumerator>();
            while (move.MoveNext())
            {
                var output = en.Current;
                var b = d.GetGenericBinding(output.sourceObject);
                if (b == null) continue;
                var go = b.TryCast<Component>()?.gameObject ?? b.TryCast<GameObject>();
                if (go == null) continue;
                int skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                if (skinned > 0) Plugin.L.LogInfo($"MODELPROBE timeline '{d.name}' track '{output.streamName}' -> '{Path(go.transform)}' skinned {skinned}");
            }
        }
        Plugin.L.LogInfo($"MODELPROBE scene '{scene}' done");
        if (scene.Contains("Hub")) { Plugin.L.LogInfo("BOT done, quitting"); System.Diagnostics.Process.GetCurrentProcess().Kill(); }
        // In the job: leave, close the report, and probe the hub too.
        var leave = Object.FindObjectOfType<MissionLeaveController>();
        if (leave != null) leave.LeaveMissionWithReport();
    }

    public static void TickReport()
    {
        var report = Object.FindObjectOfType<MissionReportFinalView>();
        if (report != null && report.isActiveAndEnabled && Time.unscaledTime > reportAt)
        {
            if (reportAt == 0) { reportAt = Time.unscaledTime + 8; return; }
            report.OnCloseReport(); reportAt = float.MaxValue;
        }
    }
    static float reportAt;

    static string Path(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}
