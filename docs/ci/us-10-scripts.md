# US 10 — Scripts du dépôt pour la publication

À ajouter au dépôt par une MR, dans `src/scripts/devops/int/`, en **UTF-8 avec BOM**. Renseigner aussi `Artifactory.BaseUrl` et `Artifactory.Repository` dans `src/scripts/devops/config/int.psd1` (mêmes valeurs que `dev.psd1`).

## int/package.ps1

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Compile les applications STYX et génère le package NuGet Styx.Publish en
    intégration (machine du runner), sans git.

.DESCRIPTION
    Version d'intégration de dev\generate_package.ps1, lancée par le runner
    après un merge dans main :
    1. restaure la solution avec les sources NuGet du dépôt ;
    2. compile chaque application en Release dans un dossier de travail ;
    3. génère Styx.Publish.<PackageVersion>.nupkg dans OutputDirectory.

    La version est fournie par le runner (numéro du pipeline externe GitLab).
    Le script ne pousse rien : la publication se fait avec
    push-artifactory.ps1.

.PARAMETER RepositoryRoot
    Racine des sources, obligatoire : git n'est pas disponible en intégration.

.PARAMETER PackageVersion
    Version NuGet du package (1.2.3, 1.2.3.4, suffixe -xxx accepté).

.PARAMETER OutputDirectory
    Dossier où le package est écrit. Le dossier de travail y est créé
    (_work) et supprimé en cas de succès.

.EXAMPLE
    .\package.ps1 -RepositoryRoot D:\ci\build -PackageVersion 1.0.913231 -OutputDirectory D:\ci\packages\1.0.913231

.NOTES
    Code de sortie : 0 succès, 1 erreur.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RepositoryRoot,

    [Parameter(Mandatory)]
    [string] $PackageVersion,

    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ============================================================
# Configuration
# ============================================================

# En intégration, les applications sont toujours compilées en Release.
$Configuration = 'Release'

$PackageId = 'Styx.Publish'

$PackageOutputDirectory = $OutputDirectory
$WorkDirectory          = Join-Path $OutputDirectory '_work'
$PayloadDirectory       = Join-Path $WorkDirectory 'payload'
$PackProject            = Join-Path $WorkDirectory 'Styx.Publish.Pack.csproj'

# ============================================================
# Fonctions
# ============================================================

function Assert-Command {
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "La commande '$Name' est introuvable dans le PATH."
    }
}

function Assert-File {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label introuvable : $Path"
    }
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)]
        [string] $Command,

        [Parameter()]
        [string[]] $Arguments = @(),

        [Parameter()]
        [string] $ErrorMessage = "La commande '$Command' a échoué."
    )

    Write-Host
    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray

    & $Command @Arguments

    if ($LASTEXITCODE -ne 0) {
        throw "$ErrorMessage Code de sortie : $LASTEXITCODE"
    }
}

function Initialize-Directory {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Build-Project {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Project,

        [Parameter(Mandatory)]
        [string] $OutputDirectory
    )

    Assert-File -Path $Project -Label "Projet $Name"

    New-Item `
        -ItemType Directory `
        -Path $OutputDirectory `
        -Force | Out-Null

    Write-Host
    Write-Host '------------------------------------------------------------'
    Write-Host "Build  : $Name"
    Write-Host "Projet : $Project"
    Write-Host "Sortie : $OutputDirectory"
    Write-Host '------------------------------------------------------------'

    Invoke-NativeCommand `
        -Command 'dotnet' `
        -Arguments @(
            'build'
            $Project
            '--configuration', $Configuration
            '--no-restore'
            '--output', $OutputDirectory
            '-p:NuGetAudit=false'
        ) `
        -ErrorMessage "Échec du build de $Name."
}

