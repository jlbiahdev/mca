# US 6 — Scripts de build int à ajouter au dépôt styx-poc

À ajouter par une MR dans `src/scripts/devops/`, en **UTF-8 avec BOM** et CRLF.

Même chaîne qu'en dev (`build_release` → `build` → `StyxBuild`), dans un dossier `int`, sans git : la racine des sources est fournie par `-RepositoryRoot` (obligatoire) et la configuration est `config/int.psd1`.

Les valeurs `XXXXX` sont masquées : les renseigner avec les vraies valeurs dans le dépôt styx-poc (jamais dans mca).

## src/scripts/devops/int/build_release.ps1

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Compile toute la solution Styx en Release (environnement d'intégration,
    sans git).

.PARAMETER RepositoryRoot
    Racine des sources, obligatoire : en intégration, git n'est pas disponible.

.PARAMETER Environment
    Nom de l'environnement (devops\config\<Environment>.psd1). Par défaut : int.

.EXAMPLE
    .\build_release.ps1 -RepositoryRoot D:\ci\build
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RepositoryRoot,

    [string] $Environment = 'int'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$buildScript = Join-Path $PSScriptRoot "build.ps1"

if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    Write-Error "Script de build introuvable : $buildScript"
    exit 1
}

& $buildScript -Configuration "Release" -RepositoryRoot $RepositoryRoot -Environment $Environment

$exitCode = $LASTEXITCODE

if ($null -eq $exitCode) {
    $exitCode = 1
}

