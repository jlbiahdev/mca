# US 6 — Build : code

Ticket : voir `epic.md`, US 6. Copier les deux fichiers dans `D:\ci\runner`, en **UTF-8 avec BOM**, en remplaçant ceux de l'étape précédente (reprendre `GitLabUrl` et `ProjectId`).

## ci-dev.psd1

```powershell
@{
    # ── GitLab ──────────────────────────────────────────────────────────────
    # Adresse de GitLab, sans / final.
    GitLabUrl  = 'https://XXXXX'

    # Identifiant numérique du projet (page d'accueil du projet, menu « ⋮ »).
    ProjectId  = 0

    # Jeton GitLab chiffré par Windows (DPAPI) : lisible uniquement par le
    # compte qui l'a créé, sur cette machine.
    TokenFile  = 'D:\ci\runner\gitlab-token.xml'

    # ── Runner ──────────────────────────────────────────────────────────────
    # Dossier du runner : script, configuration, journaux, commits traités.
    RunnerRoot = 'D:\ci\runner'

    # ── Sources ─────────────────────────────────────────────────────────────
    # Dossier où sont déposées les sources du commit. Il est VIDÉ à chaque
    # commit : ne rien y mettre d'autre.
    BuildRoot  = 'D:\ci\build'

    # ── Build ───────────────────────────────────────────────────────────────
    # Script de build, relatif à la racine des sources téléchargées.
    BuildScript         = 'devops\dev\build.ps1'
    BuildConfiguration  = 'Release'

    # Environnement transmis au script de build (devops\config\<env>.psd1).
    BuildEnvironment    = 'dev'

    # Au-delà, le build est arrêté et déclaré en échec.
    BuildTimeoutMinutes = 30
}
```

## ci-dev.ps1

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Runner CI de dev (US 6) : compile le dernier commit de chaque MR.

.DESCRIPTION
    Pour chaque nouveau commit de MR, le script télécharge les sources, lance
    le script de build qu'elles contiennent (build.ps1 -RepositoryRoot), écrit
    le résultat dans le journal et note le commit avec ce résultat.

.PARAMETER ConfigFile
    Fichier de configuration. Par défaut : ci-dev.psd1, à côté du script.

.EXAMPLE
    .\ci-dev.ps1

.NOTES
    Code de sortie : 0 succès, 1 erreur.
#>
[CmdletBinding()]
param(
    [string] $ConfigFile = (Join-Path $PSScriptRoot 'ci-dev.psd1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# La barre de progression ralentit fortement les téléchargements en PowerShell 5.1.
$ProgressPreference = 'SilentlyContinue'

$script:Config  = $null
$script:LogFile = $null

# ── Configuration ────────────────────────────────────────────────────────────

function Read-CiConfiguration {
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Configuration introuvable : $Path"
    }

    $config = Import-PowerShellDataFile -LiteralPath $Path

    $requiredKeys = @(
        'GitLabUrl', 'ProjectId', 'TokenFile', 'RunnerRoot',
        'BuildRoot', 'BuildScript', 'BuildConfiguration', 'BuildEnvironment',
        'BuildTimeoutMinutes'
    )

    foreach ($key in $requiredKeys) {
        if (-not $config.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string] $config[$key])) {
            throw "Configuration incomplète : la clé '$key' est absente ou vide dans $Path"
        }
    }

    if ([int] $config.ProjectId -le 0) {
        throw "Configuration : ProjectId doit être l'identifiant numérique du projet GitLab."
    }

    if ([int] $config.BuildTimeoutMinutes -le 0) {
        throw "Configuration : BuildTimeoutMinutes doit être un nombre de minutes positif."
    }

    # Le dossier de build est vidé à chaque commit : on refuse une racine de lecteur.
    $buildRoot = [IO.Path]::GetFullPath($config.BuildRoot).TrimEnd('\')
    if ([IO.Path]::GetPathRoot($buildRoot).TrimEnd('\') -eq $buildRoot) {
        throw "Configuration : BuildRoot ne peut pas être la racine d'un lecteur ($buildRoot)."
    }

    $config.GitLabUrl = ([string] $config.GitLabUrl).TrimEnd('/')
    $config.BuildRoot = $buildRoot

    return $config
}

# ── Journal ──────────────────────────────────────────────────────────────────
# Un fichier par jour dans <RunnerRoot>\logs.

function Write-CiLog {
    param(
        [Parameter(Mandatory)]
        [string] $Message,

        [ValidateSet('INFO', 'ATTENTION', 'ERREUR')]
        [string] $Level = 'INFO'
    )

    $line  = '[{0}] [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    $color = switch ($Level) { 'ATTENTION' { 'Yellow' } 'ERREUR' { 'Red' } default { 'Gray' } }

    Write-Host $line -ForegroundColor $color

    if ($script:LogFile) {
        Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
    }
}

function Initialize-CiLog {
    $logFolder = Join-Path $script:Config.RunnerRoot 'logs'
    New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
    $script:LogFile = Join-Path $logFolder ('ci-dev-{0}.log' -f (Get-Date -Format 'yyyyMMdd'))
}

# ── GitLab ───────────────────────────────────────────────────────────────────

function Get-GitLabToken {
    $path = $script:Config.TokenFile

    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Fichier du jeton GitLab introuvable : $path"
    }

    try {
        $secureToken = Import-Clixml -LiteralPath $path
    }
    catch {
        throw "Impossible de déchiffrer le jeton GitLab. Le fichier doit avoir été créé par le compte qui lance ce script, sur cette machine."
    }

    return [Net.NetworkCredential]::new('', $secureToken).Password
}

