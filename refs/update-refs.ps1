<#
.SYNOPSIS
    Regenerate the reference assemblies used by CI.

.DESCRIPTION
    The mod compiles against the game's own assemblies, which obviously can't be shipped in a
    public repo or downloaded on a build server. Refasmer strips every method body and leaves
    only the public metadata the compiler needs, which is small enough to commit and contains
    none of the game's actual code.

    Run this after a game update, or when the code starts referencing a new assembly (the build
    will fail with "type or namespace not found" until you add it to the list below).

        .\refs\update-refs.ps1
#>
[CmdletBinding()]
param(
    [string] $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Combolands'
)

$ErrorActionPreference = 'Stop'

# Only what the mod actually references. Keep this minimal - every entry is weight in the repo,
# and unused assemblies drift out of date silently.
# Must match the <Reference> list in CombolandsTweaks/CombolandsTweaks.csproj.
$needed = @(
    'Assembly-CSharp'
    'Assembly-CSharp-firstpass'
    'Sirenix.Serialization'
    'Unity.InputSystem'
    'Unity.TextMeshPro'
    'UnityEngine'
    'UnityEngine.CoreModule'
    'UnityEngine.IMGUIModule'
    'UnityEngine.InputLegacyModule'
    'UnityEngine.TextRenderingModule'
    'UnityEngine.UI'
    'UnityEngine.UIModule'
)

$managed = Join-Path $GameDir 'Combolands_Data\Managed'
if (-not (Test-Path $managed)) {
    Write-Host "No Managed folder at: $managed" -ForegroundColor Red
    exit 1
}

# Refasmer is a dotnet global tool.
if (-not (Get-Command refasmer -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing Refasmer...' -ForegroundColor Cyan
    dotnet tool install -g JetBrains.Refasmer.CliTool | Out-Null
    $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
}

$out = Join-Path $PSScriptRoot 'Managed'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem $out -Filter *.dll -ErrorAction SilentlyContinue | Remove-Item -Force

$missing = @()
# Refasmer writes harmless warnings to stderr (unknown types in Unity's internal signatures).
# Windows PowerShell turns native-exe stderr into terminating errors, so relax that here and
# judge success by whether the output file actually appeared.
$prevEAP = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
foreach ($name in $needed) {
    $src = Join-Path $managed "$name.dll"
    if (-not (Test-Path $src)) { $missing += $name; continue }
    # --omit-non-api-members=false keeps every member signature; only bodies are dropped.
    refasmer -c --omit-non-api-members=false -O $out $src 2>$null | Out-Null
    if (-not (Test-Path (Join-Path $out "$name.dll"))) { $missing += $name }
}
$ErrorActionPreference = $prevEAP

# Refasmer chokes on a couple of Unity 6 modules; Cecil strips those instead. Anything still
# missing after that genuinely isn't in the game folder.
$stillMissing = @()
foreach ($name in $missing) {
    $src = Join-Path $managed "$name.dll"
    if (-not (Test-Path $src)) { $stillMissing += $name; continue }
    Write-Host "Refasmer rejected $name; stripping with Cecil instead." -ForegroundColor DarkYellow
    & (Join-Path $PSScriptRoot 'strip-assembly.ps1') -Source $src -OutDir $out
    if (-not (Test-Path (Join-Path $out "$name.dll"))) { $stillMissing += $name }
}

if ($stillMissing.Count) {
    Write-Host "Could not produce references for: $($stillMissing -join ', ')" -ForegroundColor Red
}

$before = (Get-ChildItem $managed -Filter *.dll | Where-Object { $needed -contains $_.BaseName } |
           Measure-Object Length -Sum).Sum / 1MB
$after  = (Get-ChildItem $out -Filter *.dll | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Reference assemblies: {0} files, {1:N1} MB (from {2:N1} MB)" -f `
    (Get-ChildItem $out -Filter *.dll).Count, $after, $before) -ForegroundColor Green