exit $exitCode
```

## src/scripts/devops/int/build.ps1

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Compile l'intégralité de la solution STYX (environnement d'intégration,
    sans git).

.DESCRIPTION
    Le script :
    1. valide l'environnement ;
    2. restaure les dépendances, sauf avec -NoRestore ;
    3. nettoie la solution, sauf avec -NoClean ;
    4. compile la solution dans la configuration demandée.

    Les binaires sont générés dans les répertoires standards de chaque projet :

        bin\<Configuration>\<TargetFramework>\

    Le script n'effectue aucune publication et ne crée aucun package.

.PARAMETER Configuration
    Configuration de compilation : Debug ou Release.

    La valeur par défaut est Debug.

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
    Racine des sources, obligatoire : en intégration, git n'est pas disponible.
    Le runner fournit le dossier des sources récupérées par l'API GitLab.

.PARAMETER Environment
    Nom de l'environnement (devops\config\<Environment>.psd1). Par défaut : int.

.EXAMPLE
    .\build.ps1 -RepositoryRoot D:\ci\build

.EXAMPLE
    .\build.ps1 -Configuration Release -RepositoryRoot D:\ci\build

.EXAMPLE
    .\build.ps1 -Configuration Debug -NoClean -RepositoryRoot D:\ci\build

.EXAMPLE
    .\build.ps1 -Configuration Release -NoRestore -RepositoryRoot D:\ci\build

.EXAMPLE
    .\build.ps1 -IgnoreFailedSources -DisableNuGetAudit -RepositoryRoot D:\ci\build
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

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

## src/scripts/devops/modules/StyxBuild.psm1

Version qui accepte `-RepositoryRoot` sans appeler git (git n'est utilisé que si la racine n'est pas fournie, comme en dev).

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Primitives communes aux scripts de build, publication et packaging STYX.

.DESCRIPTION
    Ce module centralise :
    - la découverte du dépôt Git ;
    - la résolution des chemins de la solution et des projets ;
    - la configuration de build, publication et packaging, lue dans
      devops\config\<environnement>.psd1 ;
    - l'exécution contrôlée des commandes natives ;
    - la création des dossiers de travail et archives ZIP ;
    - certaines interrogations Git.

    Ce module ne gère pas :
    - le démarrage ou l'arrêt des applications ;
    - les processus et PID ;
    - les ports ;
    - les sessions de développement (.run\<configuration>\session.json).

    La gestion des processus relève de Styx.Processes.psm1.
#>

Set-StrictMode -Version Latest

# ============================================================
# Configuration
# ============================================================

# Chemins relatifs à la racine du dépôt Git.
$script:SrcRelativePath      = 'src'
$script:BackendRelativePath  = 'src\backend'
$script:SolutionRelativePath = 'src\backend\styxapipoc.sln'

# Projets publiables, exécutables produits et sous-dossiers du package.
$script:PublishedProjects = @(
    [pscustomobject]@{
        Name       = 'API'
        Folder     = 'api'
        Project    = 'Styx.WebApi\Styx.Front.WebApi.csproj'
        Executable = 'Styx.Front.WebApi.exe'
    }
    [pscustomobject]@{
        Name       = 'HpcLite Scheduler'
        Folder     = 'hpclite\scheduler'
        Project    = 'Styx.Application.HpcLite\scheduler\Styx.Application.HpcLite.Scheduler.csproj'
        Executable = 'Styx.HpcLite.Scheduler.exe'
    }
    [pscustomobject]@{
        Name       = 'HpcLite Agent'
        Folder     = 'hpclite\agent'
        Project    = 'Styx.Application.HpcLite\agent\Styx.Application.HpcLite.Agent.csproj'
        Executable = 'Styx.HpcLite.Agent.exe'
    }
    [pscustomobject]@{
        Name       = 'HpcLite Runner'
        Folder     = 'hpclite\runner'
        Project    = 'Styx.Application.HpcLite\runner\Styx.Application.HpcLite.Runner.csproj'
        Executable = 'Styx.HpcLite.Runner.exe'
    }
    [pscustomobject]@{
        Name       = 'Taskflow'
        Folder     = 'taskflow'
        Project    = 'Styx.Application.TaskFlow\runner\Styx.Application.TaskFlow.Runner.csproj'
        Executable = 'Styx.TaskFlow.Runner.exe'
    }
)

# Propriétés historiques conservées pour les appelants existants.
#
# Elles ne doivent pas être utilisées par le nouveau build.ps1 :
# celui-ci active explicitement ces options au moyen de paramètres.
$script:LegacyDotnetRestoreArgs = @(
    '--ignore-failed-sources'
    '-p:NuGetAudit=false'
)

$script:LegacyDotnetBuildArgs = @(
    '-p:NuGetAudit=false'
)

# Arguments communs et non sensibles pour les nouveaux scripts.
$script:DotnetRestoreArguments = @()
$script:DotnetBuildArguments = @(
    '-clp:ErrorsOnly;NoSummary'
)


# Package NuGet.
$script:DefaultPackageId          = 'Styx.Publish'
$script:PackageVersionMajor       = 1
$script:PackageArchiveName        = 'styx_publish.zip'
$script:PackageTargetFramework    = 'net8.0'

# Les valeurs propres à un environnement (réseau, chemins de travail,
# partage, Artifactory, runtime, variables des applications) sont lues
# dans devops\config\<environnement>.psd1.
$script:ConfigDirectory    = Join-Path (Split-Path -Parent $PSScriptRoot) 'config'
$script:DefaultEnvironment = 'dev'

# ============================================================
# Fonctions internes
# ============================================================

<#
.SYNOPSIS
    Formate un argument uniquement pour l'affichage de la commande.

.DESCRIPTION
    Cette fonction ne doit pas être utilisée pour construire les arguments
    réellement transmis au processus.
#>
function ConvertTo-StyxDisplayArgument {
    param(
        [AllowEmptyString()]
        [string] $Argument
    )

    if ([string]::IsNullOrEmpty($Argument)) {
        return '""'
    }

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    return '"{0}"' -f $Argument.Replace('"', '\"')
}

function Format-StyxCommandLine {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [AllowEmptyCollection()]
        [string[]] $Arguments = @()
    )

    $displayArguments = @(
        $Arguments | ForEach-Object {
            ConvertTo-StyxDisplayArgument -Argument $_
        }
    )

    if ($displayArguments.Count -eq 0) {
        return $FilePath
    }

    return '{0} {1}' -f $FilePath, ($displayArguments -join ' ')
}

# ============================================================
# Affichage
# ============================================================

<#
.SYNOPSIS
    Initialise l'encodage UTF-8 lorsque la sortie est redirigée.
#>
function Initialize-StyxOutput {
    $utf8Encoding = New-Object System.Text.UTF8Encoding $false

    try {
        # Utilisé par Windows PowerShell pour décoder la sortie des
        # commandes natives, notamment lorsque leur sortie est capturée.
        [Console]::OutputEncoding = $utf8Encoding
    }
    catch {
        # Un échec de configuration de l'encodage ne doit pas bloquer le build.
    }

    try {
        # Utilisé par PowerShell lorsqu'il transmet du texte à une
        # commande native via le pipeline.
        $global:OutputEncoding = $utf8Encoding
    }
    catch {
        # Un échec de configuration de l'encodage ne doit pas bloquer le build.
    }
}


function Write-StyxStep {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Title
    )

    Write-Host ''
    Write-Host ('=' * 62) -ForegroundColor Cyan
    Write-Host $Title -ForegroundColor Cyan
    Write-Host ('=' * 62) -ForegroundColor Cyan
}

function Write-StyxInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message,

        [System.ConsoleColor] $ForegroundColor
    )

    if ($PSBoundParameters.ContainsKey('ForegroundColor')) {
        Write-Host $Message -ForegroundColor $ForegroundColor
        return
    }

    Write-Host $Message
}

function Write-StyxSuccess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    Write-Host ''
    Write-Host ('=' * 62) -ForegroundColor Green
    Write-Host $Message -ForegroundColor Green
    Write-Host ('=' * 62) -ForegroundColor Green
}

function Write-StyxFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    Write-Host ''
    Write-Host ('=' * 62) -ForegroundColor Red
    Write-Host "ERREUR : $Message" -ForegroundColor Red
    Write-Host ('=' * 62) -ForegroundColor Red
}

# ============================================================
# Commandes externes
# ============================================================

<#
.SYNOPSIS
    Vérifie qu'une commande native est disponible.
#>
function Assert-StyxCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $command = Get-Command `
        -Name $Name `
        -CommandType Application `
        -ErrorAction SilentlyContinue

    if ($null -eq $command) {
        throw "La commande '$Name' est introuvable dans le PATH."
    }
}

