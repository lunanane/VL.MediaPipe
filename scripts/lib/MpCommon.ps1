# Shared helpers for scripts\*.ps1 of the VL.MediaPipe fork. Dot-source it:
#   . (Join-Path $PSScriptRoot 'lib\MpCommon.ps1')
#
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less files in the ANSI code page.
#
# Team rules these helpers implement (RENDERPC01, 2026-09):
#   - Only ONE vvvv.exe / vvvvc.exe at a time across everyone on the machine: hold D:\claude\_vl-dev\vvvv.lock
#     (created with CreateNew, polled every 10 s, up to 45 min) around every run, and delete it only after every
#     process the script started has actually exited.
#   - Kill only processes this script started, by PID (the vvvv process tree, recorded before killing).
#   - Dev packages come from --package-repositories D:\claude\_vl-dev (a junction VL.MediaPipe -> this repo)
#     and NuGet packages go to --nuget-path D:\claude\_vl-dev\nugets, never into the owner's folders.

$script:MpRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path.TrimEnd('\')
$script:MpLockPath = 'D:\claude\_vl-dev\vvvv.lock'

function Get-MpRepoRoot { return $script:MpRepoRoot }

function Get-MpVvvvDir {
    if ($env:MP_VVVV_DIR) { return $env:MP_VVVV_DIR }
    return 'C:\Program Files\vvvv\vvvv_gamma_7.4-0008-g793e4b395d-win-x64'
}

function Get-MpDevRepo {
    if ($env:MP_VL_DEV) { return $env:MP_VL_DEV }
    return 'D:\claude\_vl-dev'
}

function Get-MpNugetPath {
    if ($env:MP_NUGET_PATH) { return $env:MP_NUGET_PATH }
    return (Join-Path (Get-MpDevRepo) 'nugets')
}

function Get-MpNode {
    if ($env:MP_NODE) { return $env:MP_NODE }
    return 'D:\claude\_tools\node\node.exe'
}

# Start-Process in Windows PowerShell 5.1 joins -ArgumentList with spaces and does not quote.
function ConvertTo-MpArgumentString([string[]] $Arguments) {
    $parts = foreach ($a in $Arguments) {
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
    }
    return ($parts -join ' ')
}

function Enter-MpVvvvLock {
    param(
        [Parameter(Mandatory)] [string] $Label,
        [int] $TimeoutMinutes = 45,
        [int] $PollSeconds = 10,
        [string] $Path = $script:MpLockPath
    )
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $announced = $false
    while ($true) {
        try {
            $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
            $bytes = [System.Text.Encoding]::ASCII.GetBytes("$Label | pid $PID | $(Get-Date -Format o)")
            $fs.Write($bytes, 0, $bytes.Length)
            $fs.Close()
            return $Path
        } catch [System.IO.IOException] {
            if ((Get-Date) -ge $deadline) {
                $holder = ''
                try { $holder = Get-Content -Raw $Path -ErrorAction Stop } catch { }
                throw "vvvv lock not acquired within $TimeoutMinutes min ($Path held by: $holder)"
            }
            if (-not $announced) {
                $holder = ''
                try { $holder = Get-Content -Raw $Path -ErrorAction Stop } catch { }
                Write-Host "      waiting for vvvv lock (held by: $holder) ..."
                $announced = $true
            }
            Start-Sleep -Seconds $PollSeconds
        }
    }
}

function Exit-MpVvvvLock {
    param([string] $Path = $script:MpLockPath)
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    }
}

# All descendants of a process (CEF helpers, exporter child processes), as (Id, CreationDate) pairs so a reused
# PID is never mistaken for one of ours.
function Get-MpProcessTree([int] $RootId) {
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Select-Object ProcessId, ParentProcessId, CreationDate)
    $result = New-Object System.Collections.Generic.List[object]
    $root = $all | Where-Object { $_.ProcessId -eq $RootId } | Select-Object -First 1
    if ($root) { $result.Add($root) }
    $frontier = @($RootId)
    while ($frontier.Count -gt 0) {
        $children = @($all | Where-Object { $frontier -contains $_.ParentProcessId -and $_.ProcessId -ne $_.ParentProcessId })
        $children = @($children | Where-Object { $id = $_.ProcessId; -not ($result | Where-Object { $_.ProcessId -eq $id }) })
        foreach ($c in $children) { $result.Add($c) }
        $frontier = @($children | ForEach-Object { $_.ProcessId })
    }
    return $result
}

