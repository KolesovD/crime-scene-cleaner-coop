# Starts one test copy of the game (never the Steam install), windowed, with its own log.
# Extra CSCCOOP_* settings are passed as -Env @{ CSCCOOP_ROLE = "host"; ... }.
param([string]$Copy = "game-copy", [hashtable]$Env = @{}, [int]$Width = 960, [int]$Height = 540, [int]$Monitor = 0)
$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $root "local\$Copy"
if (-not (Test-Path (Join-Path $dir "CrimeCleaner.exe"))) { throw "no test copy at $dir" }
# Old log out of the way, so a caller waiting for a line can't match the previous run's.
Remove-Item (Join-Path $dir "BepInEx\LogOutput.log") -ErrorAction SilentlyContinue
$env:SteamAppId = "1040200"; $env:SteamGameId = "1040200"
# Quiet bot runs give focus back to the window in front now (HandsOff.cs): the game takes it when its window opens.
if ($Env["CSCCOOP_QUIET"] -eq "1" -and -not $Env.ContainsKey("CSCCOOP_FOCUS_BACK")) {
    Add-Type -Namespace CscCoop -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();' -ErrorAction SilentlyContinue
    $Env["CSCCOOP_FOCUS_BACK"] = [string][CscCoop.Win]::GetForegroundWindow().ToInt64()
}
foreach ($k in $Env.Keys) { Set-Item "env:$k" $Env[$k] }
$log = Join-Path $root "local\$Copy-unity.log"
$p = Start-Process (Join-Path $dir "CrimeCleaner.exe") -WorkingDirectory $dir -PassThru `
    -ArgumentList "-screen-fullscreen 0 -screen-width $Width -screen-height $Height $(if ($Monitor -gt 0) { "-monitor $Monitor" }) -logFile `"$log`""
foreach ($k in $Env.Keys) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
$p