<#
.SYNOPSIS
    Exécute une commande native et lève une exception en cas d'échec.

.DESCRIPTION
    La commande est affichée avant son exécution.

    DisplayArguments permet de fournir des arguments neutralisés pour les logs,
    différents des arguments réellement transmis au processus.

    La sortie de la commande est affichée au fil de l'eau ; ses dernières
    lignes sont ajoutées au message d'erreur en cas d'échec.

.PARAMETER MaxSuccessExitCode
    Plus grand code de sortie considéré comme un succès.

    Cette option permet notamment de prendre en charge robocopy, dont plusieurs
    codes supérieurs à zéro représentent un succès.

.PARAMETER DiagnosticLineCount
    Nombre de dernières lignes de sortie reprises dans le message d'erreur.
#>
function Invoke-StyxNative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [AllowEmptyCollection()]
        [string[]] $Arguments = @(),

        [AllowEmptyCollection()]
        [string[]] $DisplayArguments,

        [string] $Description = $FilePath,

        [ValidateRange(0, [int]::MaxValue)]
        [int] $MaxSuccessExitCode = 0,

        [ValidateRange(0, 1000)]
        [int] $DiagnosticLineCount = 40
    )

    Assert-StyxCommand -Name $FilePath

    $argumentsToDisplay = $Arguments

    if ($PSBoundParameters.ContainsKey('DisplayArguments')) {
        $argumentsToDisplay = $DisplayArguments
    }

    $displayCommand = Format-StyxCommandLine `
        -FilePath $FilePath `
        -Arguments $argumentsToDisplay

    Write-Host "> $displayCommand" -ForegroundColor DarkGray

    # En Windows PowerShell 5.1, stderr peut produire des ErrorRecord.
    # On évite qu'ErrorActionPreference = Stop interrompe prématurément
    # la récupération du code de sortie natif.
    $ErrorActionPreference = 'Continue'

    # La sortie est affichée au fil de l'eau ; seules les dernières lignes
    # sont conservées pour le diagnostic.
    $lastLines = New-Object 'System.Collections.Generic.Queue[string]'

    try {
        & $FilePath @Arguments 2>&1 | ForEach-Object {
            $line = [string] $_
            Write-Host $line

            if ($DiagnosticLineCount -gt 0) {
                $lastLines.Enqueue($line)

                if ($lastLines.Count -gt $DiagnosticLineCount) {
                    [void] $lastLines.Dequeue()
                }
            }
        }

        $exitCode = $LASTEXITCODE
    }
    catch {
        throw "$Description n'a pas pu être exécuté. Commande : $displayCommand. $($_.Exception.Message)"
    }

    if ($null -eq $exitCode) {
        throw "$Description n'a retourné aucun code de sortie. Commande : $displayCommand"
    }

    if ($exitCode -lt 0 -or $exitCode -gt $MaxSuccessExitCode) {
        $message = @(
            "$Description a échoué."
            ''
            "Commande : $displayCommand"
            "Code de sortie : $exitCode"
        )

        if ($lastLines.Count -gt 0) {
            $message += ''
            $message += "Dernières lignes de sortie ($($lastLines.Count)) :"
            $message += $lastLines.ToArray()
        }

        throw ($message -join [Environment]::NewLine)
    }
}