function Invoke-GitLabApi {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [string] $Token,

        [ValidateSet('Get', 'Post')]
        [string] $Method = 'Get',

        [hashtable] $Body
    )

    $uri = '{0}/api/v4/{1}' -f $script:Config.GitLabUrl, $Path

    $parameters = @{
        Uri     = $uri
        Method  = $Method
        Headers = @{ 'PRIVATE-TOKEN' = $Token }
    }

    if ($Body) {
        $parameters.Body = $Body
    }

    # Windows PowerShell 5.1 émet un tableau JSON comme un seul objet : on le
    # stocke avant de le renvoyer pour que ses éléments soient bien énumérés.
    try {
        $response = Invoke-RestMethod @parameters
    }
    catch {
        $status = $null
        $exception = $_.Exception
        if ($exception.PSObject.Properties['Response'] -and $exception.Response) {
            $status = [int] $exception.Response.StatusCode
        }

        switch ($status) {
            401     { throw "GitLab refuse le jeton (401) : jeton invalide ou expiré." }
            403     { throw "GitLab refuse l'accès (403) : vérifier le rôle et les scopes du jeton ($Path)." }
            404     { throw "Ressource GitLab introuvable (404) : $Path" }
            default { throw "Appel GitLab impossible ($Method $Path) : $($_.Exception.Message)" }
        }
    }

    return $response
}

function Get-OpenMergeRequests {
    param(
        [Parameter(Mandatory)]
        [string] $Token
    )

    $path = 'projects/{0}/merge_requests?state=opened&per_page=100' -f $script:Config.ProjectId
    return @(Invoke-GitLabApi -Path $path -Token $Token)
}

# ── Sources ──────────────────────────────────────────────────────────────────
# Télécharge le zip du commit (généré par GitLab) et le dépose dans BuildRoot,
# après l'avoir vidé. Aucun besoin de git sur la machine.

