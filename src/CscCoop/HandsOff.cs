using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CscCoop;

// Bot runs (CSCCOOP_TEST set) skip what only costs time: the new game's intro cinematic and the loading screen's
// "press a key" wait (job cutscenes: IntroSkip). Quiet bot runs (CSCCOOP_QUIET=1, the owner uses the PC meanwhile)
// also leave the mouse alone: the game locks the cursor to its window every frame (Controller_Switch,
// FirstPersonAIO, MoveCamera, InputController all set Cursor.lockState) and takes focus when its window opens,
// and when a job loads, which grabbed the owner's mouse. So: the cursor is freed after the game's Update, and when
// the game takes focus without a mouse click, focus goes back to the window named in CSCCOOP_FOCUS_BACK (set by
// tools/run-game.ps1 to the window in front when the script started); a click into a bot window keeps it. (Rewired
// already ignores input while the window isn't focused: ignoreInputWhenAppNotInFocus is true.) The game also locks the cursor
// while it starts and then stalls for seconds loading the menu (no frames, so FreeCursor can't run): seen as the
// owner's cursor pinned to one point for 4-8 s with another window in front. A background thread releases any
// cursor clip while the game's window isn't in front.
static class HandsOff
{
    static readonly bool bot = Env.Get("CSCCOOP_TEST") != "";
    public static readonly bool On = Env.Get("CSCCOOP_QUIET") == "1";
    static readonly IntPtr focusBack = long.TryParse(Env.Get("CSCCOOP_FOCUS_BACK"), out var h) ? new IntPtr(h) : IntPtr.Zero;
    static float next, nextLog;
    static bool introSkipped, wasOurs;
    static IntPtr loadingSkipped;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool GetClipCursor(out Rect r);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    struct Rect { public int L, T, R, B; }

    static readonly uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    static int unclipped;

    // Called from Plugin.Load, before the first scene.
    public static void Start()
    {
        if (!On) return;
        new System.Threading.Thread(() =>
        {
            while (true)
            {
                try
                {
                    System.Threading.Thread.Sleep(100);
                    GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
                    if (pid == me || !GetClipCursor(out var r)) continue;
                    if (r.R - r.L >= GetSystemMetrics(78) && r.B - r.T >= GetSystemMetrics(79)) continue;   // not clipped
                    ClipCursor(IntPtr.Zero); unclipped++;
                }
                catch { }
            }
        }) { IsBackground = true, Name = "CscCoop cursor" }.Start();
    }

    public static void Tick()
    {
        if (On) { FocusBack(); if (Time.unscaledTime >= nextLog) { nextLog = Time.unscaledTime + 5; LogUnclipped(); } }
        if (!bot || Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.5f;
        SkipGameIntro();
        SkipLoadingWait();
    }

    // From CoopRunner.LateUpdate, after the game's Update has locked it again.
    public static void FreeCursor()
    {
        if (!On) return;
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible) Cursor.visible = true;
    }

    static void FocusBack()
    {
        if (focusBack == IntPtr.Zero) return;
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out var pid);
        bool ours = pid == me;
        // Just became the front window with no mouse button down: the game took it, not the owner.
        bool clicked = (GetAsyncKeyState(0x01) & 0x8001) != 0 || (GetAsyncKeyState(0x02) & 0x8001) != 0;
        if (ours && !wasOurs && !clicked && IsWindow(focusBack) && SetForegroundWindow(focusBack))
        {
            Plugin.L.LogInfo($"COOP test: gave focus back at {Time.unscaledTime:F1} s");
            ours = false;
        }
        wasOurs = ours;
    }

    static int unclippedLogged;
    static void LogUnclipped()
    {
        int n = unclipped;
        if (n != unclippedLogged) { Plugin.L.LogInfo($"COOP test: released the cursor clip {n - unclippedLogged} time(s) while another window was in front"); unclippedLogged = n; }
    }

    // Stop() is the first thing the game's skip button does; OnSkipButtonPressed itself throws when called with
    // a default Rewired event (seen in a probe), and the intro then ends as if skipped.
    static void SkipGameIntro()
    {
        if (introSkipped) return;
        var intro = UnityEngine.Object.FindObjectOfType<IntroController>();
        var d = intro != null ? intro.Cinematic : null;
        if (d == null || d.state != UnityEngine.Playables.PlayState.Playing) return;
        introSkipped = true;
        Plugin.L.LogInfo($"COOP test: skipping the game intro at {d.time:F1}/{d.duration:F1} s");
        d.Stop();
    }

    // LoadingScreenController.WaitForSkipRoutine waits (up to 30 s) on this flag.
    static void SkipLoadingWait()
    {
        var l = UnityEngine.Object.FindObjectOfType<LoadingScreenController>();
        if (l == null || !l.WaitingForSkip || l.Pointer == loadingSkipped) return;
        loadingSkipped = l.Pointer;
        Plugin.L.LogInfo($"COOP test: skipping the loading screen's key wait at {Time.unscaledTime:F1} s");
        l.WaitingForSkip = false;
    }
}