<#
.SYNOPSIS
    Exécute une commande native sans afficher sa sortie.

.DESCRIPTION
    Retourne stdout, stderr et le code de sortie.

    La fonction ne lève pas d'exception lorsque le processus retourne un code
    non nul : l'appelant décide du traitement à appliquer.

    Une erreur de démarrage du processus provoque en revanche une exception.
#>
function Invoke-StyxNativeQuery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [AllowEmptyCollection()]
        [string[]] $Arguments = @()
    )

    Assert-StyxCommand -Name $FilePath

    $stderrFile = Join-Path `
        ([IO.Path]::GetTempPath()) `
        ("styx-native-{0}.stderr" -f [guid]::NewGuid().ToString('N'))

    $ErrorActionPreference = 'Continue'

    try {
        try {
            $stdout = @(& $FilePath @Arguments 2> $stderrFile)
            $exitCode = $LASTEXITCODE
        }
        catch {
            $displayCommand = Format-StyxCommandLine `
                -FilePath $FilePath `
                -Arguments $Arguments

            throw "Impossible d'exécuter la commande '$displayCommand'. $($_.Exception.Message)"
        }

        $stderr = @()

        if (Test-Path -LiteralPath $stderrFile -PathType Leaf) {
            $stderr = @(
                Get-Content -LiteralPath $stderrFile -ErrorAction SilentlyContinue |
                    ForEach-Object { [string] $_ }
            )
        }

        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = @(
                $stdout | ForEach-Object { [string] $_ }
            )
            ErrorOutput = @($stderr)
        }
    }
    finally {
        if (Test-Path -LiteralPath $stderrFile -PathType Leaf) {
            Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue
        }
    }
}

# ============================================================
# Dépôt et configuration
# ============================================================

<#
.SYNOPSIS
    Retourne la racine du dépôt Git contenant le répertoire fourni.
#>
function Get-StyxRepositoryRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ScriptDirectory
    )

    Assert-StyxCommand -Name 'git'

    if (-not (Test-Path -LiteralPath $ScriptDirectory -PathType Container)) {
        throw "Le répertoire utilisé pour détecter le dépôt Git est introuvable : $ScriptDirectory"
    }

    $resolvedScriptDirectory = [IO.Path]::GetFullPath(
        (Resolve-Path -LiteralPath $ScriptDirectory -ErrorAction Stop).Path
    )

    $result = Invoke-StyxNativeQuery `
        -FilePath 'git' `
        -Arguments @(
            '-C'
            $resolvedScriptDirectory
            'rev-parse'
            '--show-toplevel'
        )

    if (
        $result.ExitCode -ne 0 -or
        $result.Output.Count -eq 0 -or
        [string]::IsNullOrWhiteSpace($result.Output[0])
    ) {
        $message = @(
            "Impossible de détecter la racine du dépôt Git depuis :"
            $resolvedScriptDirectory
            "Code de sortie Git : $($result.ExitCode)"
        )

        if ($result.ErrorOutput.Count -gt 0) {
            $message += 'Diagnostic Git :'
            $message += $result.ErrorOutput
        }

        throw ($message -join [Environment]::NewLine)
    }

    return [IO.Path]::GetFullPath($result.Output[0].Trim())
}

