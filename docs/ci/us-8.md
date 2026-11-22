# US 8 — Build en échec et erreurs du runner : code

Ticket : voir `epic.md`, US 8. Copier les fichiers dans `D:\ci\runner`, en **UTF-8 avec BOM**, en remplaçant ceux de l'étape précédente (reprendre `GitLabUrl` et `ProjectId`).

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

    # ── Build ───────────────────────────────────────────────────────────────
    # Script de build, relatif à la racine des sources téléchargées.
    BuildScript         = 'src\scripts\devops\dev\build.ps1'
    BuildConfiguration  = 'Release'

    # Environnement transmis au script de build (devops\config\<env>.psd1).
    BuildEnvironment    = 'dev'

    # Au-delà, le build est arrêté et déclaré en échec.
    BuildTimeoutMinutes = 30

    # ── Statut GitLab ───────────────────────────────────────────────────────
    # Nom du statut affiché sur le commit et dans la MR.
    StatusName = 'ci-dev'
}
```

## ci-dev.ps1

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Runner CI de dev : à chaque passage, compile le dernier commit des merge
    requests ouvertes et publie le résultat dans GitLab.

.DESCRIPTION
    Le script est lancé chaque minute par la tâche planifiée Styx.CiDev et fait
    un seul passage :

    1. lit la configuration (ci-dev.psd1) et le jeton GitLab chiffré ;
    2. liste les merge requests ouvertes ;
    3. pour chaque MR dont le dernier commit n'a pas encore été traité :
       - publie le statut « en cours » sur le commit ;
       - télécharge les sources du commit (zip généré par GitLab, sans git) ;
       - lance le script de build des sources ;
       - publie le statut « réussi » ou « échoué » ;
       - note le commit comme traité.

    Une erreur du runner (GitLab injoignable, téléchargement impossible) ne
    marque pas le commit comme traité : il est repris au passage suivant.

.PARAMETER ConfigFile
    Fichier de configuration. Par défaut : ci-dev.psd1, à côté du script.

.EXAMPLE
    .\ci-dev.ps1

.NOTES
    Code de sortie : 0 succès (y compris build en échec, publié dans GitLab),
    1 erreur du runner.
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
        'BuildRoot', 'BuildScript', 'BuildConfiguration', 'BuildEnvironment',
        'BuildTimeoutMinutes', 'StatusName'
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

# Publie le statut du commit. GitLab rattache les statuts à un pipeline
# « externe » : on renvoie son identifiant pour y rattacher les statuts suivants.
function Set-CommitStatus {
    param(
        [Parameter(Mandatory)]
        [string] $Token,

        [Parameter(Mandatory)]
        $MergeRequest,

        [Parameter(Mandatory)]
        [ValidateSet('running', 'success', 'failed')]
        [string] $State,

        [Parameter(Mandatory)]
        [string] $Description,

        $PipelineId
    )

    # GitLab limite la description à 255 caractères.
    if ($Description.Length -gt 255) {
        $Description = $Description.Substring(0, 252) + '...'
    }

    $body = @{
        state       = $State
        name        = $script:Config.StatusName
        ref         = $MergeRequest.source_branch
        description = $Description
    }

    if ($PipelineId) {
        $body.pipeline_id = $PipelineId
    }

    $path = 'projects/{0}/statuses/{1}' -f $script:Config.ProjectId, $MergeRequest.sha
    $response = Invoke-GitLabApi -Path $path -Token $Token -Method Post -Body $body

    $pipeline = $null
    if ($response.PSObject.Properties['pipeline_id']) {
        $pipeline = $response.pipeline_id
    }

    # Selon la version de GitLab, la réponse peut ne pas contenir pipeline_id :
    # on le lit alors sur le commit.
    if (-not $pipeline) {
        $commitPath = 'projects/{0}/repository/commits/{1}' -f $script:Config.ProjectId, $MergeRequest.sha
        $commit = Invoke-GitLabApi -Path $commitPath -Token $Token

        if ($commit.PSObject.Properties['last_pipeline'] -and $commit.last_pipeline) {
            $pipeline = $commit.last_pipeline.id
        }
    }

    return $pipeline
}

# ── Sources ──────────────────────────────────────────────────────────────────
# Télécharge le zip du commit (généré par GitLab), puis écrit chaque fichier
# directement dans BuildRoot, après l'avoir vidé. Aucun besoin de git.
#
# GitLab range tout le contenu dans un dossier racine <projet>-<commit>-<commit>
# (près de 90 caractères) : il est retiré à l'extraction, sans dossier
# intermédiaire, pour garder les chemins sous la limite de 260 caractères de
# Windows PowerShell 5.1.

function Save-CommitSources {
    param(
        [Parameter(Mandatory)]
        [string] $Commit,

        [Parameter(Mandatory)]
        [string] $Token
    )

    $workFolder = Join-Path $script:Config.RunnerRoot 'work'
    $zipFile    = Join-Path $workFolder 'sources.zip'

    if (Test-Path -LiteralPath $workFolder) {
        Remove-Item -LiteralPath $workFolder -Recurse -Force
    }

    New-Item -ItemType Directory -Path $workFolder -Force | Out-Null

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

    $buildRoot = $script:Config.BuildRoot

    if (Test-Path -LiteralPath $buildRoot) {
        Get-ChildItem -LiteralPath $buildRoot -Force | Remove-Item -Recurse -Force
    }
    else {
        New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
    }

    $rootPrefix = [IO.Path]::GetFullPath($buildRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $fileCount  = 0

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipFile)

    try {
        foreach ($entry in $archive.Entries) {
            # Retire le dossier racine <projet>-<commit>-<commit>/.
            $separator = $entry.FullName.IndexOf('/')
            if ($separator -lt 0) { continue }

            $relative = $entry.FullName.Substring($separator + 1)
            if ([string]::IsNullOrEmpty($relative)) { continue }

            $target = [IO.Path]::GetFullPath((Join-Path $buildRoot ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))

            # Une entrée ne doit jamais écrire hors de BuildRoot.
            if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Chemin inattendu dans l'archive : $($entry.FullName)"
            }

            if ($relative.EndsWith('/')) {
                New-Item -ItemType Directory -Path $target -Force | Out-Null
                continue
            }

            try {
                $parent = Split-Path -Parent $target
                if (-not (Test-Path -LiteralPath $parent)) {
                    New-Item -ItemType Directory -Path $parent -Force | Out-Null
                }

                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
            catch {
                throw "Extraction impossible de $target ($($target.Length) caractères) : $($_.Exception.Message)"
            }

            $fileCount++
        }
    }
    finally {
        $archive.Dispose()
    }

    Remove-Item -LiteralPath $workFolder -Recurse -Force

    Write-CiLog "Sources déposées dans $buildRoot ($fileCount fichiers)"
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

    $pipelineId = Set-CommitStatus -Token $Token -MergeRequest $MergeRequest `
        -State running -Description "Build en cours sur $env:COMPUTERNAME"

    if ($pipelineId) {
        Write-CiLog "Pipeline externe GitLab : $pipelineId"
    }
    else {
        Write-CiLog 'GitLab n''a renvoyé aucun identifiant de pipeline externe.' 'ATTENTION'
    }

    try {
        Save-CommitSources -Commit $commit -Token $Token
        $result = Invoke-CiBuild -Commit $commit
    }
    catch {
        # Erreur du runner : le statut passe à « échoué », mais le commit n'est
        # pas noté comme traité, il sera repris au passage suivant.
        $null = Set-CommitStatus -Token $Token -MergeRequest $MergeRequest -PipelineId $pipelineId `
            -State failed -Description "Erreur du runner sur $env:COMPUTERNAME : $($_.Exception.Message)"
        throw
    }

    $description = '{0} en {1:mm\:ss} sur {2}. Journal : {3}' -f `
        $result.Message, $result.Duration, $env:COMPUTERNAME, $result.LogFile

    if ($result.Success) {
        $state = 'success'
        Write-CiLog "MR !$($MergeRequest.iid) : $($result.Message)"
    }
    else {
        $state = 'failed'
        Write-CiLog "MR !$($MergeRequest.iid) : $($result.Message), voir $($result.LogFile)" 'ATTENTION'
    }

    $null = Set-CommitStatus -Token $Token -MergeRequest $MergeRequest -PipelineId $pipelineId `
        -State $state -Description $description

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
