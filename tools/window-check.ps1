# Prints where the test copies' game windows are (manual two-window test).
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class WinCheck {
    public struct Rect { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
    public static string Describe(uint pid) {
        string result = "no game window";
        EnumWindows((h, p) => {
            uint wp; GetWindowThreadProcessId(h, out wp);
            var c = new StringBuilder(64); GetClassName(h, c, 64);
            if (wp != pid || c.ToString() != "UnityWndClass") return true;
            var t = new StringBuilder(128); GetWindowText(h, t, 128);
            Rect r; GetWindowRect(h, out r);
            result = string.Format("'{0}' x {1}..{2} y {3}..{4}", t, r.L, r.R, r.T, r.B);
            return false;
        }, IntPtr.Zero);
        return result;
    }
}
"@
Get-Process CrimeCleaner -ErrorAction SilentlyContinue | ForEach-Object { "{0}: {1}" -f ($_.Path -replace '.*\\local\\', ''), [WinCheck]::Describe([uint32]$_.Id) }
