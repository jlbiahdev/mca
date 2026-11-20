# stop_all.ps1 — arrête les composants de la session (pas le Runner)

Contenu du fichier, à copier dans `src\scripts\devops\dev\stop_all.ps1`.

À enregistrer en UTF-8 avec BOM (Windows PowerShell 5.1).

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Arrête la session STYX démarrée par run_all.ps1.

.DESCRIPTION
    Les composants enregistrés dans <RepositoryRoot>\.run\session.json sont
    arrêtés dans l'ordre inverse du démarrage :
    - demande de fermeture (arrêt propre) ;
    - attente de TimeoutSeconds ;
    - avec -Force uniquement : arrêt forcé de l'arbre de processus.

    Un processus n'est arrêté que si son PID et son heure de démarrage
    correspondent à ceux enregistrés dans la session.

    Le Runner n'est pas arrêté : il est lancé par l'Agent et s'arrête de
    lui-même à la fin de son job.

    Les anciennes sessions .run\debug et .run\release, si elles existent
    encore, sont traitées de la même façon.

    Le fichier de session n'est supprimé que si tous ses composants sont
    arrêtés ; sinon il est conservé pour une nouvelle tentative.

.PARAMETER TimeoutSeconds
    Délai accordé à chaque composant pour s'arrêter proprement.

.PARAMETER Force
    Force l'arrêt des composants encore actifs après le délai.

.PARAMETER RepositoryRoot
    Racine des sources. Par défaut, la racine du dépôt Git contenant ce script.
#>

[CmdletBinding()]
param(
    [ValidateRange(1, 300)]
    [int] $TimeoutSeconds = 10,

    [switch] $Force,

    [string] $RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulesDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\modules'))

try {
    Import-Module (Join-Path $modulesDirectory 'StyxBuild.psm1') -Force -ErrorAction Stop
    Import-Module (Join-Path $modulesDirectory 'Styx.Processes.psm1') -Force -ErrorAction Stop

    Initialize-StyxOutput

    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        $RepositoryRoot = Get-StyxRepositoryRoot -ScriptDirectory $PSScriptRoot
    }

    Write-StyxInfo "Dépôt : $RepositoryRoot"

    $errors = New-Object 'Collections.Generic.List[string]'
    $processedSessionCount = 0

    $runRoot = Get-StyxRunRoot -RepositoryRoot $RepositoryRoot

    $sessionFiles = @(
        Get-StyxSessionFile -RepositoryRoot $RepositoryRoot

        # Sessions des anciennes versions des scripts.
        Join-Path $runRoot 'debug\session.json'
        Join-Path $runRoot 'release\session.json'
    )

    foreach ($sessionFile in $sessionFiles) {
        if (-not (Test-Path -LiteralPath $sessionFile -PathType Leaf)) {
            continue
        }

        Write-StyxStep "Arrêt de la session $sessionFile"

        try {
            Stop-StyxSession -SessionFile $sessionFile -TimeoutSeconds $TimeoutSeconds -Force:$Force
            $processedSessionCount++
            Write-StyxInfo "[OK] Session arrêtée et supprimée." -ForegroundColor Green
        }
        catch {
            # Le fichier session.json est conservé pour une nouvelle tentative.
            $errors.Add("$sessionFile : $($_.Exception.Message)")
        }
    }

    if ($errors.Count -gt 0) {
        throw ("Certaines sessions n'ont pas pu être arrêtées :" + [Environment]::NewLine + ($errors -join [Environment]::NewLine))
    }

    if ($processedSessionCount -eq 0) {
        Write-StyxSuccess 'Aucune session STYX présente. Rien à arrêter.'
    }
    else {
        Write-StyxSuccess 'Session STYX arrêtée.'
    }

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
