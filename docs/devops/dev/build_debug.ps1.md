# build_debug.ps1 — build de la solution en Debug

Contenu du fichier, à copier dans `src\scripts\devops\dev\build_debug.ps1`.

À enregistrer en UTF-8 avec BOM (Windows PowerShell 5.1).

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Compile toute la solution Styx en Debug.
#>

[CmdletBinding()]
param(
    [string] $RepositoryRoot,

    [string] $Environment = 'dev'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$buildScript = Join-Path $PSScriptRoot "build.ps1"

if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    Write-Error "Script de build introuvable : $buildScript"
    exit 1
}

& $buildScript -Configuration "Debug" -RepositoryRoot $RepositoryRoot -Environment $Environment

$exitCode = $LASTEXITCODE

if ($null -eq $exitCode) {
    $exitCode = 1
}

exit $exitCode
```
