#requires -Version 5.1

<#
.SYNOPSIS
    Compile l'intégralité de la solution STYX en intégration (machine de
    POC, runner CI), sans git.

.DESCRIPTION
    Le script :
    1. valide l'environnement ;
    2. restaure les dépendances, sauf avec -NoRestore ;
    3. nettoie la solution, sauf avec -NoClean ;
    4. compile la solution dans la configuration de l'environnement
       (DotNet.Configuration de devops\config\<Environment>.psd1).

    Les binaires sont générés dans les répertoires standards de chaque projet :

        bin\<Configuration>\<TargetFramework>\

    Le script n'effectue aucune publication et ne crée aucun package.

.PARAMETER NoRestore
    Désactive le restore explicite et impose --no-restore au build.

    Les dépendances doivent avoir été restaurées au préalable.

.PARAMETER NoClean
    Désactive le nettoyage de la solution.

.PARAMETER IgnoreFailedSources
    Ajoute --ignore-failed-sources à la commande dotnet restore.

    Cette option ne peut pas être utilisée avec -NoRestore.

.PARAMETER DisableNuGetAudit
    Ajoute -p:NuGetAudit=false à la commande dotnet restore.

    Cette option ne peut pas être utilisée avec -NoRestore.

.PARAMETER RepositoryRoot
    Racine des sources, obligatoire : git n'est pas disponible en intégration.
    Le runner fournit le dossier des sources récupérées par l'API GitLab.

.PARAMETER Environment
    Nom de l'environnement (devops\config\<Environment>.psd1). Par défaut : int.

.EXAMPLE
    .\build.ps1 -RepositoryRoot D:\ci\build

.EXAMPLE
    .\build.ps1 -RepositoryRoot D:\ci\build -NoClean

.EXAMPLE
    .\build.ps1 -RepositoryRoot D:\ci\build -IgnoreFailedSources -DisableNuGetAudit
#>

[CmdletBinding()]
param(
    [switch] $NoRestore,

    [switch] $NoClean,

    [switch] $IgnoreFailedSources,

    [switch] $DisableNuGetAudit,

    [Parameter(Mandatory)]
    [string] $RepositoryRoot,

    [string] $Environment = 'int'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\modules\StyxBuild.psm1')
)

try {
    if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
        throw "Module StyxBuild introuvable : $modulePath"
    }

    Import-Module `
        -Name $modulePath `
        -Force `
        -ErrorAction Stop

    Initialize-StyxOutput

    if ($NoRestore -and $IgnoreFailedSources) {
        throw "Le paramètre -IgnoreFailedSources ne peut pas être utilisé avec -NoRestore, car aucune commande de restauration ne sera exécutée."
    }

    if ($NoRestore -and $DisableNuGetAudit) {
        throw "Le paramètre -DisableNuGetAudit ne peut pas être utilisé avec -NoRestore, car aucune commande de restauration ne sera exécutée."
    }

    Assert-StyxCommand -Name 'dotnet'

    $config = Get-StyxConfiguration `
        -ScriptDirectory $PSScriptRoot `
        -RepositoryRoot $RepositoryRoot `
        -Environment $Environment

    # En intégration, la configuration de compilation est celle de
    # l'environnement (DotNet.Configuration, Release pour int).
    $Configuration = $config.DotNet.DefaultConfiguration

    # Paquets NuGet restaurés dans un dossier propre au build (vidé avec les
    # sources à chaque commit), et non dans le cache de la machine : chaque
    # build vérifie que les sources de nuget.config suffisent.
    $env:NUGET_PACKAGES       = Join-Path $config.RepositoryRoot '.nuget\packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $config.RepositoryRoot '.nuget\http-cache'

    if ([string]::IsNullOrWhiteSpace($Configuration)) {
        throw "DotNet.Configuration absent de la configuration de l'environnement '$Environment'."
    }

    Write-StyxStep 'STYX - Build complet de la solution'

    Write-StyxInfo "Dépôt Git        : $($config.RepositoryRoot)"
    Write-StyxInfo "Backend          : $($config.BackendRoot)"
    Write-StyxInfo "Solution         : $($config.SolutionPath)"
    Write-StyxInfo "Configuration    : $Configuration"
    Write-StyxInfo "Paquets NuGet    : $env:NUGET_PACKAGES"

    if ($NoRestore) {
        Write-StyxInfo 'Restauration     : ignorée (-NoRestore)'
    }
    else {
        Write-StyxInfo 'Restauration     : activée'
    }

    if ($NoClean) {
        Write-StyxInfo 'Nettoyage        : ignoré (-NoClean)'
    }
    else {
        Write-StyxInfo 'Nettoyage        : activé'
    }

    # ------------------------------------------------------------
    # Restore
    # ------------------------------------------------------------

    if (-not $NoRestore) {
        $restoreArguments = @(
            'restore'
            $config.SolutionPath
        )

        $restoreArguments += @($config.DotNet.RestoreArguments)

        if ($IgnoreFailedSources) {
            $restoreArguments += '--ignore-failed-sources'
        }

        if ($DisableNuGetAudit) {
            $restoreArguments += '-p:NuGetAudit=false'
        }

        Write-StyxStep 'Restauration des dépendances'

        Invoke-StyxNative `
            -FilePath $config.DotNet.Executable `
            -Arguments $restoreArguments `
            -Description 'La restauration de la solution'
    }

    # ------------------------------------------------------------
    # Clean
    # ------------------------------------------------------------

    if (-not $NoClean) {
        $cleanArguments = @(
            'clean'
            $config.SolutionPath
            '--configuration'
            $Configuration
        )

        Write-StyxStep "Nettoyage de la solution en $Configuration"

        Invoke-StyxNative `
            -FilePath $config.DotNet.Executable `
            -Arguments $cleanArguments `
            -Description 'Le nettoyage de la solution'
    }

    # ------------------------------------------------------------
    # Build
    # ------------------------------------------------------------

    $buildArguments = @(
        'build'
        $config.SolutionPath
        '--configuration'
        $Configuration
        '--no-restore'
    )

    $buildArguments += @($config.DotNet.BuildArguments)

    Write-StyxStep "Compilation de la solution en $Configuration"

    Invoke-StyxNative `
        -FilePath $config.DotNet.Executable `
        -Arguments $buildArguments `
        -Description 'La compilation de la solution'

    Write-StyxSuccess "Build $Configuration terminé avec succès."

    exit 0
}
catch {
    $message = $_.Exception.Message

    if ($null -ne (Get-Command -Name 'Write-StyxFailure' -ErrorAction SilentlyContinue)) {
        Write-StyxFailure $message
    }
    else {
        Write-Error $message
    }

    exit 1
}