function Test-MpProcessAlive($Entry) {
    $p = Get-CimInstance Win32_Process -Filter "ProcessId = $($Entry.ProcessId)" -ErrorAction SilentlyContinue
    return ($null -ne $p -and $p.CreationDate -eq $Entry.CreationDate)
}

# Kills the tree of a process this script started and waits until every member has exited.
function Stop-MpProcessTreeAndWait {
    param([Parameter(Mandatory)] [int] $Id, [int] $TimeoutSeconds = 120)
    $tree = @(Get-MpProcessTree $Id)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & taskkill.exe /PID $Id /T /F *> $null } catch { } finally { $ErrorActionPreference = $prev }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $alive = @($tree | Where-Object { Test-MpProcessAlive $_ })
        if ($alive.Count -eq 0) { return $true }
        foreach ($a in $alive) {
            try { Stop-Process -Id $a.ProcessId -Force -ErrorAction SilentlyContinue } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# Waits for every process of a tree that exited on its own (e.g. vvvvc) to be gone.
function Wait-MpProcessTreeExit {
    param([Parameter(Mandatory)] [object[]] $Tree, [int] $TimeoutSeconds = 120)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (@($Tree | Where-Object { Test-MpProcessAlive $_ }).Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# Runs a console program with stdout/stderr captured. Returns ExitCode (-1 on timeout), TimedOut, Id.
# Returns only after the process and every child it started have exited.
function Invoke-MpNative {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [string[]] $Arguments = @(),
        [Parameter(Mandatory)] [string] $StdOutPath,
        [Parameter(Mandatory)] [string] $StdErrPath,
        [int] $TimeoutSeconds = 900
    )
    $p = Start-Process -FilePath $FilePath -ArgumentList (ConvertTo-MpArgumentString $Arguments) -PassThru -NoNewWindow `
        -RedirectStandardOutput $StdOutPath -RedirectStandardError $StdErrPath
    $null = $p.Handle
    $seen = @{}
    $tree = New-Object System.Collections.Generic.List[object]
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $p.HasExited) {
        foreach ($e in @(Get-MpProcessTree $p.Id)) {
            $key = "$($e.ProcessId)|$($e.CreationDate)"
            if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $tree.Add($e) }
        }
        if ((Get-Date) -ge $deadline) {
            $null = Stop-MpProcessTreeAndWait -Id $p.Id
            $null = Wait-MpProcessTreeExit -Tree $tree.ToArray()
            return [pscustomobject]@{ ExitCode = -1; TimedOut = $true; Id = $p.Id }
        }
        Start-Sleep -Milliseconds 500
    }
    $p.WaitForExit()
    $null = Wait-MpProcessTreeExit -Tree $tree.ToArray()
    return [pscustomobject]@{ ExitCode = $p.ExitCode; TimedOut = $false; Id = $p.Id }
}

# D:\claude\_vl-dev\VL.MediaPipe -> this repo, so --package-repositories D:\claude\_vl-dev finds the package.
function Initialize-MpDevJunction {
    $devRepo = Get-MpDevRepo
    if (-not (Test-Path -LiteralPath $devRepo)) { New-Item -ItemType Directory -Force -Path $devRepo | Out-Null }
    $link = Join-Path $devRepo 'VL.MediaPipe'
    if (Test-Path -LiteralPath $link) {
        $item = Get-Item -LiteralPath $link -Force
        if (-not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "$link exists but is not a junction; refusing to touch it."
        }
        $target = @($item.Target) | Select-Object -First 1
        if ($target -and ([string]$target).TrimEnd('\') -ne $script:MpRepoRoot) {
            throw "$link is a junction to '$target', expected '$($script:MpRepoRoot)'."
        }
        return $link
    }
    New-Item -ItemType Junction -Path $link -Target $script:MpRepoRoot | Out-Null
    return $link
}

function Get-MpVvvvLogDirs {
    $docs = [Environment]::GetFolderPath('MyDocuments')
    return @((Join-Path $docs 'vvvv\gamma-preview'), (Join-Path $docs 'vvvv\gamma')) | Select-Object -Unique
}

function Find-MpVvvvLog {
    param([Parameter(Mandatory)] [datetime] $Since)
    $files = foreach ($d in (Get-MpVvvvLogDirs)) {
        if (Test-Path -LiteralPath $d) { Get-ChildItem -LiteralPath $d -Filter 'vvvv*.log' -File -ErrorAction SilentlyContinue }
    }
    return ($files | Where-Object { $_.LastWriteTime -ge $Since.AddSeconds(-2) } | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}

# Reads a file another process still has open for writing.
function Read-MpSharedText([string] $Path) {
    try {
        $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
            ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        try { return (New-Object System.IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
    } catch { return $null }
}
