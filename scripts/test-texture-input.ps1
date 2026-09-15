<#
.SYNOPSIS
    Texture-input checks that need no vvvv: the page's Node test suite, the C# FramePacket / POCO checks, and the
    cross-language packet check (C# writes packets, the page's parser reads them).

.DESCRIPTION
    1. node --test mediapipe-touchdesigner/test/**/*.test.mjs          (portable Node, D:\claude\_tools\node)
    2. dotnet build + run test\csharp\FramePacketCheck                  (against lib\net8.0\VL.MediaPipe.dll)
    3. node test\framepacket\check_packets.mjs results\framepacket-check
    Optional -PageSmoke: headless Edge run of the built page in texture mode (scripts\page-smoke.mjs) on the
    hand fixture, which needs refvenv Python + Pillow to decode the JPEG once.

    Exit: 0 all passed, 1 a check failed.
#>
[CmdletBinding()]
param([switch] $PageSmoke)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\MpCommon.ps1')
$root = Get-MpRepoRoot
$node = Get-MpNode
$failed = New-Object System.Collections.Generic.List[string]

function Invoke-Step([string] $Name, [scriptblock] $Block) {
    Write-Host "== $Name"
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Block; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $prev }
    if ($code -ne 0) { $failed.Add("$Name (exit $code)"); Write-Host "   -> FAIL (exit $code)" } else { Write-Host '   -> ok' }
}

Push-Location (Join-Path $root 'mediapipe-touchdesigner')
# Explicit files: node --test expands glob patterns only from Node 21 on.
$pageTests = @(Get-ChildItem -LiteralPath 'test' -Recurse -Filter '*.test.mjs' | ForEach-Object { $_.FullName })
try { Invoke-Step 'page unit tests (node --test)' { & $node --test @pageTests 2>&1 | ForEach-Object { "   $_" } | Write-Host } } finally { Pop-Location }

$checkDir = Join-Path $root 'results\framepacket-check'
Invoke-Step 'build FramePacketCheck' { & dotnet build (Join-Path $root 'test\csharp\FramePacketCheck\FramePacketCheck.csproj') -c Release -nologo -v q 2>&1 | Select-Object -Last 3 | ForEach-Object { "   $_" } | Write-Host }
# The check binaries live outside the repo (see FramePacketCheck.csproj); only the packets go to results\.
$checkExe = Join-Path ([System.IO.Path]::GetTempPath()) 'VL.MediaPipe.FramePacketCheck\bin\FramePacketCheck.exe'
Invoke-Step 'C# FramePacket / POCO checks' { & $checkExe $checkDir 2>&1 | ForEach-Object { "   $_" } | Write-Host }
Invoke-Step 'page parser reads C# packets' { & $node (Join-Path $root 'test\framepacket\check_packets.mjs') $checkDir 2>&1 | ForEach-Object { "   $_" } | Write-Host }

if ($PageSmoke) {
    $smokeDir = Join-Path $root 'results\page-smoke'
    New-Item -ItemType Directory -Force -Path $smokeDir | Out-Null
    $fixture = 'D:\claude\depth-handtracking\tools\hand-tracking\fixtures\hand_rgb_sample.jpg'
    $python = Join-Path (Get-MpDevRoot) 'tools\refvenv-cuda\Scripts\python.exe'
    & $python -c "from PIL import Image; im = Image.open(r'$fixture').convert('RGBA'); open(r'$smokeDir\hand_rgb_sample.rgba','wb').write(im.tobytes()); print(im.size[0], im.size[1])" | Tee-Object -Variable size | Out-Null
    $w, $h = ($size -split ' ')
    Invoke-Step 'page smoke (headless Edge, texture mode, ack)' { & $node (Join-Path $root 'scripts\page-smoke.mjs') --web (Join-Path $root 'web') --image "$smokeDir\hand_rgb_sample.rgba" --width $w --height $h --frames 300 --ack 1 --out "$smokeDir\texture-ack.json" 2>&1 | Select-Object -Last 40 | ForEach-Object { "   $_" } | Write-Host }
}

if ($failed.Count -gt 0) { Write-Host 'FAIL:'; $failed | ForEach-Object { Write-Host "  - $_" }; exit 1 }
Write-Host 'PASS'
exit 0
