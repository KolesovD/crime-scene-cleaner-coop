# Two-instance test on one PC: local\game-copy is the host, local\game-copy-2 the client (hard-linked).
# Both bots load save slot 1 into the same job, connect over localhost UDP and run CSCCOOP_TEST=$Test.
param([string]$Test = "items", [int]$QuitAfter = 40, [int]$LossPct = 0, [int]$LatencyMs = 0, [int]$TimeoutSec = 480, [string]$Mutant = "", [switch]$SkipIntro, [switch]$NoBackground, [switch]$Loud, [int]$Mission = -1)
# Quiet by default (the owner uses the PC meanwhile): small windows stacked on the second monitor (the
# primary's right neighbour, else the primary's right edge), lowest quality, 30 fps, no sound. -Loud: as before.
# cctv drives on from the first job to the Smart House inside the bots' time: 40 s ends it before the checks.
if ($Test -eq "cctv" -and -not $PSBoundParameters.ContainsKey("QuitAfter")) { $QuitAfter = 75 }
# -Mission <id>: the host drives on from the first job to that job (11 = Game Dev, "Developers' Hell"), the client follows.
if ($Test -eq "cctv" -and $Mission -lt 0) { $Mission = 5 }
if ($Mission -ge 0 -and -not $PSBoundParameters.ContainsKey("QuitAfter")) { $QuitAfter = [Math]::Max($QuitAfter, 75) }
# The bots start the test in that job's scene (cctv keeps its own flow, which starts on the way).
$testScene = @{ 11 = "GameDevOffice"; 5 = "SmartHouse" }[$Mission]
if ($Test -eq "cctv" -or -not $testScene) { $testScene = "" }
Add-Type -AssemblyName System.Windows.Forms
$screens = [System.Windows.Forms.Screen]::AllScreens
$mon = ($screens | Where-Object { -not $_.Primary } | Select-Object -First 1)
if (-not $mon) { $mon = $screens[0] }
$b = $mon.Bounds
$winW = [Math]::Min(960, $b.Width - 40); $winH = [int]($winW * 9 / 16)
$root = Split-Path $PSScriptRoot -Parent
Get-Process CrimeCleaner -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\local\game-copy*" } | Stop-Process -Force
Start-Sleep 2
$copies = @(
    @{ Name = "host";   Copy = "game-copy";   Port = 27901; Y = $b.Y + 20 },
    @{ Name = "client"; Copy = "game-copy-2"; Port = 27902; Y = $b.Y + 20 + $winH + 60 }
)
foreach ($c in $copies) {
    $bak = Join-Path $root "local\saves-before-$($c.Name)"
    Remove-Item $bak -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "local\$($c.Copy)\CrimeCleaner_Data\Saves") $bak -Recurse
}
$procs = @()
foreach ($c in $copies) {
    $csv = Join-Path $root "local\$Test-$($c.Name).csv"
    Remove-Item $csv -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $root "local\keys-$($c.Name).txt") -ErrorAction SilentlyContinue
    $quiet = @{}
    if (-not $Loud) { $quiet = @{ CSCCOOP_QUIET = "1"; CSCCOOP_WINDOW = "$($b.X + 20),$($c.Y),$winW,$winH,Crime Scene Cleaner - BOT $($c.Name.ToUpper())" } }
    $procs += & (Join-Path $PSScriptRoot "run-game.ps1") -Copy $c.Copy -Width $winW -Height $winH -Monitor $(if ($Loud) { 0 } else { 2 }) -Env ($quiet + @{
        CSCCOOP_TEST = $Test; CSCCOOP_ROLE = $c.Name; CSCCOOP_PORT = "$($c.Port)"; CSCCOOP_HOST = "27901"
        CSCCOOP_CSV = $csv; CSCCOOP_QUIT_AFTER = "$QuitAfter"; CSCCOOP_LOSS_PCT = "$LossPct"; CSCCOOP_LATENCY_MS = "$LatencyMs"
        CSCCOOP_NO_BG = $(if ($NoBackground) { "1" } else { "" }); CSCCOOP_MUTANT = $Mutant; CSCCOOP_SKIP_INTRO = $(if ($SkipIntro) { "1" } else { "" })
        CSCCOOP_DUMP_KEYS = (Join-Path $root "local\keys-$($c.Name).txt"); CSCCOOP_TEST_SCENE = $testScene
        # The bot waits this long in the main menu before loading the slot (default 25 s; 10 s was enough in all suites).
        CSCCOOP_MENU_WAIT = "10"
        CSCCOOP_MISSION = $(if ($Mission -ge 0 -and $c.Name -eq "host") { "$Mission" } else { "" }); CSCCOOP_ANY_JOB = $(if ($Mission -ge 0) { "1" } else { "" })
        # jobs: both players start from the main menu's Co-op screen (host: Host -> slot; client: Join -> slot -> play).
        CSCCOOP_VIA_MENU = $(if ($Test -in "jobs", "laptop") { "1" } else { "" }); CSCCOOP_SHOT_DIR = (Join-Path $root "local")
        # ... with "Новая игра" in the empty slot 3 (Slot3.es3; slot 1 of the test copies is a save inside a job).
        CSCCOOP_SLOT = $(if ($Test -in "jobs", "laptop") { "3" } else { "" }); CSCCOOP_MENU_NEW = $(if ($Test -in "jobs", "laptop") { "1" } else { "" })
    })
    Start-Sleep 3
}
$t = 0
while ($t -lt $TimeoutSec -and ($procs | Where-Object { -not $_.HasExited })) { Start-Sleep 5; $t += 5 }
foreach ($p in $procs) { if (-not $p.HasExited) { "timeout: killing pid $($p.Id)"; Stop-Process -Id $p.Id -Force } }
"elapsed ${t}s"
foreach ($c in $copies) {
    "=== $($c.Name)"
    Select-String -Path (Join-Path $root "local\$($c.Copy)\BepInEx\LogOutput.log") -Pattern "COOP|BOT (ready|scene|StartGame|done)|ITEMTEST|Exception" |
        Where-Object { $_.Line -notmatch "unsupported parameter|COOP status" } | Select-Object -Last 40 |
        ForEach-Object { $_.Line -replace '^\[\w+\s*:Crime Scene Cleaner Co-op\] ', '' }
}
# Keep this run's logs: the next run overwrites the copies' LogOutput.log.
$keep = Join-Path $root ("local\last-$Test" + $(if ($Mutant) { "-$Mutant" } else { "" }))
New-Item -ItemType Directory -Force $keep | Out-Null
Copy-Item (Join-Path $root "local\game-copy\BepInEx\LogOutput.log") (Join-Path $keep "host.log") -Force
Copy-Item (Join-Path $root "local\game-copy-2\BepInEx\LogOutput.log") (Join-Path $keep "client.log") -Force
$report = if ($Test -eq "items") { "item_report.py" } else { "$($Test)_report.py" }
python -I (Join-Path $PSScriptRoot $report) (Join-Path $root "local\$Test-host.csv") (Join-Path $root "local\$Test-client.csv") (Join-Path $root "local\game-copy\BepInEx\LogOutput.log") (Join-Path $root "local\game-copy-2\BepInEx\LogOutput.log")
# Tests that leave a job write the copies' saves (quick save, money): put them back.
foreach ($c in $copies) {
    $bak = Join-Path $root "local\saves-before-$($c.Name)"
    if (Test-Path $bak) {
        $saves = Join-Path $root "local\$($c.Copy)\CrimeCleaner_Data\Saves"
        Remove-Item $saves -Recurse -Force
        Copy-Item $bak $saves -Recurse
        "restored saves of $($c.Copy)"
    }
}
