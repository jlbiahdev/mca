# US 5 — Récupération des sources : code

Ticket : voir `epic.md`, US 5. Copier les fichiers dans `D:\ci\runner`, en **UTF-8 avec BOM**, en remplaçant ceux de l'étape précédente (reprendre `GitLabUrl` et `ProjectId`).

## ci-dev.psd1

```powershell
@{
    # ── GitLab ──────────────────────────────────────────────────────────────
    # Adresse de GitLab, sans / final.
    GitLabUrl  = 'https://XXXXX'

    # Identifiant numérique du projet (page d'accueil du projet, menu « ⋮ »).
    ProjectId  = 0

    # Jeton GitLab chiffré par protect-token.ps1 (DPAPI, niveau machine) ;
    # lecture limitée à SYSTEM, aux administrateurs et au compte du runner.
    TokenFile  = 'D:\ci\runner\gitlab-token.dat'

    # ── Runner ──────────────────────────────────────────────────────────────
    # Dossier du runner : script, configuration, journaux, commits traités.
    RunnerRoot = 'D:\ci\runner'

    # ── Sources ─────────────────────────────────────────────────────────────
    # Dossier où sont déposées les sources du commit. Il est VIDÉ à chaque
    # commit : ne rien y mettre d'autre.
    BuildRoot  = 'D:\ci\build'
}
```

## ci-dev.ps1

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Runner CI de dev (US 5) : récupère les sources du commit.

.DESCRIPTION
    Pour chaque nouveau commit de MR, le script télécharge le zip du commit
    généré par GitLab (git n'est pas nécessaire), le dépose dans BuildRoot,
    puis note le commit comme traité.

.PARAMETER ConfigFile
    Fichier de configuration. Par défaut : ci-dev.psd1, à côté du script.

.EXAMPLE
    .\ci-dev.ps1

.NOTES
    Code de sortie : 0 succès, 1 erreur.
#>
[CmdletBinding()]
param(
    [string] $ConfigFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# En Windows PowerShell 5.1, $PSScriptRoot est vide dans les valeurs par défaut
# du bloc param() : le chemin par défaut est donc calculé ici.
if ([string]::IsNullOrWhiteSpace($ConfigFile)) {
    $ConfigFile = Join-Path $PSScriptRoot 'ci-dev.psd1'
}

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
        'BuildRoot'
    )

    foreach ($key in $requiredKeys) {
        if (-not $config.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string] $config[$key])) {
            throw "Configuration incomplète : la clé '$key' est absente ou vide dans $Path"
        }
    }

    if ([int] $config.ProjectId -le 0) {
        throw "Configuration : ProjectId doit être l'identifiant numérique du projet GitLab."
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
        try {
            Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
        }
        catch {
            # Le message d'origine reste affiché dans la console.
            Write-Host "[ATTENTION] Journal impossible à écrire ($($script:LogFile)) : $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

function Initialize-CiLog {
    $logFolder = Join-Path $script:Config.RunnerRoot 'logs'
    New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
    $script:LogFile = Join-Path $logFolder ('ci-dev-{0}.log' -f (Get-Date -Format 'yyyyMMdd'))
}

# ── GitLab ───────────────────────────────────────────────────────────────────

# Le jeton est chiffré par protect-token.ps1 au niveau de la machine (DPAPI),
# ce qui permet à la tâche de le lire sans session ouverte ni mot de passe
# enregistré. Les droits du fichier limitent sa lecture au compte du runner.
function Get-GitLabToken {
    $path = $script:Config.TokenFile

    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Fichier du jeton GitLab introuvable : $path (le créer avec protect-token.ps1)"
    }

    try {
        $encoded = (Get-Content -LiteralPath $path -Raw -Encoding ASCII).Trim()
    }
    catch {
        throw "Lecture du jeton GitLab refusée ($path) : le compte qui lance ce script doit être autorisé par protect-token.ps1."
    }

    try {
        Add-Type -AssemblyName System.Security
        $entropy   = [Text.Encoding]::UTF8.GetBytes('Styx.CiDev')
        $encrypted = [Convert]::FromBase64String($encoded)
        $plain     = [Security.Cryptography.ProtectedData]::Unprotect(
            $encrypted, $entropy, [Security.Cryptography.DataProtectionScope]::LocalMachine)
    }
    catch {
        throw "Impossible de déchiffrer le jeton GitLab ($path) : le recréer avec protect-token.ps1 sur cette machine."
    }

    try {
        return [Text.Encoding]::UTF8.GetString($plain)
    }
    finally {
        [Array]::Clear($plain, 0, $plain.Length)
    }
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

# ── Commits traités ──────────────────────────────────────────────────────────
# Une ligne par commit : <commit> <tab> MR !<numéro> <tab> <date>

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
        [int] $MergeRequestIid
    )

    $line = "{0}`tMR !{1}`t{2}" -f $Commit, $MergeRequestIid, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
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

    Add-Processed -Commit $commit -MergeRequestIid $MergeRequest.iid
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
        # Seul un fichier déjà ouvert par un autre passage signifie « en cours » ;
        # un accès refusé, par exemple, est une vraie erreur.
        $lockError = $_.Exception
        if ($lockError.InnerException) { $lockError = $lockError.InnerException }

        if ($lockError -is [IO.IOException]) {
            Write-CiLog 'Un passage est déjà en cours : rien à faire.'
            exit 0
        }

        throw "Verrou impossible à créer ($lockFile) : $($lockError.Message)"
    }

    # TLS 1.2 n'est pas actif par défaut dans Windows PowerShell 5.1.
    [Net.ServicePointManager]::SecurityProtocol = `
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $token = Get-GitLabToken

    $mergeRequests = @(Get-OpenMergeRequests -Token $token)
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
