# Census of every job (Bot "census"): one test copy, quiet on the second monitor, starts each mission in turn
# from the hub and writes local\census.csv (scene,type,count). The copy's saves are restored afterwards.
param([int]$TimeoutSec = 3000)
$root = Split-Path $PSScriptRoot -Parent
Get-Process CrimeCleaner -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\local\game-copy*" } | Stop-Process -Force
Start-Sleep 2
Add-Type -AssemblyName System.Windows.Forms
$mon = ([System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1)
if (-not $mon) { $mon = [System.Windows.Forms.Screen]::PrimaryScreen }
$b = $mon.Bounds
$saves = Join-Path $root "local\game-copy\CrimeCleaner_Data\Saves"
$bak = Join-Path $root "local\saves-before-census"
Remove-Item $bak -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item $saves $bak -Recurse
$csv = Join-Path $root "local\census.csv"
Remove-Item $csv -ErrorAction SilentlyContinue
$p = & (Join-Path $PSScriptRoot "run-game.ps1") -Copy "game-copy" -Width 960 -Height 540 -Monitor 2 -Env @{
    CSCCOOP_TEST = "census"; CSCCOOP_CSV = $csv; CSCCOOP_QUIET = "1"; CSCCOOP_SKIP_INTRO = "1"
    CSCCOOP_WINDOW = "$($b.X + 20),$($b.Y + 20),960,540,Crime Scene Cleaner - CENSUS"
}
$t = 0
while ($t -lt $TimeoutSec -and -not $p.HasExited) { Start-Sleep 5; $t += 5 }
if (-not $p.HasExited) { "timeout: killing pid $($p.Id)"; Stop-Process -Id $p.Id -Force }
"elapsed ${t}s"
Start-Sleep 2
Remove-Item $saves -Recurse -Force
Copy-Item $bak $saves -Recurse
"restored saves of game-copy"
Copy-Item (Join-Path $root "local\game-copy\BepInEx\LogOutput.log") (Join-Path $root "local\census.log") -Force
Select-String -Path (Join-Path $root "local\census.log") -Pattern "CENSUS|Exception|BOT scene" | ForEach-Object { $_.Line -replace '^\[\w+\s*:Crime Scene Cleaner Co-op\] ', '' }
