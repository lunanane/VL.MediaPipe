<#
.SYNOPSIS
    Headless texture-input harness: runs test\TextureInputHarness.vl in vvvv and checks the sentinels it logs.

.DESCRIPTION
    1. Copies the fixture image (default D:\claude\depth-handtracking\tools\hand-tracking\fixtures\hand_rgb_sample.jpg,
       read-only) to results\texture-harness\hand_rgb_sample.jpg, where the harness document's FileTexture loads it.
    2. Holds the team lock D:\claude\_vl-dev\vvvv.lock and starts
         vvvv.exe -o D:\claude\_vl-dev\VL.MediaPipe\test\TextureInputHarness.vl -m --log
                  --package-repositories D:\claude\_vl-dev --nuget-path D:\claude\_vl-dev\nugets
    3. Follows vvvv's newest log (Documents\vvvv\gamma-preview\vvvv_<stamp>.log) for lines of category [VL.MediaPipe]
       written by TextureInputProbe, until "TEXTURE HARNESS DONE" or the timeout.
    4. Kills the vvvv process tree it started (by PID), waits until every process of it has exited, releases the lock.

    Pass: a FIRST HAND line with landmarks=21, FRAME IDS / LATENCY summary lines (>= 100 samples), DONE, and no CRIT
    line. FAIL lines are listed; they fail the run unless -AllowFailLines.
    Exit: 0 pass, 1 fail, 2 setup problem. Log copy and a summary JSON go to results\texture-harness\.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-texture-harness.ps1
#>
[CmdletBinding()]
param(
    [string] $Image = 'D:\claude\depth-handtracking\tools\hand-tracking\fixtures\hand_rgb_sample.jpg',
    [int] $TimeoutSeconds = 300,
    [int] $MinSamples = 100,
    [switch] $AllowFailLines,
    [switch] $KeepOpen
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\MpCommon.ps1')

$root = Get-MpRepoRoot
$vvvv = Join-Path (Get-MpVvvvDir) 'vvvv.exe'
$outDir = Join-Path $root 'results\texture-harness'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
foreach ($p in @($vvvv, $Image, (Join-Path $root 'test\TextureInputHarness.vl'), (Join-Path $root 'lib\net8.0\VL.MediaPipe.dll'),
                 (Join-Path $root 'test\lib\net8.0\VL.MediaPipe.TextureHarness.dll'), (Join-Path $root 'web\index.html'))) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "SETUP: not found: $p"; exit 2 }
}
$imageCopy = Join-Path $outDir 'hand_rgb_sample.jpg'
Copy-Item -LiteralPath $Image -Destination $imageCopy -Force
$sha = (Get-FileHash -LiteralPath $imageCopy -Algorithm SHA256).Hash
Write-Host "image    : $Image -> $imageCopy (sha256 $sha)"

