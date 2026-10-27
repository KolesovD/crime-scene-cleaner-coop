# Manual two-player test on one PC: local\game-copy is the host, local\game-copy-2 the client, connected over
# localhost UDP (no Steam friend needed, no bots). Two windows side by side, titled HOST and CLIENT.
# -Mission N: both load save slot 1 by themselves and the host starts job N (the client follows); the copies
# run at the lowest graphics settings, with sound. -1: the old way, everything by hand.
# -ViaMenu: both start in the main menu with no session; the players use "Кооператив" there (host: Хостить; client:
# Присоединиться, the localhost host is listed). The copies' saves are backed up first; -Restore puts them back
# (the bot suites need slot 3 empty).
param([int]$Width = 1270, [int]$Height = 714, [int]$Mission = -1, [switch]$ViaMenu, [switch]$Restore)
$root = Split-Path $PSScriptRoot -Parent
Get-Process CrimeCleaner -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\local\game-copy*" } | Stop-Process -Force
Start-Sleep 2
foreach ($copy in "game-copy", "game-copy-2") {
    $saves = Join-Path $root "local\$copy\CrimeCleaner_Data\Saves"
    $bak = Join-Path $root "local\saves-before-manual-$copy"
    if ($Restore) {
        if (Test-Path $bak) { Remove-Item $saves -Recurse -Force; Copy-Item $bak $saves -Recurse; "restored saves of $copy" } else { "no backup for $copy" }
    } elseif ($ViaMenu) {
        Remove-Item $bak -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item $saves $bak -Recurse
    }
}
if ($Restore) { return }

# The game's own window is the UnityWndClass window of the process. Not Process.MainWindowHandle: that can
# be the BepInEx console, and moving/titling it instead swapped the HOST/CLIENT labels in the owner's test.
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Win {
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetWindowText(IntPtr h, string text);
    public static IntPtr GameWindow(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) => {
            uint wp; GetWindowThreadProcessId(h, out wp);
            var sb = new StringBuilder(64); GetClassName(h, sb, 64);
            if (wp == pid && sb.ToString() == "UnityWndClass") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

$copies = @(
    @{ Name = "host";   Copy = "game-copy";   Port = 27901; X = 0 },
    @{ Name = "client"; Copy = "game-copy-2"; Port = 27902; X = $Width + 10 }
)
$procs = @()
foreach ($c in $copies) {
    $p = & (Join-Path $PSScriptRoot "run-game.ps1") -Copy $c.Copy -Width $Width -Height $Height -Env @{
        CSCCOOP_ROLE = $c.Name; CSCCOOP_PORT = "$($c.Port)"; CSCCOOP_HOST = "27901"
        CSCCOOP_TEST = $(if ($Mission -ge 0) { "manual" } else { "" }); CSCCOOP_LOWGFX = $(if ($Mission -ge 0 -or $ViaMenu) { "1" } else { "" })
        CSCCOOP_VIA_MENU = $(if ($ViaMenu) { "1" } else { "" })
        CSCCOOP_MISSION = $(if ($Mission -ge 0 -and $c.Name -eq "host") { "$Mission" } else { "" })
        CSCCOOP_WINDOW = "$($c.X),40,$Width,$Height,Crime Scene Cleaner - $($c.Name.ToUpper())"
    }
    $procs += @{ P = $p; C = $c }
    Start-Sleep 3
}
foreach ($e in $procs) {
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 90 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep 1; $h = [Win]::GameWindow([uint32]$e.P.Id) }
    if ($h -ne [IntPtr]::Zero) {
        [Win]::MoveWindow($h, $e.C.X, 40, $Width + 16, $Height + 39, $true) | Out-Null
        [Win]::SetWindowText($h, "Crime Scene Cleaner - $($e.C.Name.ToUpper())") | Out-Null
    }
    "$($e.C.Name): pid $($e.P.Id), copy $($e.C.Copy), window $(if ($h -ne [IntPtr]::Zero) { 'placed at x ' + $e.C.X } else { 'NOT FOUND' })"
}
