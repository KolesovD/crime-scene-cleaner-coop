using UnityEngine;

namespace CscCoop;

// Skips the job's intro cutscene (the drive to the crime scene). Job intros are
// PlayableDirectorOverride timelines; ExitCutscene is the game's own way out of one.
// On with the SkipIntro setting (BepInEx/config/csccoop.cfg, also switchable in the F8 panel) or CSCCOOP_SKIP_INTRO=1.
static class IntroSkip
{
    static float lastTry;

    public static void Tick(GameWorld w)
    {
        if (!(Plugin.SkipIntro.Value || Env.Get("CSCCOOP_SKIP_INTRO") == "1") || w.Player == null || w.Player.CanMove) return;
        if (Time.unscaledTime - lastTry < 1) return;
        lastTry = Time.unscaledTime;
        foreach (var c in Object.FindObjectsOfType<PlayableDirectorOverride>())
        {
            var d = c.playableDirector;
            if (d == null || d.state != UnityEngine.Playables.PlayState.Playing) continue;
            Plugin.L.LogInfo($"COOP skipping cutscene {c.name} at {d.time:F1}/{d.duration:F1} s");
            // ExitCutscene fast-forwards the timeline (seen: 0.3 s -> 8.5 s within a second); the player can
            // move ~11 s after the job loads instead of ~30 s. Jumping straight to the last frame was faster
            // (0-6 s) but the game then made the client drop a held item a few seconds later (items test failed).
            c.ExitCutscene();
        }
    }
}
