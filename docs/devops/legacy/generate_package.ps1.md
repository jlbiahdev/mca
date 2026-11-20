# generate_package.ps1 — génération du package NuGet (ancienne version, non modifiée)

Contenu du fichier, à copier dans `src\scripts\devops\legacy\generate_package.ps1`.

À enregistrer en UTF-8 avec BOM (Windows PowerShell 5.1).

```powershell
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $PackageVersion,

    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ============================================================
# Configuration
# ============================================================

$PackageId = 'Styx.Publish'

$PackageOutputDirectory = Join-Path $env:TEMP 'styx_packages'
$WorkDirectory          = Join-Path $env:TEMP 'styx_package_work'
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

    Assert-Command -Name 'git'
    Assert-Command -Name 'dotnet'

    # --------------------------------------------------------
    # Détection du repository Git
    # --------------------------------------------------------

    $ScriptDirectory = $PSScriptRoot

    if ([string]::IsNullOrWhiteSpace($ScriptDirectory)) {
        $ScriptDirectory = (Get-Location).Path
    }

    $RepositoryRoot = & git -C $ScriptDirectory rev-parse --show-toplevel

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        throw "Impossible de détecter la racine du repository Git depuis '$ScriptDirectory'."
    }

    $RepositoryRoot = $RepositoryRoot.Trim()

    # --------------------------------------------------------
    # Chemins des projets
    # --------------------------------------------------------

    $BackendDirectory = Join-Path $RepositoryRoot 'src\backend'
    $SolutionFile     = Join-Path $BackendDirectory 'styxapipoc.sln'

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

    if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
        $now = Get-Date

        $PackageVersion = '{0}.{1}.{2}' -f `
            $now.Year,
            $now.ToString('MMdd'),
            $now.ToString('HHmmss')
    }

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
