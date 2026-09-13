<#
.SYNOPSIS
    Compile gate for the VL.MediaPipe fork: lint every document, then compile each with vvvvc.exe.

.DESCRIPTION
    Phase 0  lint (no vvvv): the XML parses; Ids of elements this fork added are 22 base62 characters; every Id
             is unique; every Link and Fragment points at an existing Id; each Application has a
             ProcessDefinition with Create/Update fragments; relative PlatformDependency files exist; a document
             that uses types or nodes from VL.MediaPipe.dll carries its own PlatformDependency on it.
    Phase 1  vvvvc.exe <doc> --package-repositories D:\claude\_vl-dev --nuget-path D:\claude\_vl-dev\nugets
             --output-directory results\vvvvc\<doc>, holding the team lock D:\claude\_vl-dev\vvvv.lock. Documents
             are opened through the junction D:\claude\_vl-dev\VL.MediaPipe so VL.MediaPipe.dll loads from one
             path only. The exit code is the verdict; stdout/stderr go to results\gate\.

    Exit: 0 every document passed, 1 a gate failed, 2 setup problem.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\vvvvc-gate.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\vvvvc-gate.ps1 -Documents VL.MediaPipe.vl
#>
[CmdletBinding()]
param(
    [string[]] $Documents = @(),
    [int] $TimeoutSeconds = 1500,
    [ValidateSet('Trace', 'Debug', 'Information', 'Warning', 'Error')] [string] $Verbosity = 'Warning',
    [switch] $SkipLint,
    [switch] $KeepExport
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\MpCommon.ps1')

$root = Get-MpRepoRoot
# powershell -File passes "a.vl,b.vl" as one string
$Documents = @($Documents | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Documents.Count -eq 0) {
    $Documents = @('VL.MediaPipe.vl', 'help\HowTo Track hands on a texture.vl', 'test\TextureInputHarness.vl')
}
$relative = @($Documents | ForEach-Object {
    $full = if ([System.IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $root $_ }
    $full = [System.IO.Path]::GetFullPath($full)
    if (-not $full.StartsWith($root + '\', [System.StringComparison]::OrdinalIgnoreCase)) { throw "$full is not inside $root" }
    $full.Substring($root.Length + 1)
})
$vvvvc = Join-Path (Get-MpVvvvDir) 'vvvvc.exe'
$gateDir = Join-Path $root 'results\gate'
New-Item -ItemType Directory -Force -Path $gateDir | Out-Null
foreach ($p in @($vvvvc) + @($relative | ForEach-Object { Join-Path $root $_ })) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Host "SETUP: not found: $p"; exit 2 }
}
if (-not (Test-Path -LiteralPath (Join-Path $root 'lib\net8.0\VL.MediaPipe.dll'))) {
    Write-Host 'SETUP: lib\net8.0\VL.MediaPipe.dll is missing; run dotnet build src\VL.MediaPipe.csproj -c Release'
    exit 2
}
$failures = New-Object System.Collections.Generic.List[string]

function Test-MpVlDocument([string] $Path) {
    $problems = New-Object System.Collections.Generic.List[string]
    $xml = New-Object System.Xml.XmlDocument
    try { $xml.LoadXml([System.IO.File]::ReadAllText($Path)) } catch {
        $problems.Add("XML does not parse ('--' inside a comment is the usual cause): $($_.Exception.Message)")
        return , $problems
    }
    $ids = @{}
    foreach ($n in $xml.SelectNodes('//*[@Id]')) {
        $id = $n.GetAttribute('Id')
        if ($id -cnotmatch '^[A-Za-z0-9]{22}$') { $problems.Add("Id '$id' on <$($n.Name)> is not 22 base62 characters") }
        if ($ids.ContainsKey($id)) { $problems.Add("Id '$id' is used twice (<$($ids[$id])> and <$($n.Name)>)") } else { $ids[$id] = $n.Name }
    }
    foreach ($n in $xml.SelectNodes('//*[@SlotId]')) {
        if (-not $ids.ContainsKey($n.GetAttribute('SlotId'))) { $problems.Add("SlotId '$($n.GetAttribute('SlotId'))' points nowhere") }
    }
    foreach ($link in $xml.SelectNodes('//Link')) {
        foreach ($ref in ($link.GetAttribute('Ids') -split ',')) {
            if (-not $ids.ContainsKey($ref)) { $problems.Add("Link $($link.GetAttribute('Id')) references unknown Id '$ref'") }
        }
    }
    foreach ($fragment in $xml.SelectNodes('//Fragment')) {
        if (-not $ids.ContainsKey($fragment.GetAttribute('Patch'))) { $problems.Add("Fragment $($fragment.GetAttribute('Id')) references unknown Patch '$($fragment.GetAttribute('Patch'))'") }
    }
    foreach ($app in $xml.SelectNodes("//Node[@Name='Application']")) {
        if ($app.SelectNodes('Patch/ProcessDefinition/Fragment').Count -lt 2) {
            $problems.Add('Application has no ProcessDefinition with Create and Update fragments: nothing on its canvas runs')
        }
    }
    $isPackageDoc = [System.IO.Path]::GetFileName($Path) -ieq 'VL.MediaPipe.vl'
    $usesDll = $xml.SelectNodes("//*[@LastDependency='VL.MediaPipe.dll']").Count -gt 0
    $hasDll = @($xml.SelectNodes('/Document/PlatformDependency') | Where-Object { $_.GetAttribute('Location') -match 'VL\.MediaPipe\.dll$' }).Count -gt 0
    if ($usesDll -and -not $hasDll -and -not $isPackageDoc) {
        $problems.Add('uses VL.MediaPipe.dll types/nodes but has no PlatformDependency on it')
    }
    $dir = Split-Path -Parent $Path
    foreach ($dep in $xml.SelectNodes('/Document/PlatformDependency')) {
        $location = $dep.GetAttribute('Location')
        if ($location -match '^\.{1,2}[\\/]' -and -not (Test-Path -LiteralPath (Join-Path $dir $location))) {
            $problems.Add("PlatformDependency $location does not exist")
        }
    }
    return , $problems
}

if (-not $SkipLint) {
    foreach ($rel in $relative) {
        Write-Host "[0] lint $rel"
        $problems = Test-MpVlDocument (Join-Path $root $rel)
        if ($problems.Count -eq 0) { Write-Host '      ok' }
        foreach ($problem in $problems) { Write-Host "      $problem"; $failures.Add("lint ${rel}: $problem") }
    }
    if ($failures.Count -gt 0) { Write-Host 'FAIL: lint problems; not compiling.'; $failures | ForEach-Object { Write-Host "  - $_" }; exit 1 }
}

$junction = Initialize-MpDevJunction
$devRepo = Get-MpDevRepo
$nugets = Get-MpNugetPath
New-Item -ItemType Directory -Force -Path $nugets | Out-Null
Write-Host "junction : $junction -> $root"
Write-Host "nugets   : $nugets"
$lock = $null
try {
    $lock = Enter-MpVvvvLock -Label "VL.MediaPipe scripts\vvvvc-gate.ps1"
    Write-Host "lock     : acquired $lock"
    foreach ($rel in $relative) {
        $base = [System.IO.Path]::GetFileNameWithoutExtension($rel) -replace '[^A-Za-z0-9._-]', '_'
        $outDir = Join-Path $root "results\vvvvc\$base"
        $stdout = Join-Path $gateDir "vvvvc-$base.out.txt"
        $stderr = Join-Path $gateDir "vvvvc-$base.err.txt"
        # The package document itself opens by its real path: through the junction vvvvc takes it for the
        # VL.MediaPipe package and reports "Entry point for document VL.MediaPipe.vl not found".
        $doc = if ($rel -ieq 'VL.MediaPipe.vl') { Join-Path $root $rel } else { Join-Path $junction $rel }
        Write-Host "[1] vvvvc $doc"
        $t0 = Get-Date
        $r = Invoke-MpNative -FilePath $vvvvc -StdOutPath $stdout -StdErrPath $stderr -TimeoutSeconds $TimeoutSeconds -Arguments @(
            $doc, '--verbosity', $Verbosity, '--package-repositories', $devRepo, '--nuget-path', $nugets, '--output-directory', $outDir)
        $secs = [int]((Get-Date) - $t0).TotalSeconds
        @(Get-Content -LiteralPath $stdout, $stderr -ErrorAction SilentlyContinue) | Select-Object -Last 25 | ForEach-Object { Write-Host "      $_" }
        # vvvvc exports a document even when a package it depends on failed to compile, and still exits 0.
        $depErrors = @(Get-Content -LiteralPath $stdout, $stderr -ErrorAction SilentlyContinue | Where-Object { $_ -match 'has errors\. Package compilation will continue' })
        if ($r.TimedOut) { $failures.Add("vvvvc $rel timed out after $TimeoutSeconds s"); Write-Host '      FAIL: timed out' }
        elseif ($r.ExitCode -ne 0) { $failures.Add("vvvvc $rel exited $($r.ExitCode)"); Write-Host "      FAIL: exit $($r.ExitCode) after $secs s" }
        elseif ($depErrors.Count -gt 0) { $failures.Add("vvvvc $rel exited 0 but a dependency did not compile: $($depErrors[0].Trim())"); Write-Host "      FAIL: exit 0 after $secs s, but a dependency has errors" }
        else { Write-Host "      ok: exit 0 after $secs s" }
        if (-not $KeepExport -and $r.ExitCode -eq 0 -and (Test-Path -LiteralPath $outDir)) {
            Remove-Item -LiteralPath $outDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
} finally {
    if ($lock) { Exit-MpVvvvLock $lock; Write-Host 'lock     : released' }
}

if ($failures.Count -gt 0) { Write-Host 'FAIL:'; $failures | ForEach-Object { Write-Host "  - $_" }; exit 1 }
Write-Host 'PASS: every document compiled.'
exit 0