$junction = Initialize-MpDevJunction
$devRepo = Get-MpDevRepo
$nugets = Get-MpNugetPath
$doc = Join-Path $junction 'test\TextureInputHarness.vl'
$lock = $null
$proc = $null
$logFile = $null
$lines = @()
$stopped = $true
try {
    $lock = Enter-MpVvvvLock -Label 'VL.MediaPipe scripts\run-texture-harness.ps1'
    Write-Host "lock     : acquired $lock"
    $launchedAt = Get-Date
    $proc = Start-Process -FilePath $vvvv -PassThru -ArgumentList (ConvertTo-MpArgumentString @(
        '-o', $doc, '-m', '--log', '--package-repositories', $devRepo, '--nuget-path', $nugets))
    $null = $proc.Handle
    Write-Host "vvvv     : pid $($proc.Id), waiting up to $TimeoutSeconds s for TEXTURE HARNESS DONE"
    $deadline = $launchedAt.AddSeconds($TimeoutSeconds)
    $lastReported = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
        if (-not $logFile) {
            $candidate = Find-MpVvvvLog -Since $launchedAt
            if ($candidate) { $logFile = $candidate.FullName; Write-Host "log      : $logFile" }
        }
        if ($logFile) {
            $text = Read-MpSharedText $logFile
            if ($text) {
                $lines = @($text -split "`r?`n")
                $ours = @($lines | Where-Object { $_ -match '\[VL\.MediaPipe\]' })
                for ($i = $lastReported; $i -lt $ours.Count; $i++) { Write-Host "  $($ours[$i])" }
                $lastReported = $ours.Count
                if ($ours | Where-Object { $_ -match 'TEXTURE HARNESS DONE' }) { Start-Sleep -Seconds 2; $lines = @(((Read-MpSharedText $logFile) -split "`r?`n")); break }
            }
        }
        if ($proc.HasExited) { Write-Host "vvvv exited by itself (code $($proc.ExitCode))"; break }
    }
    Write-Host ("waited   : {0:F0} s" -f ((Get-Date) - $launchedAt).TotalSeconds)
} finally {
    if ($proc -and -not $proc.HasExited) {
        if ($KeepOpen) {
            Write-Host "-KeepOpen: vvvv pid $($proc.Id) stays open; the lock is held until it exits."
            $proc.WaitForExit()
        } else {
            Write-Host "closing  : vvvv pid $($proc.Id) and its child processes"
            $stopped = Stop-MpProcessTreeAndWait -Id $proc.Id
            if (-not $stopped) { Write-Host 'WARNING: a process of the vvvv tree did not exit within 120 s; keeping the lock.' }
        }
    }
    if ($lock -and $stopped) { Exit-MpVvvvLock $lock; Write-Host 'lock     : released' }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($logFile) {
    $text = Read-MpSharedText $logFile
    if ($text) { [System.IO.File]::WriteAllText((Join-Path $outDir "vvvv-$stamp.log.txt"), $text); $lines = @($text -split "`r?`n") }
}
$ours = @($lines | Where-Object { $_ -match '\[VL\.MediaPipe\]' })
$crit = @($lines | Where-Object { $_ -match '\tCRIT\t' })
$fail = @($lines | Where-Object { $_ -match '\tFAIL\t' })
$failures = New-Object System.Collections.Generic.List[string]
if (-not $logFile) { $failures.Add('vvvv wrote no log') }
$firstHand = $ours | Where-Object { $_ -match 'FIRST HAND landmarks=(\d+)' } | Select-Object -First 1
if (-not $firstHand) { $failures.Add('no FIRST HAND line: no hand was detected') }
elseif (-not ($firstHand -match 'landmarks=21\b')) { $failures.Add("first hand did not have 21 landmarks: $firstHand") }
$latencyLine = $ours | Where-Object { $_ -match 'LATENCY n=(\d+)' } | Select-Object -Last 1
if (-not $latencyLine) { $failures.Add('no LATENCY line') }
elseif ([int]([regex]::Match($latencyLine, 'n=(\d+)').Groups[1].Value) -lt $MinSamples) { $failures.Add("fewer than $MinSamples latency samples: $latencyLine") }
if (-not ($ours | Where-Object { $_ -match 'FRAME IDS' })) { $failures.Add('no FRAME IDS line') }
if (-not ($ours | Where-Object { $_ -match 'TEXTURE HARNESS DONE' })) { $failures.Add("no TEXTURE HARNESS DONE within $TimeoutSeconds s") }
if ($crit.Count -gt 0) { $failures.Add("$($crit.Count) CRIT line(s) in the vvvv log") }
if ($fail.Count -gt 0 -and -not $AllowFailLines) { $failures.Add("$($fail.Count) FAIL line(s) in the vvvv log") }

Write-Host ''
Write-Host '--- [VL.MediaPipe] lines ---'
if ($ours.Count -eq 0) { Write-Host '(none)' } else { $ours | ForEach-Object { Write-Host $_ } }
if ($crit.Count + $fail.Count -gt 0) {
    Write-Host ''
    Write-Host '--- FAIL / CRIT lines (first 30) ---'
    @($crit + $fail) | Select-Object -First 30 | ForEach-Object { Write-Host $_ }
}
$summary = [ordered]@{ image = $Image; imageSha256 = $sha; log = $logFile; sentinelLines = $ours; critLines = $crit.Count; failLines = $fail.Count; failures = @($failures) }
($summary | ConvertTo-Json -Depth 4) | Out-File -Encoding utf8 (Join-Path $outDir "summary-$stamp.json")
Write-Host ''
if ($failures.Count -gt 0) { Write-Host 'FAIL:'; $failures | ForEach-Object { Write-Host "  - $_" }; exit 1 }
Write-Host 'PASS: texture input tracked the fixture with echoed frame ids and latency statistics.'
exit 0
