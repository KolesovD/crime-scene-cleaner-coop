using System;
using System.Reflection;
using UnityEngine;

namespace CscCoop;

// Bot runs while the owner uses the PC (CSCCOOP_QUIET=1, set by tools/run-pair.ps1): lowest quality, 30 fps,
// no sound, keeps running unfocused. Re-applied every few seconds: the game applies its saved settings after
// the menu loads (as with the window size, see WindowPlacer). The game's sound is Wwise, which ignores
// AudioListener, so the output volume is set to 0 through AkSoundEngine (reflection: no Wwise reference).
static class Quiet
{
    // CSCCOOP_LOWGFX=1: the same lowest settings but with sound (manual tests on the owner's screen).
    static readonly bool mute = Env.Get("CSCCOOP_QUIET") == "1";
    public static readonly bool On = mute || Env.Get("CSCCOOP_LOWGFX") == "1";
    static float next;
    static MethodInfo setOutputVolume;
    static bool looked, logged;

    public static void Tick()
    {
        if (!On || Time.unscaledTime < next) return;
        next = Time.unscaledTime + 3;
        Application.runInBackground = true;
        if (QualitySettings.GetQualityLevel() != 0) QualitySettings.SetQualityLevel(0, true);
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = mute ? 30 : 60;   // 60 when someone plays on it
        if (!mute) return;
        AudioListener.volume = 0;
        Mute();
    }

    static void Mute()
    {
        if (!looked)
        {
            looked = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("AkSoundEngine");
                if (t == null) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "SetOutputVolume" && ps.Length == 2 && ps[1].ParameterType == typeof(float)) { setOutputVolume = m; break; }
                }
            }
        }
        if (setOutputVolume == null) { if (!logged) { logged = true; Plugin.L.LogWarning("COOP quiet: AkSoundEngine.SetOutputVolume not found, sound stays on"); } return; }
        try
        {
            var idType = setOutputVolume.GetParameters()[0].ParameterType;
            setOutputVolume.Invoke(null, new[] { Convert.ChangeType(0, idType), (object)0f });   // 0 = the main output
            if (!logged) { logged = true; Plugin.L.LogInfo("COOP quiet: lowest quality, 30 fps, sound off"); }
        }
        catch (Exception e) { if (!logged) { logged = true; Plugin.L.LogWarning($"COOP quiet: muting failed: {e.Message}"); } }
    }
}