function Save-CommitSources {
    param(
        [Parameter(Mandatory)]
        [string] $Commit,

        [Parameter(Mandatory)]
        [string] $Token
    )

    $workFolder = Join-Path $script:Config.RunnerRoot 'work'
    $zipFile    = Join-Path $workFolder 'sources.zip'
    $extractDir = Join-Path $workFolder 'extract'

    if (Test-Path -LiteralPath $workFolder) {
        Remove-Item -LiteralPath $workFolder -Recurse -Force
    }

    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

    Write-CiLog "Téléchargement des sources du commit $Commit"

    $uri = '{0}/api/v4/projects/{1}/repository/archive.zip?sha={2}' -f `
        $script:Config.GitLabUrl, $script:Config.ProjectId, $Commit

    try {
        Invoke-WebRequest -Uri $uri -Headers @{ 'PRIVATE-TOKEN' = $Token } -OutFile $zipFile -UseBasicParsing
    }
    catch {
        throw "Téléchargement des sources impossible : $($_.Exception.Message)"
    }

    Write-CiLog ('Archive reçue : {0:N1} Mo' -f ((Get-Item -LiteralPath $zipFile).Length / 1MB))

    Expand-Archive -LiteralPath $zipFile -DestinationPath $extractDir -Force

    # GitLab range tout le contenu dans un dossier racine <projet>-<commit>-<commit>.
    $rootFolders = @(Get-ChildItem -LiteralPath $extractDir -Directory)
    if ($rootFolders.Count -ne 1) {
        throw "Archive inattendue : un seul dossier racine attendu, $($rootFolders.Count) trouvé(s)."
    }

    $buildRoot = $script:Config.BuildRoot

    if (Test-Path -LiteralPath $buildRoot) {
        Get-ChildItem -LiteralPath $buildRoot -Force | Remove-Item -Recurse -Force
    }
    else {
        New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    }

    Get-ChildItem -LiteralPath $rootFolders[0].FullName -Force | Move-Item -Destination $buildRoot

    Remove-Item -LiteralPath $workFolder -Recurse -Force

    Write-CiLog "Sources déposées dans $buildRoot"
}

# ── Build ────────────────────────────────────────────────────────────────────
# Lance le script de build des sources téléchargées : c'est la version du
# script qui accompagne le commit qui compile ce commit.

function Invoke-CiBuild {
    param(
        [Parameter(Mandatory)]
        [string] $Commit
    )

    $buildRoot   = $script:Config.BuildRoot
    $buildScript = Join-Path $buildRoot $script:Config.BuildScript

    if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
        throw "Script de build introuvable dans les sources : $buildScript"
    }

    $buildLogFolder = Join-Path $script:Config.RunnerRoot 'builds'
    New-Item -ItemType Directory -Path $buildLogFolder -Force | Out-Null

    $stamp      = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Commit.Substring(0, 8)
    $outputLog  = Join-Path $buildLogFolder "$stamp.log"
    $errorLog   = Join-Path $buildLogFolder "$stamp.err.log"

    $arguments = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $buildScript),
        '-Configuration', $script:Config.BuildConfiguration,
        '-RepositoryRoot', ('"{0}"' -f $buildRoot),
        '-Environment', $script:Config.BuildEnvironment
    )

    Write-CiLog "Build : $buildScript ($($script:Config.BuildConfiguration))"
    Write-CiLog "Journal du build : $outputLog"

    $started = Get-Date

    $process = Start-Process `
        -FilePath 'powershell.exe' `
        -ArgumentList $arguments `
        -WorkingDirectory $buildRoot `
        -RedirectStandardOutput $outputLog `
        -RedirectStandardError $errorLog `
        -NoNewWindow `
        -PassThru

    # Sans cet accès, ExitCode peut rester vide en Windows PowerShell 5.1.
    $null = $process.Handle

    $timeoutMilliseconds = [int] $script:Config.BuildTimeoutMinutes * 60 * 1000

    if (-not $process.WaitForExit($timeoutMilliseconds)) {
        # On arrête le build (et dotnet) : ce n'est pas un composant applicatif.
        & taskkill.exe /PID $process.Id /T /F | Out-Null

        return [pscustomobject]@{
            Success  = $false
            Message  = "Build interrompu après $($script:Config.BuildTimeoutMinutes) min"
            LogFile  = $outputLog
            Duration = (Get-Date) - $started
        }
    }

    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $duration = (Get-Date) - $started

    if ($exitCode -eq 0) {
        $message = 'Build réussi'
    }
    else {
        $message = "Build échoué (code $exitCode)"
    }

    return [pscustomobject]@{
        Success  = ($exitCode -eq 0)
        Message  = $message
        LogFile  = $outputLog
        Duration = $duration
    }
}