<#
.SYNOPSIS
    Lit le fichier de configuration d'un environnement.

.DESCRIPTION
    Le fichier est un .psd1 lu avec Import-PowerShellDataFile : aucun code
    n'y est exécuté. Les sections obligatoires sont vérifiées.

    Les chemins de la section Paths sont développés (%TEMP%...) et les
    marqueurs {HostName}, {BindHost}, {ApiPort}, {AgentPort}, {TaskFlowPort}
    des variables d'application sont remplacés.
#>
function Get-StyxEnvironmentSettings {
    [CmdletBinding()]
    param(
        [string] $Environment = $script:DefaultEnvironment,

        [string] $ConfigFile
    )

    if ([string]::IsNullOrWhiteSpace($ConfigFile)) {
        $ConfigFile = Join-Path $script:ConfigDirectory "$Environment.psd1"
    }

    if (-not (Test-Path -LiteralPath $ConfigFile -PathType Leaf)) {
        throw "Fichier de configuration introuvable : $ConfigFile"
    }

    $configPath = [IO.Path]::GetFullPath(
        (Resolve-Path -LiteralPath $ConfigFile -ErrorAction Stop).Path
    )

    try {
        $settings = Import-PowerShellDataFile -LiteralPath $configPath -ErrorAction Stop
    }
    catch {
        throw "Fichier de configuration illisible : $configPath. $($_.Exception.Message)"
    }

    $requiredSections = @(
        'Network', 'DotNet', 'Paths', 'Share', 'Artifactory', 'Applications'
    )

    foreach ($section in $requiredSections) {
        if (-not $settings.ContainsKey($section)) {
            throw "Section '$section' absente du fichier de configuration : $configPath"
        }
    }

    $requiredValues = @(
        @('Network', 'HostName'), @('Network', 'BindHost'),
        @('Network', 'ApiPort'), @('Network', 'AgentPort'), @('Network', 'TaskFlowPort'),
        @('DotNet', 'Configuration'), @('DotNet', 'Runtime'), @('DotNet', 'SelfContained'),
        @('Paths', 'WorkRoot'), @('Paths', 'PackageOutput'),
        @('Share', 'Path'),
        @('Artifactory', 'BaseUrl'), @('Artifactory', 'Repository'), @('Artifactory', 'ApiKeyVariable')
    )

    foreach ($pair in $requiredValues) {
        $section = $settings[$pair[0]]

        if (-not $section.ContainsKey($pair[1]) -or $null -eq $section[$pair[1]]) {
            throw "Valeur '$($pair[0]).$($pair[1])' absente du fichier de configuration : $configPath"
        }
    }

    $network = $settings.Network

    $markers = @{
        '{HostName}'     = [string] $network.HostName
        '{BindHost}'     = [string] $network.BindHost
        '{ApiPort}'      = [string] $network.ApiPort
        '{AgentPort}'    = [string] $network.AgentPort
        '{TaskFlowPort}' = [string] $network.TaskFlowPort
    }

    $applicationEnvironment = @{}

    if ($settings.Applications.ContainsKey('Environment')) {
        foreach ($name in @($settings.Applications.Environment.Keys)) {
            $value = [string] $settings.Applications.Environment[$name]

            foreach ($marker in $markers.Keys) {
                $value = $value.Replace($marker, $markers[$marker])
            }

            $applicationEnvironment[$name] = $value
        }
    }

    $secretVariables = @()

    if ($settings.Applications.ContainsKey('SecretVariables')) {
        $secretVariables = @($settings.Applications.SecretVariables)
    }

    # Environnement .NET utilisé par run_all.ps1 -Development (validation
    # de l'injection de dépendances et user-secrets).
    $developmentEnvironment = 'Development'

    if ($settings.ContainsKey('Development') -and $settings.Development.ContainsKey('Environment')) {
        $developmentEnvironment = [string] $settings.Development.Environment
    }

    return [pscustomobject]@{
        Name                   = if ($settings.ContainsKey('Environment')) { [string] $settings.Environment } else { $Environment }
        ConfigFile             = $configPath
        Network                = $network
        DotNet                 = $settings.DotNet
        WorkRoot               = [Environment]::ExpandEnvironmentVariables([string] $settings.Paths.WorkRoot)
        PackageOutput          = [Environment]::ExpandEnvironmentVariables([string] $settings.Paths.PackageOutput)
        Share                  = $settings.Share
        Artifactory            = $settings.Artifactory
        ApplicationEnvironment = $applicationEnvironment
        SecretVariables        = $secretVariables
        DevelopmentEnvironment = $developmentEnvironment
    }
}

