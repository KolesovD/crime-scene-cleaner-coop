# One-copy probe (Bot CSCCOOP_TEST=$Test, e.g. toolprobe), quiet on the second monitor; the copy's saves
# are restored afterwards and the log kept in local\last-$Test\probe.log.
param([string]$Test = "toolprobe", [int]$TimeoutSec = 400, [int]$Mission = -1)
$root = Split-Path $PSScriptRoot -Parent
Get-Process CrimeCleaner -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\local\game-copy*" } | Stop-Process -Force
Start-Sleep 2
Add-Type -AssemblyName System.Windows.Forms
$mon = ([System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1)
if (-not $mon) { $mon = [System.Windows.Forms.Screen]::PrimaryScreen }
$b = $mon.Bounds
$saves = Join-Path $root "local\game-copy\CrimeCleaner_Data\Saves"
$bak = Join-Path $root "local\saves-before-probe"
Remove-Item $bak -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item $saves $bak -Recurse
$p = & (Join-Path $PSScriptRoot "run-game.ps1") -Copy "game-copy" -Width 960 -Height 540 -Monitor 2 -Env @{
    CSCCOOP_TEST = $Test; CSCCOOP_QUIET = "1"; CSCCOOP_SHOT_DIR = (Join-Path $root "local")
    # -Mission <id>: drive on from the first job to that one (11 = Game Dev)
    CSCCOOP_MISSION = $(if ($Mission -ge 0) { "$Mission" } else { "" }); CSCCOOP_ANY_JOB = $(if ($Mission -ge 0) { "1" } else { "" })
    CSCCOOP_TEST_SCENE = $(if ($Mission -eq 11) { "GameDevOffice" } else { "" })
    CSCCOOP_WINDOW = "$($b.X + 20),$($b.Y + 20),960,540,Crime Scene Cleaner - PROBE"
}
$t = 0
while ($t -lt $TimeoutSec -and -not $p.HasExited) { Start-Sleep 5; $t += 5 }
if (-not $p.HasExited) { "timeout: killing pid $($p.Id)"; Stop-Process -Id $p.Id -Force }
"elapsed ${t}s"
Start-Sleep 2
Remove-Item $saves -Recurse -Force
Copy-Item $bak $saves -Recurse
"restored saves of game-copy"
$keep = Join-Path $root "local\last-$Test"
New-Item -ItemType Directory -Force $keep | Out-Null
Copy-Item (Join-Path $root "local\game-copy\BepInEx\LogOutput.log") (Join-Path $keep "probe.log") -Force
