<#
.SYNOPSIS
    Strip method bodies from an assembly, producing a compile-only reference copy.

.DESCRIPTION
    Refasmer handles almost everything, but it fails on a couple of Unity 6 modules
    (UnityEngine.CoreModule, UnityEngine.PhysicsModule) with:

        Unknown type in signature: {TypeDef[..]: ::<m_ProbeOcclusionLightIndex>e__FixedBuffer}

    This does the same job with Mono.Cecil: every method body is replaced with `throw null`,
    so the metadata the compiler needs survives and none of the real code does.

    update-refs.ps1 calls this automatically for whatever Refasmer rejects.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Source,
    [Parameter(Mandatory = $true)][string] $OutDir,
    [string] $CecilDll
)

$ErrorActionPreference = 'Stop'

if (-not $CecilDll -or -not (Test-Path $CecilDll)) {
    $CecilDll = Join-Path $PSScriptRoot 'tools\Mono.Cecil.dll'
}
if (-not (Test-Path $CecilDll)) {
    Write-Host "Mono.Cecil.dll not found. Fetching it..." -ForegroundColor Cyan
    $toolDir = Join-Path $PSScriptRoot 'tools'
    New-Item -ItemType Directory -Force -Path $toolDir | Out-Null
    $nupkg = Join-Path $env:TEMP ('cecil_' + [Guid]::NewGuid().ToString('N') + '.zip')
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri 'https://www.nuget.org/api/v2/package/Mono.Cecil/0.11.5' -OutFile $nupkg -UseBasicParsing
    $extract = Join-Path $env:TEMP ('cecil_' + [Guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $nupkg -DestinationPath $extract -Force
    Copy-Item (Join-Path $extract 'lib\netstandard2.0\Mono.Cecil.dll') $toolDir -Force
    Remove-Item $nupkg, $extract -Recurse -Force -ErrorAction SilentlyContinue
    $CecilDll = Join-Path $toolDir 'Mono.Cecil.dll'
}

Add-Type -Path $CecilDll

$name = [System.IO.Path]::GetFileName($Source)
$dest = Join-Path $OutDir $name

# Resolve sibling assemblies from the same folder, or Cecil can't read the type graph.
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory([System.IO.Path]::GetDirectoryName($Source))
$params = New-Object Mono.Cecil.ReaderParameters
$params.AssemblyResolver = $resolver

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Source, $params)
try {
    foreach ($module in $asm.Modules) {
        foreach ($type in $module.GetTypes()) {
            foreach ($method in $type.Methods) {
                if (-not $method.HasBody) { continue }
                $body = $method.Body
                $body.Instructions.Clear()
                $body.Variables.Clear()
                $body.ExceptionHandlers.Clear()
                $il = $body.GetILProcessor()
                # throw null - valid for every signature, and never actually executed.
                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldnull))
                $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Throw))
            }
        }
    }
    $asm.Write($dest)
} finally {
    $asm.Dispose()
}

$before = (Get-Item $Source).Length / 1MB
$after  = (Get-Item $dest).Length / 1MB
Write-Host ("  stripped {0}: {1:N1} MB -> {2:N1} MB" -f $name, $before, $after) -ForegroundColor DarkGray
