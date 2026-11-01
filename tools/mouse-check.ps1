param([int]$Seconds = 120, [int]$Every = 2)
# Checks that test launches leave the mouse alone: every few seconds prints which process owns the focused window
# and whether the cursor is clipped to a rectangle smaller than the screen (what a locked/confined game cursor does).
Add-Type -Namespace CscTest -Name Mouse -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetClipCursor(out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
'@ -ErrorAction SilentlyContinue
$sw = [CscTest.Mouse]::GetSystemMetrics(78); $sh = [CscTest.Mouse]::GetSystemMetrics(79)   # virtual screen size
$end = (Get-Date).AddSeconds($Seconds); $t0 = Get-Date
while ((Get-Date) -lt $end) {
    $r = New-Object CscTest.Mouse+RECT; [void][CscTest.Mouse]::GetClipCursor([ref]$r)
    $clipped = ($r.R - $r.L) -lt $sw -or ($r.B - $r.T) -lt $sh
    $pid0 = 0; [void][CscTest.Mouse]::GetWindowThreadProcessId([CscTest.Mouse]::GetForegroundWindow(), [ref]$pid0)
    $p = Get-Process -Id $pid0 -ErrorAction SilentlyContinue
    "{0,5:F0}s focus={1} clipped={2}{3}" -f ((Get-Date) - $t0).TotalSeconds, $(if ($p) { $p.ProcessName } else { '?' }), $clipped, $(if ($clipped) { " ($($r.L),$($r.T))-($($r.R),$($r.B))" } else { '' })
    Start-Sleep $Every
}