<#
.SYNOPSIS
    Retourne la valeur d'une variable d'environnement portant un secret.

.DESCRIPTION
    La variable est cherchée dans la session, puis au niveau utilisateur,
    puis au niveau machine. Retourne $null si elle est absente ou vide.
    La valeur ne doit jamais être affichée par l'appelant.
#>
function Get-StyxSecretValue {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    foreach ($target in @('Process', 'User', 'Machine')) {
        $value = [Environment]::GetEnvironmentVariable($Name, $target)

        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return $null
}

<#
.SYNOPSIS
    Retourne la configuration STYX avec des chemins absolus validés.

.DESCRIPTION
    La racine des sources est :
    - celle fournie par -RepositoryRoot (cas du runner, qui récupère les
      sources par l'API GitLab) ;
    - sinon, la racine du dépôt Git contenant -ScriptDirectory (poste de dev).

    Le comportement est identique dans les deux cas.

    Les nouvelles propriétés normalisées sont :
    - RepositoryRoot
    - SourceRoot
    - BackendRoot
    - SolutionPath
    - Projects
    - DotNet
    - Environment (configuration de l'environnement, voir Get-StyxEnvironmentSettings)

    Les propriétés historiques suivantes sont conservées pour compatibilité :
    - SrcRoot
    - SolutionFile
    - Configuration
    - Runtime
    - SelfContained
    - RestoreArgs
    - BuildArgs
#>
function Get-StyxConfiguration {
    [CmdletBinding()]
    param(
        [string] $ScriptDirectory,

        [string] $RepositoryRoot,

        [string] $Environment = $script:DefaultEnvironment,

        [string] $ConfigFile
    )

    if (
        [string]::IsNullOrWhiteSpace($RepositoryRoot) -and
        [string]::IsNullOrWhiteSpace($ScriptDirectory)
    ) {
        throw 'Get-StyxConfiguration : indiquer -RepositoryRoot ou -ScriptDirectory.'
    }

    $resolvedScriptDirectory = $null

    if (-not [string]::IsNullOrWhiteSpace($ScriptDirectory)) {
        $resolvedScriptDirectory = [IO.Path]::GetFullPath(
            (Resolve-Path -LiteralPath $ScriptDirectory -ErrorAction Stop).Path
        )
    }

    if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
            throw "Racine des sources introuvable : $RepositoryRoot"
        }

        $repositoryRoot = [IO.Path]::GetFullPath(
            (Resolve-Path -LiteralPath $RepositoryRoot -ErrorAction Stop).Path
        ).TrimEnd('\', '/')
    }
    else {
        $repositoryRoot = Get-StyxRepositoryRoot `
            -ScriptDirectory $resolvedScriptDirectory
    }

    $environmentSettings = Get-StyxEnvironmentSettings `
        -Environment $Environment `
        -ConfigFile $ConfigFile

    $sourceRoot = [IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot $script:SrcRelativePath)
    )

    $backendRoot = [IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot $script:BackendRelativePath)
    )

    $solutionPath = [IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot $script:SolutionRelativePath)
    )

    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Dossier source introuvable : $sourceRoot"
    }

    if (-not (Test-Path -LiteralPath $backendRoot -PathType Container)) {
        throw "Dossier backend introuvable : $backendRoot"
    }

    if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
        throw "Solution STYX introuvable : $solutionPath"
    }

    $projects = @(
        foreach ($projectDefinition in $script:PublishedProjects) {
            $projectPath = [IO.Path]::GetFullPath(
                (Join-Path $backendRoot $projectDefinition.Project)
            )

            if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
                throw "Projet '$($projectDefinition.Name)' introuvable : $projectPath"
            }

            if ([string]::IsNullOrWhiteSpace($projectDefinition.Executable)) {
                throw "Aucun exécutable n'est configuré pour le projet '$($projectDefinition.Name)'."
            }

            [pscustomobject]@{
                Name       = $projectDefinition.Name
                Folder     = $projectDefinition.Folder
                Project    = $projectPath
                Executable = $projectDefinition.Executable
            }
        }
    )

    $dotNetConfiguration = [pscustomobject]@{
        Executable = 'dotnet'

        # Arguments sûrs pour les nouveaux scripts.
        RestoreArguments = @($script:DotnetRestoreArguments)
        BuildArguments   = @($script:DotnetBuildArguments)

        # Valeurs utilisées par la publication et le packaging.
        DefaultConfiguration = [string] $environmentSettings.DotNet.Configuration
        Runtime              = [string] $environmentSettings.DotNet.Runtime
        SelfContained        = [bool] $environmentSettings.DotNet.SelfContained
    }

    # Les fichiers annexes du partage sont résolus depuis le dossier du
    # script appelant lorsqu'il est connu, sinon depuis la racine des sources.
    $extraFilesBase = $resolvedScriptDirectory

    if ($null -eq $extraFilesBase) {
        $extraFilesBase = $repositoryRoot
    }

    $shareExtraFiles = @()

    if ($environmentSettings.Share.ContainsKey('ExtraFiles')) {
        $shareExtraFiles = @(
            $environmentSettings.Share.ExtraFiles | ForEach-Object {
                Join-Path $extraFilesBase $_
            }
        )
    }

    return [pscustomobject]@{
        # Contrat normalisé.
        RepositoryRoot  = $repositoryRoot
        SourceRoot      = $sourceRoot
        BackendRoot     = $backendRoot
        SolutionPath    = $solutionPath
        ScriptDirectory = $resolvedScriptDirectory
        Projects        = $projects
        DotNet          = $dotNetConfiguration
        Environment     = $environmentSettings

        # Propriétés historiques conservées pour compatibilité.
        SrcRoot       = $sourceRoot
        SolutionFile  = $solutionPath
        Configuration = $dotNetConfiguration.DefaultConfiguration
        Runtime       = $dotNetConfiguration.Runtime
        SelfContained = $dotNetConfiguration.SelfContained
        RestoreArgs   = @($script:LegacyDotnetRestoreArgs)
        BuildArgs     = @($script:LegacyDotnetBuildArgs)

        # Packaging.
        DefaultPackageId       = $script:DefaultPackageId
        PackageArchiveName     = $script:PackageArchiveName
        PackageTargetFramework = $script:PackageTargetFramework
        WorkRoot               = $environmentSettings.WorkRoot
        DefaultPackageOutput   = $environmentSettings.PackageOutput

        # Partage réseau.
        SharePath       = [string] $environmentSettings.Share.Path
        ShareExtraFiles = $shareExtraFiles

        # Artifactory.
        ArtifactorySource = '{0}/artifactory/api/nuget/v3/{1}' -f `
            ([string] $environmentSettings.Artifactory.BaseUrl).TrimEnd('/'), `
            [string] $environmentSettings.Artifactory.Repository

        ArtifactoryRepo           = [string] $environmentSettings.Artifactory.Repository
        ArtifactoryApiKeyVariable = [string] $environmentSettings.Artifactory.ApiKeyVariable
    }
}

function Write-StyxConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Config
    )

    Write-StyxInfo "Dépôt Git        : $($Config.RepositoryRoot)"
    Write-StyxInfo "Sources          : $($Config.SourceRoot)"
    Write-StyxInfo "Backend          : $($Config.BackendRoot)"
    Write-StyxInfo "Solution         : $($Config.SolutionPath)"
    Write-StyxInfo "Environnement    : $($Config.Environment.Name) ($($Config.Environment.ConfigFile))"

    foreach ($project in $Config.Projects) {
        Write-StyxInfo ('{0,-18}: {1}' -f $project.Name, $project.Project)
        Write-StyxInfo ('{0,-18}  Exécutable : {1}' -f '', $project.Executable)
    }
}

# ============================================================
# Version et dossiers de travail
# ============================================================

<#
.SYNOPSIS
    Génère une version au format <majeur>.<AAMMJJ>.<HHMMSS>.
#>
function New-StyxPackageVersion {
    param(
        [datetime] $Date = (Get-Date)
    )

    return '{0}.{1}.{2}' -f `
        $script:PackageVersionMajor, `
        [int] $Date.ToString('yyMMdd'), `
        [int] $Date.ToString('HHmmss')
}

<#
.SYNOPSIS
    Crée un dossier de travail unique pour l'exécution courante.
#>
function New-StyxWorkDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Config
    )

    $name = '{0}-{1}' -f `
        (Get-Date -Format 'yyyyMMdd-HHmmss'), `
        [guid]::NewGuid().ToString('N').Substring(0, 8)

    $path = Join-Path $Config.WorkRoot $name

    New-Item `
        -Path $path `
        -ItemType Directory `
        -Force `
        -ErrorAction Stop |
        Out-Null

    return $path
}

# ============================================================
# ZIP
# ============================================================

<#
.SYNOPSIS
    Crée une archive ZIP avec des séparateurs conformes « / ».
#>
function New-StyxZip {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string] $ZipFile
    )

    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) {
        throw "Dossier source du ZIP introuvable : $SourceDirectory"
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $source = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
    $zipPath = [IO.Path]::GetFullPath($ZipFile)
    $zipParent = Split-Path -Parent $zipPath

    if (-not (Test-Path -LiteralPath $zipParent -PathType Container)) {
        New-Item `
            -Path $zipParent `
            -ItemType Directory `
            -Force `
            -ErrorAction Stop |
            Out-Null
    }

    if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction Stop
    }

    $zip = [IO.Compression.ZipFile]::Open(
        $zipPath,
        [IO.Compression.ZipArchiveMode]::Create
    )

    try {
        foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File -Force) {
            $relativePath = $file.FullName.Substring($source.Length + 1)
            $relativePath = $relativePath.Replace('\', '/')

            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip,
                $file.FullName,
                $relativePath,
                [IO.Compression.CompressionLevel]::Optimal
            ) | Out-Null
        }
    }
    finally {
        if ($null -ne $zip) {
            $zip.Dispose()
        }
    }
}

