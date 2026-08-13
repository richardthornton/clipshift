<#
.SYNOPSIS
  Drive an interleaved paired A/B measurement session per issue #13 section 5.

.DESCRIPTION
  One command per session. Issues #11 and #15 both burned several passes on hand-driven
  harnesses; this exists so a run is not a sequence of clicks.

  Implements, from #13 section 5:
    - >=5 off/on pairs, >=120 s each                    (-Pairs, -Seconds)
    - INTERLEAVED, never all-off-then-all-on            (the loop below)
    - first 30 s of every run discarded                 (PresentMon --delay)
    - A/A control run first to find the noise floor     (-Mode AA)

  It does NOT compute statistics. analyze.py does that, so the numbers can be
  recomputed from the CSVs without re-running the machine for two hours.

.PARAMETER Mode
  AA  Both halves of every pair with ClipShift OFF. This is the control, and #13
      requires it to be run FIRST. Whatever spread it shows is the noise floor; a
      later A/B difference smaller than the floor is not a result.
  AB  Half the runs with ClipShift off, half with it on.

.PARAMETER ClipShiftCmd
  Path to the spike executable (issue #19). Required for -Mode AB, ignored for AA.
  Arguments go in -ClipShiftArgs, which is how the capture variant and NVENC preset
  get swept without rebuilding between runs.

.EXAMPLE
  # The control. Run this first, with nothing else changed.
  .\Run-Pairs.ps1 -Mode AA -Pairs 5 -Seconds 120

.EXAMPLE
  .\Run-Pairs.ps1 -Mode AB -Pairs 5 -Seconds 120 -ClipShiftCmd ..\..\..\..\spike\spike.exe -ClipShiftArgs '--variant dda-release --preset p5'

.NOTES
  PresentMon needs elevation (ETW). This script self-elevates once for the whole
  session rather than once per run, so a 20-run session costs one UAC prompt.
#>
[CmdletBinding()]
param(
    [ValidateSet('AA','AB')] [string]$Mode = 'AA',
    [int]$Pairs = 5,
    [int]$Seconds = 120,
    [int]$Warmup = 30,
    [int]$SettleSeconds = 10,
    [string]$ProcessName = 'grid2.exe',
    [string]$ClipShiftCmd = '',
    [string]$ClipShiftArgs = '',
    [string]$Label = '',
    [string]$OutRoot = '',
    [string]$PresentMon = '',
    [switch]$SkipObsCheck
)

$ErrorActionPreference = 'Stop'

# --- elevation -------------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    throw @"
PresentMon requires elevation to start an ETW trace session.

Re-run this script from an elevated PowerShell window. One elevated window covers the
whole session, however many runs it contains.

The standing alternative, if you would rather not elevate for every future session:
add your account to the local "Performance Log Users" group, then sign out and back in.
  net localgroup "Performance Log Users" $env:USERNAME /add
"@
}

# --- resolve paths ---------------------------------------------------------------
if (-not $PresentMon) {
    $PresentMon = Join-Path $PSScriptRoot '..\..\..\..\tools\PresentMon-2.5.1-x64.exe'
}
$PresentMon = [System.IO.Path]::GetFullPath($PresentMon)
if (-not (Test-Path $PresentMon)) {
    throw "PresentMon not found at $PresentMon. Run fetch-presentmon.ps1 first."
}

if ($Mode -eq 'AB' -and -not $ClipShiftCmd) {
    throw "-Mode AB needs -ClipShiftCmd (the spike from issue #19)."
}
if ($ClipShiftCmd) {
    $ClipShiftCmd = [System.IO.Path]::GetFullPath($ClipShiftCmd)
    if (-not (Test-Path $ClipShiftCmd)) { throw "Spike not found at $ClipShiftCmd" }
}

if (-not $OutRoot) { $OutRoot = Join-Path $PSScriptRoot 'results' }
$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$tag     = if ($Label) { "$Mode-$Label-$stamp" } else { "$Mode-$stamp" }
$session = Join-Path $OutRoot $tag
New-Item -ItemType Directory -Path $session -Force | Out-Null

# --- preflight -------------------------------------------------------------------
Write-Host "`n=== Preflight ===" -ForegroundColor Cyan

$game = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ProcessName)) -ErrorAction SilentlyContinue
if (-not $game) {
    throw @"
$ProcessName is not running.

Start the load first and leave it running in its benchmark loop:
  grid2.exe -benchmark <full path to benchmark_clipshift.xml>

The harness attaches to the running game rather than launching it, so that the load is
already warm and settled before the first run is recorded.
"@
}
Write-Host "Load:      $ProcessName (pid $($game.Id))"

$obs = Get-Process -Name 'obs64' -ErrorAction SilentlyContinue
if (-not $obs -and -not $SkipObsCheck) {
    throw @"
OBS is not running.

#13 section 2 makes an OBS stream non-negotiable for every measurement in this budget:
it is the standing constraint of the whole effort, and every NVENC figure inherited
from #3 was taken WITHOUT it. A number taken with OBS closed is not comparable.

Start OBS and begin streaming, then re-run. Use -SkipObsCheck only for a deliberate
off-budget probe, and label it as such.
"@
}
Write-Host "OBS:       $(if ($obs) { "running (pid $($obs.Id))" } else { 'NOT RUNNING (skipped)' })"