function New-PackProject {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    # PackageVersion est volontairement distinct de Version.
    # La génération des informations d'assembly est désactivée afin
    # qu'une version NuGet telle que 2026.1117.170057 ne soit pas
    # utilisée comme version d'assembly .NET.
    $projectContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>

    <PackageId>$PackageId</PackageId>
    <PackageVersion>$PackageVersion</PackageVersion>

    <Authors>STYX</Authors>
    <Company>STYX</Company>
    <Description>Binaires issus du build des applications backend STYX.</Description>

    <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
    <IsPackable>true</IsPackable>

    <IncludeBuildOutput>false</IncludeBuildOutput>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
    <EnableDefaultItems>false</EnableDefaultItems>

    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>

    <NoWarn>NU5100</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <None
      Include="payload\**\*"
      Pack="true"
      PackagePath="tools\%(RecursiveDir)" />
  </ItemGroup>
</Project>
"@

    Set-Content `
        -LiteralPath $Path `
        -Value $projectContent `
        -Encoding UTF8

    Assert-File -Path $Path -Label 'Projet temporaire de packaging'
}

# ============================================================
# Exécution
# ============================================================

try {
    Write-Host
    Write-Host '============================================================'
    Write-Host 'Build et génération du package NuGet STYX'
    Write-Host '============================================================'

    # --------------------------------------------------------
    # Validation des outils
    # --------------------------------------------------------

    Assert-Command -Name 'dotnet'

    # --------------------------------------------------------
    # Racine des sources (fournie par le runner, sans git)
    # --------------------------------------------------------

    if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
        throw "Racine des sources introuvable : $RepositoryRoot"
    }

    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')

    # --------------------------------------------------------
    # Chemins des projets
    # --------------------------------------------------------

    $BackendDirectory = Join-Path $RepositoryRoot 'src\backend'
    $SolutionFile     = Join-Path $BackendDirectory 'styxapipoc.sln'

    # Le projet de packaging est créé hors du dépôt : on lui donne
    # explicitement les sources NuGet du dépôt.
    $NuGetConfig      = Join-Path $BackendDirectory 'nuget.config'

    $ApiProject = Join-Path `
        $BackendDirectory `
        'Styx.WebApi\Styx.Front.WebApi.csproj'

    $HpcLiteSchedulerProject = Join-Path `
        $BackendDirectory `
        'Styx.Application.HpcLite\scheduler\Styx.Application.HpcLite.Scheduler.csproj'

    $HpcLiteAgentProject = Join-Path `
        $BackendDirectory `
        'Styx.Application.HpcLite\agent\Styx.Application.HpcLite.Agent.csproj'

    $HpcLiteRunnerProject = Join-Path `
        $BackendDirectory `
        'Styx.Application.HpcLite\runner\Styx.Application.HpcLite.Runner.csproj'

    $TaskflowProject = Join-Path `
        $BackendDirectory `
        'Styx.Application.TaskFlow\runner\Styx.Application.TaskFlow.Runner.csproj'

    # --------------------------------------------------------
    # Génération de la version
    # --------------------------------------------------------

    if ($PackageVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?$') {
        throw "La version NuGet '$PackageVersion' n'est pas valide."
    }

    # --------------------------------------------------------
    # Affichage de la configuration
    # --------------------------------------------------------

    Write-Host
    Write-Host "Repository           : $RepositoryRoot"
    Write-Host "Backend              : $BackendDirectory"
    Write-Host "Solution             : $SolutionFile"
    Write-Host "Configuration        : $Configuration"
    Write-Host "Package ID           : $PackageId"
    Write-Host "Package version      : $PackageVersion"
    Write-Host "Package destination  : $PackageOutputDirectory"
    Write-Host
    Write-Host "API                  : $ApiProject"
    Write-Host "HPC Lite Scheduler   : $HpcLiteSchedulerProject"
    Write-Host "HPC Lite Agent       : $HpcLiteAgentProject"
    Write-Host "HPC Lite Runner      : $HpcLiteRunnerProject"
    Write-Host "Taskflow             : $TaskflowProject"

    # --------------------------------------------------------
    # Validation des fichiers
    # --------------------------------------------------------

    Assert-File -Path $SolutionFile -Label 'Solution'
    Assert-File -Path $NuGetConfig -Label 'Configuration NuGet'
    Assert-File -Path $ApiProject -Label 'Projet API'

    Assert-File `
        -Path $HpcLiteSchedulerProject `
        -Label 'Projet HPC Lite Scheduler'

    Assert-File `
        -Path $HpcLiteAgentProject `
        -Label 'Projet HPC Lite Agent'

    Assert-File `
        -Path $HpcLiteRunnerProject `
        -Label 'Projet HPC Lite Runner'

    Assert-File `
        -Path $TaskflowProject `
        -Label 'Projet Taskflow'

    # --------------------------------------------------------
    # Initialisation des répertoires
    # --------------------------------------------------------

    Write-Host
    Write-Host '============================================================'
    Write-Host 'Initialisation des répertoires'
    Write-Host '============================================================'

    Initialize-Directory -Path $WorkDirectory

    New-Item `
        -ItemType Directory `
        -Path $PackageOutputDirectory `
        -Force | Out-Null

    $ApiOutput = Join-Path $PayloadDirectory 'api'

    $HpcLiteSchedulerOutput = Join-Path `
        $PayloadDirectory `
        'hpclite\scheduler'

    $HpcLiteAgentOutput = Join-Path `
        $PayloadDirectory `
        'hpclite\agent'

    $HpcLiteRunnerOutput = Join-Path `
        $PayloadDirectory `
        'hpclite\runner'

    $TaskflowOutput = Join-Path `
        $PayloadDirectory `
        'taskflow'

    # --------------------------------------------------------
    # Restore de la solution
    # --------------------------------------------------------

    Write-Host
    Write-Host '============================================================'
    Write-Host 'Restore de la solution'
    Write-Host '============================================================'

    Invoke-NativeCommand `
        -Command 'dotnet' `
        -Arguments @(
            'restore'
            $SolutionFile
            '-p:NuGetAudit=false'
        ) `
        -ErrorMessage 'Échec du restore de la solution.'

    # --------------------------------------------------------
    # Build des applications
    # --------------------------------------------------------

    Write-Host
    Write-Host '============================================================'
    Write-Host 'Build des applications'
    Write-Host '============================================================'

    Build-Project `
        -Name 'API' `
        -Project $ApiProject `
        -OutputDirectory $ApiOutput

    Build-Project `
        -Name 'HPC Lite Scheduler' `
        -Project $HpcLiteSchedulerProject `
        -OutputDirectory $HpcLiteSchedulerOutput

    Build-Project `
        -Name 'HPC Lite Agent' `
        -Project $HpcLiteAgentProject `
        -OutputDirectory $HpcLiteAgentOutput

    Build-Project `
        -Name 'HPC Lite Runner' `
        -Project $HpcLiteRunnerProject `
        -OutputDirectory $HpcLiteRunnerOutput

    Build-Project `
        -Name 'Taskflow' `
        -Project $TaskflowProject `
        -OutputDirectory $TaskflowOutput

    # --------------------------------------------------------
    # Création du projet de packaging
    # --------------------------------------------------------

    Write-Host
    Write-Host '============================================================'
    Write-Host 'Création du projet NuGet temporaire'
    Write-Host '============================================================'

    New-PackProject -Path $PackProject

    # Supprime uniquement un package existant ayant la même version.
    $ExpectedPackageFile = Join-Path `
        $PackageOutputDirectory `
        "$PackageId.$PackageVersion.nupkg"

    if (Test-Path -LiteralPath $ExpectedPackageFile) {
        Remove-Item -LiteralPath $ExpectedPackageFile -Force
    }

    # --------------------------------------------------------
    # Restore du projet de packaging
    # --------------------------------------------------------

    Write-Host
    Write-Host '============================================================'
    Write-Host 'Restore du projet de packaging'
    Write-Host '============================================================'

    Invoke-NativeCommand `
        -Command 'dotnet' `
        -Arguments @(
            'restore'
            $PackProject
            '--configfile', $NuGetConfig
            '-p:NuGetAudit=false'
        ) `
        -ErrorMessage 'Échec du restore du projet de packaging.'

    # --------------------------------------------------------
    # Génération du package NuGet
    # --------------------------------------------------------

    Write-Host
    Write-Host '============================================================'
    Write-Host 'Génération du package NuGet'
    Write-Host '============================================================'

    $packStartTime = Get-Date

    Invoke-NativeCommand `
        -Command 'dotnet' `
        -Arguments @(
            'pack'
            $PackProject
            '--no-build'
            '--no-restore'
            '--configuration', 'Release'
            '--output', $PackageOutputDirectory
        ) `
        -ErrorMessage 'Échec de génération du package NuGet.'

    # NuGet peut normaliser la représentation de la version dans
    # le nom du fichier. On recherche donc le package effectivement
    # créé plutôt que de dépendre uniquement du nom attendu.
    $GeneratedPackage = Get-ChildItem `
        -LiteralPath $PackageOutputDirectory `
        -Filter "$PackageId.*.nupkg" `
        -File |
        Where-Object {
            $_.LastWriteTime -ge $packStartTime.AddSeconds(-2)
        } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $GeneratedPackage) {
        throw "Le package NuGet n'a pas été trouvé dans '$PackageOutputDirectory'."
    }

    # --------------------------------------------------------
    # Nettoyage
    # --------------------------------------------------------

    if (Test-Path -LiteralPath $WorkDirectory) {
        Remove-Item `
            -LiteralPath $WorkDirectory `
            -Recurse `
            -Force
    }

    # --------------------------------------------------------
    # Résultat
    # --------------------------------------------------------

    Write-Host
    Write-Host '============================================================' -ForegroundColor Green
    Write-Host 'Package NuGet généré avec succès' -ForegroundColor Green
    Write-Host '============================================================' -ForegroundColor Green
    Write-Host
    Write-Host 'Package :'
    Write-Host "  $($GeneratedPackage.FullName)" -ForegroundColor Cyan
    Write-Host
    Write-Host 'Contenu :'
    Write-Host '  tools\api'
    Write-Host '  tools\hpclite\scheduler'
    Write-Host '  tools\hpclite\agent'
    Write-Host '  tools\hpclite\runner'
    Write-Host '  tools\taskflow'
    Write-Host

    exit 0
}
catch {
    Write-Host
    Write-Host '============================================================' -ForegroundColor Red
    Write-Host 'ERREUR pendant le build ou la génération du package' -ForegroundColor Red
    Write-Host '============================================================' -ForegroundColor Red
    Write-Host
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host
    Write-Host 'Répertoire de diagnostic conservé :'
    Write-Host "  $WorkDirectory"
    Write-Host

    exit 1
}
```

## int/push-artifactory.ps1

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Pousse un package .nupkg Styx vers Artifactory, en intégration (machine du
    runner), sans git.

.DESCRIPTION
    Le fichier à pousser est OBLIGATOIRE : aucun « dernier package trouvé »,
    pour ne jamais pousser un ancien package par erreur.

    Une version déjà présente sur Artifactory est une ERREUR (pas de
    --skip-duplicate) : deux builds ne doivent pas produire la même version.

    L'adresse et le dépôt Artifactory sont lus dans
    devops\config\<Environment>.psd1 (section Artifactory).

    La clé d'API est lue dans la variable d'environnement nommée par
    Artifactory.ApiKeyVariable. Le runner la lit dans une variable GitLab et
    la transmet au script par l'environnement du processus : elle n'est
    jamais écrite dans un fichier ni affichée.

.PARAMETER PackageFile
    Chemin du .nupkg, tel que produit par package.ps1.

.PARAMETER RepositoryRoot
    Racine des sources, obligatoire : git n'est pas disponible en intégration.

.PARAMETER Environment
    Nom de l'environnement (devops\config\<Environment>.psd1). Par défaut : int.

.EXAMPLE
    $env:ARTIFACTORY_API_KEY = "..."
    .\push-artifactory.ps1 -PackageFile D:\ci\packages\1.0.913231\Styx.Publish.1.0.913231.nupkg -RepositoryRoot D:\ci\build

.NOTES
    Code de sortie : 0 succès, 1 erreur.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageFile,

    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot,

    [string] $Environment = 'int'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "..\modules\StyxBuild.psm1") -Force
Initialize-StyxOutput

try {
    Write-StyxStep "STYX - Push NuGet vers Artifactory"

    Assert-StyxCommand -Name "dotnet"
    $config = Get-StyxConfiguration `
        -ScriptDirectory $PSScriptRoot `
        -RepositoryRoot $RepositoryRoot `
        -Environment $Environment

    if (-not (Test-Path -LiteralPath $PackageFile -PathType Leaf)) {
        throw "Package introuvable : $PackageFile"
    }

    $package = Get-Item -LiteralPath $PackageFile

    if ($package.Extension -ne ".nupkg") {
        throw "Le fichier n'est pas un package NuGet (.nupkg) : $($package.FullName)"
    }

    # Clé d'API : session, puis utilisateur, puis machine.
    $variable = $config.ArtifactoryApiKeyVariable
    $apiKey = Get-StyxSecretValue -Name $variable

    if ($null -eq $apiKey) {
        throw "Variable d'environnement $variable non définie. Exemple : `$env:$variable = `"<clé>`""
    }

    Write-StyxInfo "Package : $($package.FullName)"
    Write-StyxInfo "Repo    : $($config.ArtifactoryRepo)"
    Write-StyxInfo "Source  : $($config.ArtifactorySource)"
    Write-Host ""

    $pushArguments = @(
        'nuget', 'push', $package.FullName,
        '--source', $config.ArtifactorySource,
        '--api-key', $apiKey
    )

    # La clé est remplacée par *** dans l'affichage de la commande.
    $displayArguments = @(
        'nuget', 'push', $package.FullName,
        '--source', $config.ArtifactorySource,
        '--api-key', '***'
    )

    try {
        Invoke-StyxNative `
            -FilePath 'dotnet' `
            -Arguments $pushArguments `
            -DisplayArguments $displayArguments `
            -Description 'Le push vers Artifactory'
    }
    catch {
        throw (
            $_.Exception.Message + [Environment]::NewLine +
            "Si le message indique un conflit (409), cette version existe déjà sur Artifactory : chaque version correspond à un pipeline GitLab, un nouveau commit sur main produira une nouvelle version."
        )
    }

    Write-StyxSuccess "Package poussé avec succès : $($package.Name)"
    exit 0
}
catch {
    Write-StyxFailure $_.Exception.Message
    exit 1
}
```
