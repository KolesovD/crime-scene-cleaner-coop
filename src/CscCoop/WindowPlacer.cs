using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace CscCoop;

// Manual two-window tests (CSCCOOP_WINDOW="x,y,w,h,title"): keeps this game windowed at that place and size.
// The game applies its saved screen settings after the menu loads, which put both test windows full screen
// on top of each other (owner's manual test), so the start arguments alone are not enough.
static class WindowPlacer
{
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr h, string text);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
    struct Rect { public int L, T, R, B; }

    static readonly string spec = Env.Get("CSCCOOP_WINDOW");
    static float next;
    static IntPtr window;

    public static void Tick()
    {
        if (spec == "" || Time.unscaledTime < next) return;
        next = Time.unscaledTime + 2;
        var p = spec.Split(',');
        if (p.Length < 4) return;
        int x = int.Parse(p[0]), y = int.Parse(p[1]), w = int.Parse(p[2]), h = int.Parse(p[3]);
        if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != w || Screen.height != h)
            Screen.SetResolution(w, h, FullScreenMode.Windowed);
        if (window == IntPtr.Zero) window = Find();
        if (window == IntPtr.Zero) return;
        GetWindowRect(window, out var r);
        if (r.L != x || r.T != y) MoveWindow(window, x, y, r.R - r.L, r.B - r.T, true);
        if (p.Length > 4) SetWindowText(window, p[4]);
    }

    static IntPtr Find()
    {
        uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var sb = new StringBuilder(64); GetClassName(hwnd, sb, 64);
            if (pid == me && sb.ToString() == "UnityWndClass") { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