$gpu = & nvidia-smi --query-gpu=name,driver_version --format=csv,noheader 2>$null
Write-Host "GPU:       $gpu"
Write-Host "Mode:      $Mode   Pairs: $Pairs   Seconds: $Seconds   Warmup: $Warmup"
Write-Host "Output:    $session"

# --- run primitives --------------------------------------------------------------
function Start-ClipShift {
    if (-not $ClipShiftCmd) { return $null }
    $p = if ($ClipShiftArgs) {
        Start-Process -FilePath $ClipShiftCmd -ArgumentList $ClipShiftArgs -PassThru -WindowStyle Minimized
    } else {
        Start-Process -FilePath $ClipShiftCmd -PassThru -WindowStyle Minimized
    }
    Start-Sleep -Seconds 3   # let capture and the encoder session come up before recording
    if ($p.HasExited) { throw "Spike exited immediately (code $($p.ExitCode)). Aborting session." }
    return $p
}

function Stop-ClipShift($p) {
    if (-not $p) { return }
    if (-not $p.HasExited) {
        # CloseMainWindow first so the spike can finalise its own counters
        [void]$p.CloseMainWindow()
        if (-not $p.WaitForExit(10000)) { $p | Stop-Process -Force }
    }
}

function Invoke-Run {
    param([int]$Index, [string]$Arm)

    $csv = Join-Path $session ("run{0:d2}-{1}.csv" -f $Index, $Arm)
    Write-Host ("`n[{0:d2}] arm={1}  recording {2}s after {3}s warmup" -f $Index, $Arm, $Seconds, $Warmup) -ForegroundColor Yellow

    $proc = $null
    try {
        if ($Arm -eq 'on') { $proc = Start-ClipShift }

        $pmArgs = @(
            '--process_name', $ProcessName
            '--output_file',  $csv
            '--delay',        $Warmup          # #13 s5.2: discard the first 30 s
            '--timed',        $Seconds
            '--terminate_after_timed'
            '--no_console_stats'
            '--stop_existing_session'
            '--qpc_time'                       # same clock domain as #5's master clock
            '--v1_metrics'                     # #13's budget is written in v1 column names
            '--track_gpu_video'                # separates NVENC engine work from the rest
        )

        $pm = Start-Process -FilePath $PresentMon -ArgumentList $pmArgs -PassThru -NoNewWindow -Wait
        if ($pm.ExitCode -ne 0) { Write-Warning "PresentMon exited $($pm.ExitCode) on run $Index" }
    }
    finally {
        Stop-ClipShift $proc
    }

    if (-not (Test-Path $csv)) { throw "Run $Index produced no CSV. Aborting rather than continuing with a hole in the session." }
    $rows = (Get-Content $csv | Measure-Object -Line).Lines - 1
    Write-Host ("     -> {0} frames" -f $rows)

    return [pscustomobject]@{ index = $Index; arm = $Arm; csv = (Split-Path $csv -Leaf); frames = $rows }
}

# --- the session -----------------------------------------------------------------
Write-Host "`n=== Session ===" -ForegroundColor Cyan
$runs = @()
$i = 0
for ($p = 1; $p -le $Pairs; $p++) {
    Write-Host "`n--- pair $p of $Pairs ---" -ForegroundColor Magenta
    # Interleaved: both arms of a pair are adjacent in time, so thermal drift and clock
    # behaviour land on BOTH arms rather than masquerading as capture overhead.
    foreach ($arm in @('off', $(if ($Mode -eq 'AA') { 'off' } else { 'on' }))) {
        $i++
        $runs += Invoke-Run -Index $i -Arm $arm
        if ($SettleSeconds -gt 0) { Start-Sleep -Seconds $SettleSeconds }
    }
}

# --- manifest --------------------------------------------------------------------
# Everything needed to interpret the CSVs later, including what would invalidate them.
$manifest = [ordered]@{
    mode            = $Mode
    label           = $Label
    started         = $stamp
    pairs           = $Pairs
    seconds         = $Seconds
    warmupSeconds   = $Warmup
    settleSeconds   = $SettleSeconds
    processName     = $ProcessName
    clipShiftCmd    = $ClipShiftCmd
    clipShiftArgs   = $ClipShiftArgs
    obsRunning      = [bool]$obs
    gpu             = $gpu
    presentMon      = (Split-Path $PresentMon -Leaf)
    presentMonSha256= (Get-FileHash $PresentMon -Algorithm SHA256).Hash
    host            = $env:COMPUTERNAME
    runs            = $runs
}
$manifest | ConvertTo-Json -Depth 5 | Out-File (Join-Path $session 'manifest.json') -Encoding utf8

Write-Host "`n=== Done ===" -ForegroundColor Green
Write-Host "Session: $session"
Write-Host "Analyse: python analyze.py `"$session`""