# ── Commits traités ──────────────────────────────────────────────────────────
# Une ligne par commit : <commit> <tab> MR !<numéro> <tab> <résultat> <tab> <date>

function Get-ProcessedFile {
    return Join-Path $script:Config.RunnerRoot 'processed.txt'
}

function Test-AlreadyProcessed {
    param(
        [Parameter(Mandatory)]
        [string] $Commit
    )

    $file = Get-ProcessedFile
    if (-not (Test-Path -LiteralPath $file)) { return $false }

    return [bool] (Select-String -LiteralPath $file -Pattern "^$Commit\t" -Quiet)
}

function Add-Processed {
    param(
        [Parameter(Mandatory)]
        [string] $Commit,

        [Parameter(Mandatory)]
        [int] $MergeRequestIid,

        [Parameter(Mandatory)]
        [string] $Result
    )

    $line = "{0}`tMR !{1}`t{2}`t{3}" -f $Commit, $MergeRequestIid, $Result, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    Add-Content -LiteralPath (Get-ProcessedFile) -Value $line -Encoding UTF8
}

# ── Traitement d'une MR ──────────────────────────────────────────────────────

function Invoke-MergeRequestBuild {
    param(
        [Parameter(Mandatory)]
        $MergeRequest,

        [Parameter(Mandatory)]
        [string] $Token
    )

    $commit = $MergeRequest.sha
    Write-CiLog "MR !$($MergeRequest.iid) « $($MergeRequest.title) » ($($MergeRequest.source_branch)) : commit $commit"

    Save-CommitSources -Commit $commit -Token $Token
    $result = Invoke-CiBuild -Commit $commit

    if ($result.Success) {
        $state = 'success'
        Write-CiLog "MR !$($MergeRequest.iid) : $($result.Message)"
    }
    else {
        $state = 'failed'
        Write-CiLog "MR !$($MergeRequest.iid) : $($result.Message), voir $($result.LogFile)" 'ATTENTION'
    }

    Add-Processed -Commit $commit -MergeRequestIid $MergeRequest.iid -Result $state
}

# ── Programme principal ──────────────────────────────────────────────────────

$lock = $null

try {
    $script:Config = Read-CiConfiguration -Path $ConfigFile

    New-Item -ItemType Directory -Path $script:Config.RunnerRoot -Force | Out-Null
    Initialize-CiLog

    # Un seul passage à la fois : le verrou est libéré à la fin du processus.
    $lockFile = Join-Path $script:Config.RunnerRoot 'ci-dev.lock'
    try {
        $lock = [IO.File]::Open($lockFile, 'OpenOrCreate', 'ReadWrite', 'None')
    }
    catch {
        Write-CiLog 'Un passage est déjà en cours : rien à faire.'
        exit 0
    }

    # TLS 1.2 n'est pas actif par défaut dans Windows PowerShell 5.1.
    [Net.ServicePointManager]::SecurityProtocol = `
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $token = Get-GitLabToken

    $mergeRequests = Get-OpenMergeRequests -Token $token
    $toProcess = @(
        $mergeRequests |
            Where-Object { -not (Test-AlreadyProcessed -Commit $_.sha) } |
            Sort-Object iid
    )

    if ($toProcess.Count -eq 0) {
        Write-CiLog "Aucune MR à traiter ($($mergeRequests.Count) ouverte(s))."
        exit 0
    }

    Write-CiLog "$($toProcess.Count) MR à traiter sur $($mergeRequests.Count) ouverte(s)."

    foreach ($mergeRequest in $toProcess) {
        Invoke-MergeRequestBuild -MergeRequest $mergeRequest -Token $token
    }

    exit 0
}
catch {
    Write-CiLog $_.Exception.Message 'ERREUR'
    exit 1
}
finally {
    if ($lock) { $lock.Dispose() }
}
```