# ============================================================
# Git
# ============================================================

<#
.SYNOPSIS
    Retourne les fichiers modifiés ou non suivis du dépôt.
#>
function Get-StyxUncommittedChanges {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
        throw "Dépôt Git introuvable : $RepositoryRoot"
    }

    $result = Invoke-StyxNativeQuery `
        -FilePath 'git' `
        -Arguments @(
            '-C'
            $RepositoryRoot
            'status'
            '--porcelain'
        )

    if ($result.ExitCode -ne 0) {
        $message = @(
            'git status a échoué.'
            "Code de sortie : $($result.ExitCode)"
        )

        if ($result.ErrorOutput.Count -gt 0) {
            $message += 'Diagnostic Git :'
            $message += $result.ErrorOutput
        }

        throw ($message -join [Environment]::NewLine)
    }

    return @(
        $result.Output |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_)
            }
    )
}

Export-ModuleMember -Function `
    Initialize-StyxOutput, `
    Write-StyxStep, `
    Write-StyxInfo, `
    Write-StyxSuccess, `
    Write-StyxFailure, `
    Invoke-StyxNative, `
    Invoke-StyxNativeQuery, `
    Assert-StyxCommand, `
    Get-StyxRepositoryRoot, `
    Get-StyxEnvironmentSettings, `
    Get-StyxSecretValue, `
    Get-StyxConfiguration, `
    Write-StyxConfiguration, `
    New-StyxPackageVersion, `
    New-StyxWorkDirectory, `
    New-StyxZip, `
    Get-StyxUncommittedChanges
```

## src/scripts/devops/config/int.psd1

Nouveau : configuration de la machine de POC. Pour le build, seule la section `DotNet` est utilisée ; les autres sont exigées par `StyxBuild.psm1`.

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
