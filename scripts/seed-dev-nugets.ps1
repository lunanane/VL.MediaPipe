<#
.SYNOPSIS
    Restores the NuGet packages VL.MediaPipe.vl depends on (and that vvvv 7.4 does not ship in packs\) into the
    dev nuget folder D:\claude\_vl-dev\nugets, so vvvv / vvvvc runs with --nuget-path find them there.

.DESCRIPTION
    Uses a throw-away SDK project whose PackageReferences mirror VL.MediaPipe.vl's NugetDependencies:
    VL.CEF 0.7.2 (+ VL.CEF.Stride 0.7.2 for the help patches; the version vvvv 7.2 and 7.5 ship) and VL.IO.WebServer 1.0.1. VL.IO.WebSocket 1.1.0
    ships with vvvv 7.4 (packs\) and is not restored.

    Package sources (a NuGet.config next to the project, <clear/> first): the local global-packages folder used
    as a read-only feed (offline; nothing is written there), then nuget.org. DNS on RENDERPC01 is flaky, so
    restore is retried up to 3 times.

    dotnet restore lays packages out as <id>\<version>\. vvvvc 7.4 did not find VL.CEF there ("Missing package",
    and no lookup of it in the Debug log), so every restored package also gets a junction <Id>.<Version> ->
    <id>\<version>, the layout `nuget install` produces. Junctions, so the CEF runtime is not copied twice.

    Exit: 0 ok, 4 restore failed.
#>
[CmdletBinding()]
param(
    [string] $NugetPath = '',
    [string] $WorkDir = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\MpCommon.ps1')
if (-not $NugetPath) { $NugetPath = Get-MpNugetPath }
if (-not $WorkDir) { $WorkDir = Join-Path (Get-MpRepoRoot) 'results\seed-nugets' }
New-Item -ItemType Directory -Force -Path $NugetPath, $WorkDir | Out-Null

$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="VL.CEF" Version="0.7.2" />
    <PackageReference Include="VL.CEF.Stride" Version="0.7.2" />
    <PackageReference Include="VL.IO.WebServer" Version="1.0.1" />
  </ItemGroup>
</Project>
'@
[System.IO.File]::WriteAllText((Join-Path $WorkDir 'seed.csproj'), $project)

$localFeed = Join-Path $env:USERPROFILE '.nuget\packages'
$sources = ''
if (Test-Path -LiteralPath $localFeed) { $sources += "    <add key=`"local-cache`" value=`"$localFeed`" />`r`n" }
$sources += "    <add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`" protocolVersion=`"3`" />`r`n"
$config = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`r`n<configuration>`r`n  <packageSources>`r`n    <clear />`r`n$sources  </packageSources>`r`n</configuration>`r`n"
$configPath = Join-Path $WorkDir 'NuGet.config'
[System.IO.File]::WriteAllText($configPath, $config)

$log = Join-Path $WorkDir 'restore.log'
$code = 1
for ($attempt = 1; $attempt -le 3; $attempt++) {
    Write-Host "restore attempt $attempt -> $NugetPath"
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet restore (Join-Path $WorkDir 'seed.csproj') --packages $NugetPath --configfile $configPath --nologo *> $log
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($code -eq 0) { break }
    Get-Content $log -Tail 10 | ForEach-Object { Write-Host "  $_" }
    Start-Sleep -Seconds 15
}
if ($code -ne 0) { Write-Host "FAIL: restore exited $code (see $log)"; exit 4 }

# <Id>.<Version> junctions next to the <id>\<version> folders.
foreach ($idDir in (Get-ChildItem -LiteralPath $NugetPath -Directory | Where-Object { -not ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) })) {
    foreach ($versionDir in (Get-ChildItem -LiteralPath $idDir.FullName -Directory)) {
        $nuspec = Get-ChildItem -LiteralPath $versionDir.FullName -Filter '*.nuspec' -File | Select-Object -First 1
        if (-not $nuspec) { continue }
        $metadata = ([xml](Get-Content -LiteralPath $nuspec.FullName -Raw)).package.metadata
        $link = Join-Path $NugetPath ("{0}.{1}" -f $metadata.id, $versionDir.Name)
        if (Test-Path -LiteralPath $link) {
            $item = Get-Item -LiteralPath $link -Force
            if (-not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { Write-Host "  keep existing folder $link"; continue }
            continue
        }
        New-Item -ItemType Junction -Path $link -Target $versionDir.FullName | Out-Null
        Write-Host "  junction $link -> $($versionDir.FullName)"
    }
}
Get-ChildItem -LiteralPath $NugetPath -Directory | Where-Object { $_.Name -match '^(vl\.cef|vl\.io\.webserver|watson)' -or $_.Name -match '^(VL\.CEF|VL\.IO\.WebServer|Watson)' } |
    ForEach-Object { Write-Host ("  {0}{1}" -f $_.Name, $(if ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { ' (junction)' } else { '' })) }
Write-Host 'ok'
exit 0
