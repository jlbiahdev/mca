# run_all.ps1 — compile en Release, copie dans .run et démarre les composants (-Development : validation DI)

Contenu du fichier, à copier dans `src\scripts\devops\dev\run_all.ps1`.

À enregistrer en UTF-8 avec BOM (Windows PowerShell 5.1).

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Construit en Release, copie et démarre les composants STYX.

.DESCRIPTION
    Le script :
    - vérifie qu'aucune session n'est active, qu'aucun Runner ne travaille
      depuis .run et que les ports sont libres ;
    - vérifie les secrets obligatoires (sauf avec -Development) ;
    - compile la solution en Release (build_release.ps1) ;
    - copie les sorties dans le dossier unique <RepositoryRoot>\.run ;
    - démarre, dans l'ordre : TaskFlow Runner, HPC Lite Agent,
      HPC Lite Scheduler, STYX Web API ;
    - ne démarre pas HPC Lite Runner : l'Agent le lance à partir du chemin
      enregistré en base (exe_path = <RepositoryRoot>\.run\hpclite\runner\...) ;
    - enregistre chaque composant démarré dans .run\session.json ;
    - arrête les composants déjà lancés si le démarrage échoue.

    La logique est dans modules\Styx.Processes.psm1.

.PARAMETER Development
    Lance les composants en environnement Development : .NET y valide
    l'injection de dépendances (un service scoped résolu depuis la racine,
    un service impossible à construire...) et charge les user-secrets.
    Le Runner, lancé par l'Agent, hérite de cet environnement.

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
    [switch] $Development,

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
        -BuildScript (Join-Path $PSScriptRoot 'build_release.ps1') `
        -ScriptDirectory $PSScriptRoot `
        -RepositoryRoot $RepositoryRoot `
        -Environment $Environment `
        -ConfigFile $ConfigFile `
        -Development:$Development `
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
