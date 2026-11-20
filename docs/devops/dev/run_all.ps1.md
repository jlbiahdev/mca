# run_all.ps1 — construit et lance les composants en Release (validation locale)

Contenu du fichier, à copier dans `src\scripts\devops\dev\run_all.ps1`.

À enregistrer en UTF-8 avec BOM (Windows PowerShell 5.1).

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Construit, prépare et démarre les composants STYX en Release.

.DESCRIPTION
    Le script :
    - appelle build_release.ps1 ;
    - lit la configuration de l'environnement (devops\config\<env>.psd1) ;
    - vérifie les secrets obligatoires et la disponibilité des ports ;
    - copie les sorties Release dans <RepositoryRoot>\.run\release ;
    - démarre, dans l'ordre : TaskFlow Runner, HPC Lite Agent,
      HPC Lite Scheduler, STYX Web API ;
    - ne démarre pas directement HPC Lite Runner ;
    - enregistre chaque composant démarré dans session.json ;
    - arrête les composants déjà lancés si le démarrage échoue.

    La logique commune à debug_all.ps1 et run_all.ps1 est dans
    modules\Styx.Processes.psm1.

.PARAMETER RepositoryRoot
    Racine des sources. Par défaut, la racine du dépôt Git contenant ce script.

.PARAMETER Environment
    Nom de l'environnement : lit devops\config\<Environment>.psd1. Par défaut : dev.

.PARAMETER ConfigFile
    Chemin explicite d'un fichier de configuration (prioritaire sur -Environment).

.PARAMETER StartupTimeoutSeconds
    Délai maximal d'attente de chaque composant (port en écoute).
#>

[CmdletBinding()]
param(
    [string] $RepositoryRoot,

    [string] $Environment = 'dev',

    [string] $ConfigFile,

    [ValidateRange(1, 300)]
    [int] $StartupTimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulesDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\modules'))

try {
    Import-Module (Join-Path $modulesDirectory 'StyxBuild.psm1') -Force -ErrorAction Stop
    Import-Module (Join-Path $modulesDirectory 'Styx.Processes.psm1') -Force -ErrorAction Stop

    Initialize-StyxOutput

    Start-StyxSession `
        -Configuration 'Release' `
        -BuildScript (Join-Path $PSScriptRoot 'build_release.ps1') `
        -ScriptDirectory $PSScriptRoot `
        -RepositoryRoot $RepositoryRoot `
        -Environment $Environment `
        -ConfigFile $ConfigFile `
        -StartupTimeoutSeconds $StartupTimeoutSeconds

    exit 0
}
catch {
    if ($null -ne (Get-Command -Name 'Write-StyxFailure' -ErrorAction SilentlyContinue)) {
        Write-StyxFailure $_.Exception.Message
    }
    else {
        Write-Error $_.Exception.Message -ErrorAction Continue
    }

    exit 1
}
```
