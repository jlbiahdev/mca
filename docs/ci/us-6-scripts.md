# US 6 — Scripts d'intégration à ajouter au dépôt styx-poc

À ajouter par une MR dans `src/scripts/devops/`, en **UTF-8 avec BOM** et CRLF. Le module `StyxBuild.psm1` ne change pas.

Les valeurs `XXXXX` sont masquées : les renseigner avec les vraies valeurs dans le dépôt styx-poc (jamais dans mca).

## src/scripts/devops/int/build.ps1

Build d'intégration : `-RepositoryRoot` obligatoire (pas de git), environnement `int` par défaut, configuration de compilation lue dans l'environnement (`DotNet.Configuration`, Release). Aucune option Debug.

```powershell
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

    if ([string]::IsNullOrWhiteSpace($Configuration)) {
        throw "DotNet.Configuration absent de la configuration de l'environnement '$Environment'."
    }

    Write-StyxStep 'STYX - Build complet de la solution'

    Write-StyxInfo "Dépôt Git        : $($config.RepositoryRoot)"
    Write-StyxInfo "Backend          : $($config.BackendRoot)"
    Write-StyxInfo "Solution         : $($config.SolutionPath)"
    Write-StyxInfo "Configuration    : $Configuration"

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
```

## src/scripts/devops/config/int.psd1

Configuration de la machine de POC. Pour le build, seule la section `DotNet` est utilisée ; les autres sont exigées par `StyxBuild.psm1`.

```powershell
# Configuration de l'environnement d'intégration (machine de POC, runner CI).
#
# Ce fichier ne contient aucun secret : la section Applications.SecretVariables
# liste uniquement le NOM des variables d'environnement qui doivent porter les
# secrets.
#
# Pour le build (runner CI), seule la section DotNet est utilisée ; les autres
# sections sont exigées par StyxBuild et serviront au package et au
# déploiement. Les valeurs XXXXX sont à renseigner.
@{
    Environment = 'int'

    Network = @{
        # Nom d'hôte de la machine de POC, utilisé dans les URL transmises aux
        # applications.
        HostName     = 'XXXXX'

        BindHost     = '0.0.0.0'

        ApiPort      = 5002
        AgentPort    = 5200
        TaskFlowPort = 5100
    }

    DotNet = @{
        Configuration = 'Release'
        Runtime       = 'win-x64'
        SelfContained = $true
    }

    Paths = @{
        WorkRoot      = '%TEMP%\styx_build'
        PackageOutput = '%TEMP%\styx_packages'
    }

    Share = @{
        Path       = '\\XXXXX\XXXXX\publish\styx'
        ExtraFiles = @()
    }

    Artifactory = @{
        BaseUrl        = 'https://XXXXX'
        Repository     = 'XXXXX'
        ApiKeyVariable = 'ARTIFACTORY_API_KEY'
    }

    Development = @{
        Environment = 'Development'
    }

    Applications = @{
        Environment = @{
            DOTNET_ENVIRONMENT     = 'Test'
            ASPNETCORE_ENVIRONMENT = 'Test'
            TaskFlow__BaseUrl      = 'http://{HostName}:{TaskFlowPort}'
            TaskFlow__CallbackUrl  = 'http://{HostName}:{TaskFlowPort}/taskflow/taskState'
        }

        SecretVariables = @(
            'ConnectionStrings__StyxConnection'
        )
    }
}
```
