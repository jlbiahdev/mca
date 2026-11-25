# US 11 — Scripts

Code des fichiers du tableau « Code » de `us-11.md`. Chaque fichier est à enregistrer en **UTF-8 avec BOM** (sinon Windows PowerShell 5.1 affiche mal les accents).

## cicd-int-poc.ps1

Runner, à mettre dans `D:\ci\runner` (machine du runner, § 1 du ticket).

````powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Runner CI/CD d'intégration : à chaque passage, compile le dernier commit
    des merge requests ouvertes (CI), puis package et publie sur Artifactory
    le dernier commit de main (CD).

.DESCRIPTION
    Le script est lancé chaque minute par la tâche planifiée Styx.CicdIntPoc et
    fait un seul passage :

    1. lit la configuration (cicd-int-poc.psd1) et le jeton GitLab chiffré ;
    2. CI : pour chaque MR ouverte dont le dernier commit n'a pas été traité :
       - publie le statut « en cours » (StatusName) sur le commit ;
       - télécharge les sources du commit (zip généré par GitLab, sans git) ;
       - lance le script de build des sources ;
       - dépose le journal du build dans GitLab ;
       - publie le statut « réussi » ou « échoué », avec l'auteur du commit et
         un lien vers le journal ;
       - en cas d'échec, publie dans la MR un commentaire avec les erreurs ;
       - note le commit comme traité (processed.txt) ;
    3. CD : si le dernier commit de CdBranch (main) n'a pas été publié :
       - publie le statut « en cours » (CdStatusName) et lit l'identifiant du
         pipeline externe créé par GitLab : il donne la version du package ;
       - télécharge les sources, lance le script de package avec cette
         version, puis le script de push avec la clé Artifactory lue dans une
         variable GitLab ;
       - dépose les journaux dans GitLab, publie « réussi » ou « échoué » ;
       - note le commit comme traité (processed-cd.txt).

    Une erreur du runner (GitLab injoignable, téléchargement impossible) ne
    marque pas le commit comme traité : il est repris au passage suivant.

.PARAMETER ConfigFile
    Fichier de configuration. Par défaut : cicd-int-poc.psd1, à côté du script.

.EXAMPLE
    .\cicd-int-poc.ps1

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
    $ConfigFile = Join-Path $PSScriptRoot 'cicd-int-poc.psd1'
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
        'BuildRoot', 'BuildScript', 'BuildEnvironment',
        'BuildTimeoutMinutes', 'StatusName',
        'CdBranch', 'CdStatusName', 'PackageScript', 'PushScript',
        'PackageRoot', 'VersionFormat', 'ArtifactoryKeyVariable',
        'BuildUsernameVariable', 'BuildTokenVariable'
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

    if (([string] $config.VersionFormat) -notmatch '\{0\}') {
        throw "Configuration : VersionFormat doit contenir {0}, remplacé par le numéro du pipeline."
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
    $script:LogFile = Join-Path $logFolder ('cicd-int-poc-{0}.log' -f (Get-Date -Format 'yyyyMMdd'))
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
        # Nom historique de l'entropie, conservé pour ne pas rechiffrer le jeton.
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

        [ValidateSet('Get', 'Post', 'Head')]
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
        [string] $Commit,

        [Parameter(Mandatory)]
        [string] $Ref,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [ValidateSet('running', 'success', 'failed')]
        [string] $State,

        [Parameter(Mandatory)]
        [string] $Description,

        $PipelineId,

        # Lien ouvert en cliquant sur le job dans GitLab (journal).
        [string] $TargetUrl
    )

    # GitLab limite la description à 255 caractères.
    if ($Description.Length -gt 255) {
        $Description = $Description.Substring(0, 252) + '...'
    }

    $body = @{
        state       = $State
        name        = $Name
        ref         = $Ref
        description = $Description
    }

    if ($PipelineId) {
        $body.pipeline_id = $PipelineId
    }

    if ($TargetUrl) {
        $body.target_url = $TargetUrl
    }

    $path = 'projects/{0}/statuses/{1}' -f $script:Config.ProjectId, $Commit
    $response = Invoke-GitLabApi -Path $path -Token $Token -Method Post -Body $body

    $pipeline = $null
    if ($response.PSObject.Properties['pipeline_id']) {
        $pipeline = $response.pipeline_id
    }

    # Selon la version de GitLab, la réponse peut ne pas contenir pipeline_id :
    # on le lit alors sur le commit.
    if (-not $pipeline) {
        $commitPath = 'projects/{0}/repository/commits/{1}' -f $script:Config.ProjectId, $Commit
        $commitInfo = Invoke-GitLabApi -Path $commitPath -Token $Token

        if ($commitInfo.PSObject.Properties['last_pipeline'] -and $commitInfo.last_pipeline) {
            $pipeline = $commitInfo.last_pipeline.id
        }
    }

    return $pipeline
}

# Auteur et titre du commit, pour la description du statut et le commentaire.
# En cas d'erreur, on continue sans ces informations.
function Get-CommitInfo {
    param(
        [Parameter(Mandatory)]
        [string] $Token,

        [Parameter(Mandatory)]
        [string] $Commit
    )

    try {
        $path = 'projects/{0}/repository/commits/{1}' -f $script:Config.ProjectId, $Commit
        return Invoke-GitLabApi -Path $path -Token $Token
    }
    catch {
        Write-CiLog "Informations du commit indisponibles : $($_.Exception.Message)" 'ATTENTION'
        return $null
    }
}

# Dépose un fichier dans le projet GitLab (API d'upload, envoi multipart).
# Invoke-RestMethod ne sait pas envoyer de formulaire multipart en Windows
# PowerShell 5.1 : on passe par HttpClient. Renvoie la réponse de GitLab
# (url, full_path, markdown).
function Publish-GitLabFile {
    param(
        [Parameter(Mandatory)]
        [string] $Token,

        [Parameter(Mandatory)]
        [string] $FileName,

        [Parameter(Mandatory)]
        [byte[]] $Content
    )

    Add-Type -AssemblyName System.Net.Http

    $uri = '{0}/api/v4/projects/{1}/uploads' -f $script:Config.GitLabUrl, $script:Config.ProjectId

    $client = [System.Net.Http.HttpClient]::new()
    try {
        $client.DefaultRequestHeaders.Add('PRIVATE-TOKEN', $Token)

        $file = [System.Net.Http.ByteArrayContent]::new($Content)
        $file.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')

        # Nom du champ et du fichier entre guillemets : HttpClient les omet, ce
        # que certains serveurs refusent.
        $disposition = [System.Net.Http.Headers.ContentDispositionHeaderValue]::new('form-data')
        $disposition.Name     = '"file"'
        $disposition.FileName = '"{0}"' -f $FileName
        $file.Headers.ContentDisposition = $disposition

        $form = [System.Net.Http.MultipartFormDataContent]::new()
        $form.Add($file)

        $response = $client.PostAsync($uri, $form).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        if (-not $response.IsSuccessStatusCode) {
            throw "GitLab refuse le dépôt du fichier ($([int] $response.StatusCode))."
        }

        return $text | ConvertFrom-Json
    }
    finally {
        $client.Dispose()
    }
}

# Dernier commit de la branche de publication (main).
function Get-BranchHead {
    param(
        [Parameter(Mandatory)]
        [string] $Token
    )

    $branch = [uri]::EscapeDataString($script:Config.CdBranch)
    $path = 'projects/{0}/repository/branches/{1}' -f $script:Config.ProjectId, $branch
    return (Invoke-GitLabApi -Path $path -Token $Token).commit
}

# Indique si un fichier existe dans un commit, sans le télécharger.
function Test-CommitFile {
    param(
        [Parameter(Mandatory)]
        [string] $Token,

        [Parameter(Mandatory)]
        [string] $Commit,

        # Chemin relatif à la racine des sources (\ ou /).
        [Parameter(Mandatory)]
        [string] $RelativePath
    )

    $filePath = [uri]::EscapeDataString($RelativePath.Replace('\', '/'))
    $path = 'projects/{0}/repository/files/{1}?ref={2}' -f $script:Config.ProjectId, $filePath, $Commit

    try {
        $null = Invoke-GitLabApi -Path $path -Token $Token -Method Head
        return $true
    }
    catch {
        if ($_.Exception.Message -like '*(404)*') { return $false }
        throw
    }
}

# Identifiants Artifactory du build (restauration des paquets NuGet), tirés de
# la clé de push « <compte>:<jeton> » : le compte de build sert aux deux.
# Renvoie la table des variables d'environnement à transmettre au build.
function Get-BuildCredentials {
    param(
        [Parameter(Mandatory)]
        [string] $Token
    )

    $key = Get-GitLabVariable -Token $Token -Key $script:Config.ArtifactoryKeyVariable
    $separator = $key.IndexOf(':')

    if ($separator -lt 1 -or $separator -eq $key.Length - 1) {
        throw "Variable GitLab '$($script:Config.ArtifactoryKeyVariable)' mal formée : attendu <compte>:<jeton>."
    }

    return @{
        $script:Config.BuildUsernameVariable = $key.Substring(0, $separator)
        $script:Config.BuildTokenVariable    = $key.Substring($separator + 1)
    }
}

# Valeur d'une variable de projet GitLab (clé Artifactory). La lecture demande
# le rôle Maintainer pour le jeton du runner.
function Get-GitLabVariable {
    param(
        [Parameter(Mandatory)]
        [string] $Token,

        [Parameter(Mandatory)]
        [string] $Key
    )

    $path = 'projects/{0}/variables/{1}' -f $script:Config.ProjectId, [uri]::EscapeDataString($Key)

    try {
        $variable = Invoke-GitLabApi -Path $path -Token $Token
    }
    catch {
        throw "Variable GitLab '$Key' illisible (elle doit exister dans Settings > CI/CD > Variables, et le jeton doit avoir le rôle Maintainer) : $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace([string] $variable.value)) {
        throw "Variable GitLab '$Key' vide."
    }

    return [string] $variable.value
}

# Publie un commentaire dans la MR.
function Add-MergeRequestNote {
    param(
        [Parameter(Mandatory)]
        [string] $Token,

        [Parameter(Mandatory)]
        $MergeRequest,

        [Parameter(Mandatory)]
        [string] $Text
    )

    $path = 'projects/{0}/merge_requests/{1}/notes' -f $script:Config.ProjectId, $MergeRequest.iid
    $null = Invoke-GitLabApi -Path $path -Token $Token -Method Post -Body @{ body = $Text }
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

# ── Scripts du dépôt ─────────────────────────────────────────────────────────
# Lance un script des sources téléchargées (build, package, push) : c'est la
# version du script qui accompagne le commit qui traite ce commit.

function Invoke-CiScript {
    param(
        [Parameter(Mandatory)]
        [string] $Commit,

        # Chemin du script, relatif à la racine des sources.
        [Parameter(Mandatory)]
        [string] $RelativePath,

        # Nom de l'étape, pour le journal et les messages (Build, Package…).
        [Parameter(Mandatory)]
        [string] $Step,

        [string[]] $Arguments = @(),

        # Variables d'environnement propres à ce script (identifiants) :
        # transmises au processus enfant, retirées du runner ensuite.
        [hashtable] $EnvironmentVariables = @{}
    )

    $buildRoot  = $script:Config.BuildRoot
    $scriptPath = Join-Path $buildRoot $RelativePath

    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "Script introuvable dans les sources ($Step) : $scriptPath"
    }

    $buildLogFolder = Join-Path $script:Config.RunnerRoot 'builds'
    New-Item -ItemType Directory -Path $buildLogFolder -Force | Out-Null

    $stamp      = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Commit.Substring(0, 8), $Step.ToLowerInvariant()
    $outputLog  = Join-Path $buildLogFolder "$stamp.log"
    $errorLog   = Join-Path $buildLogFolder "$stamp.err.log"

    $processArguments = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $scriptPath)
    )

    foreach ($argument in $Arguments) {
        if ($argument -match '\s' -or $argument -match '\\$') {
            $processArguments += '"{0}"' -f $argument.TrimEnd('\')
        }
        else {
            $processArguments += $argument
        }
    }

    Write-CiLog "$Step : $scriptPath"
    Write-CiLog "Journal ($Step) : $outputLog"

    $started = Get-Date

    # Le processus enfant hérite de l'environnement du runner : les variables
    # sont posées juste avant son lancement et retirées juste après.
    try {
        foreach ($name in $EnvironmentVariables.Keys) {
            [Environment]::SetEnvironmentVariable($name, [string] $EnvironmentVariables[$name], 'Process')
        }

        $process = Start-Process `
            -FilePath 'powershell.exe' `
            -ArgumentList $processArguments `
            -WorkingDirectory $buildRoot `
            -RedirectStandardOutput $outputLog `
            -RedirectStandardError $errorLog `
            -NoNewWindow `
            -PassThru
    }
    finally {
        foreach ($name in $EnvironmentVariables.Keys) {
            [Environment]::SetEnvironmentVariable($name, $null, 'Process')
        }
    }

    # Sans cet accès, ExitCode peut rester vide en Windows PowerShell 5.1.
    $null = $process.Handle

    $timeoutMilliseconds = [int] $script:Config.BuildTimeoutMinutes * 60 * 1000

    if (-not $process.WaitForExit($timeoutMilliseconds)) {
        # On arrête le script (et dotnet) : ce n'est pas un composant applicatif.
        & taskkill.exe /PID $process.Id /T /F | Out-Null

        return [pscustomobject]@{
            Step         = $Step
            Success      = $false
            Message      = "$Step interrompu après $($script:Config.BuildTimeoutMinutes) min"
            LogFile      = $outputLog
            ErrorLogFile = $errorLog
            Duration     = (Get-Date) - $started
        }
    }

    $process.WaitForExit()
    $exitCode = $process.ExitCode

    if ($exitCode -eq 0) {
        $message = "$Step réussi"
    }
    else {
        $message = "$Step échoué (code $exitCode)"
    }

    return [pscustomobject]@{
        Step         = $Step
        Success      = ($exitCode -eq 0)
        Message      = $message
        LogFile      = $outputLog
        ErrorLogFile = $errorLog
        Duration     = (Get-Date) - $started
    }
}

# ── Trace du build ───────────────────────────────────────────────────────────
# Journal complet déposé dans GitLab, et erreurs extraites pour la MR.

# Contenu du journal à déposer : sortie standard, puis sortie d'erreur si elle
# n'est pas vide.
function Get-BuildLogContent {
    param(
        [Parameter(Mandatory)]
        [object[]] $Results
    )

    $content = New-Object System.Collections.Generic.List[byte]

    foreach ($result in $Results) {
        if ($Results.Count -gt 1) {
            $content.AddRange([Text.Encoding]::UTF8.GetBytes("===== $($result.Step) : $($result.Message) =====`r`n"))
        }

        if (Test-Path -LiteralPath $result.LogFile) {
            $content.AddRange([IO.File]::ReadAllBytes($result.LogFile))
        }

        if ((Test-Path -LiteralPath $result.ErrorLogFile) -and (Get-Item -LiteralPath $result.ErrorLogFile).Length -gt 0) {
            $content.AddRange([Text.Encoding]::UTF8.GetBytes("`r`n===== Sortie d'erreur ($($result.Step)) =====`r`n"))
            $content.AddRange([IO.File]::ReadAllBytes($result.ErrorLogFile))
        }

        $content.AddRange([Text.Encoding]::UTF8.GetBytes("`r`n"))
    }

    return , $content.ToArray()
}

# Dépose le journal dans GitLab et renvoie son adresse complète et son lien
# Markdown. En cas d'échec, le résultat reste valable : on continue sans lien.
function Publish-BuildLog {
    param(
        [Parameter(Mandatory)]
        [string] $Token,

        # Adresse web du projet (repli si GitLab ne renvoie pas full_path).
        [Parameter(Mandatory)]
        [string] $ProjectUrl,

        [Parameter(Mandatory)]
        [object[]] $Results
    )

    try {
        $fileName = Split-Path -Leaf $Results[-1].LogFile

        $upload = Publish-GitLabFile -Token $Token -FileName $fileName `
            -Content (Get-BuildLogContent -Results $Results)

        # Selon la version de GitLab, l'adresse est relative à l'instance
        # (full_path) ou au projet (url).
        if ($upload.PSObject.Properties['full_path'] -and $upload.full_path) {
            $url = $script:Config.GitLabUrl + $upload.full_path
        }
        else {
            $url = $ProjectUrl + $upload.url
        }

        Write-CiLog "Journal déposé dans GitLab : $url"

        return [pscustomobject]@{
            Url      = $url
            Markdown = $upload.markdown
        }
    }
    catch {
        Write-CiLog "Journal non déposé dans GitLab : $($_.Exception.Message)" 'ATTENTION'
        return $null
    }
}

# Lignes d'erreur du compilateur (« fichier(ligne,col): error CS0246: ... »),
# sans le chemin du dossier de build ni le projet entre crochets, sans doublon.
# Sans erreur reconnue (délai dépassé, échec du script), on renvoie la fin des
# journaux.
function Get-BuildErrors {
    param(
        [Parameter(Mandatory)]
        $Result,

        [int] $TailLines = 20
    )


    # Chemins affichés relatifs au dossier de build.
    $root = $script:Config.BuildRoot + [IO.Path]::DirectorySeparatorChar

    $lines = @(
        foreach ($file in @($Result.LogFile, $Result.ErrorLogFile)) {
            if (Test-Path -LiteralPath $file) {
                Get-Content -LiteralPath $file -Encoding UTF8
            }
        }
    )

    $errors = @(
        $lines |
            Where-Object { $_ -match ':\s*error\s+[A-Z]+\d+\s*:' } |
            ForEach-Object { ($_.Trim() -replace '\s*\[[^\]]+\]$', '').Replace($root, '') } |
            Select-Object -Unique
    )

    if ($errors.Count -gt 0) {
        return [pscustomobject]@{ Found = $true; Lines = $errors }
    }

    $tail = @($lines | Where-Object { $_.Trim() } | Select-Object -Last $TailLines)
    return [pscustomobject]@{ Found = $false; Lines = $tail }
}

# Commentaire publié dans la MR en cas d'échec.
function New-FailureNote {
    param(
        [Parameter(Mandatory)]
        $MergeRequest,

        [Parameter(Mandatory)]
        $Result,

        [Parameter(Mandatory)]
        $BuildErrors,

        $CommitInfo,

        $PipelineId,

        $LogLink,

        [int] $MaxLines = 50
    )

    $commit = $MergeRequest.sha.Substring(0, 8)
    if ($CommitInfo) {
        $commitText = "``$commit`` « $($CommitInfo.title) » de $($CommitInfo.author_name)"
    }
    else {
        $commitText = "``$commit``"
    }

    $note = [Text.StringBuilder]::new()
    $null = $note.AppendLine("**$($script:Config.StatusName) : $($Result.Message)** pour le commit $commitText.")
    $null = $note.AppendLine()
    $null = $note.AppendLine("- MR de $($MergeRequest.author.name)")
    $null = $note.AppendLine(('- Durée : {0:mm\:ss}, sur {1} (environnement {2})' -f $Result.Duration, $env:COMPUTERNAME, $script:Config.BuildEnvironment))

    if ($PipelineId) {
        $null = $note.AppendLine("- Pipeline externe : #$PipelineId")
    }

    if ($LogLink) {
        $null = $note.AppendLine("- Journal complet : $($LogLink.Markdown)")
    }
    else {
        $null = $note.AppendLine("- Journal complet : non déposé dans GitLab, voir ``$($Result.LogFile)`` sur $env:COMPUTERNAME")
    }

    $null = $note.AppendLine()

    if ($BuildErrors.Found) {
        $null = $note.AppendLine("Erreurs ($($BuildErrors.Lines.Count)) :")
    }
    else {
        $null = $note.AppendLine('Aucune erreur de compilation reconnue. Dernières lignes du journal :')
    }

    $null = $note.AppendLine()
    $null = $note.AppendLine('```text')
    foreach ($line in ($BuildErrors.Lines | Select-Object -First $MaxLines)) {
        $null = $note.AppendLine($line)
    }
    $null = $note.AppendLine('```')

    if ($BuildErrors.Lines.Count -gt $MaxLines) {
        $null = $note.AppendLine()
        $null = $note.AppendLine("… et $($BuildErrors.Lines.Count - $MaxLines) autre(s) : voir le journal complet.")
    }

    return $note.ToString()
}

# ── Commits traités ──────────────────────────────────────────────────────────
# Une ligne par commit : <commit> <tab> <origine> <tab> <résultat> <tab> <date>

function Get-ProcessedFile {
    param(
        # processed.txt pour la CI, processed-cd.txt pour la CD.
        [string] $Name = 'processed.txt'
    )

    return Join-Path $script:Config.RunnerRoot $Name
}

function Test-AlreadyProcessed {
    param(
        [Parameter(Mandatory)]
        [string] $Commit,

        [string] $Name = 'processed.txt'
    )

    $file = Get-ProcessedFile -Name $Name
    if (-not (Test-Path -LiteralPath $file)) { return $false }

    return [bool] (Select-String -LiteralPath $file -Pattern "^$Commit\t" -Quiet)
}

function Add-Processed {
    param(
        [Parameter(Mandatory)]
        [string] $Commit,

        # « MR !<numéro> » pour la CI, « main 1.0.<pipeline> » pour la CD.
        [Parameter(Mandatory)]
        [string] $Origin,

        [Parameter(Mandatory)]
        [string] $Result,

        [string] $Name = 'processed.txt'
    )

    $line = "{0}`t{1}`t{2}`t{3}" -f $Commit, $Origin, $Result, (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
    Add-Content -LiteralPath (Get-ProcessedFile -Name $Name) -Value $line -Encoding UTF8
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

    $commitInfo = Get-CommitInfo -Token $Token -Commit $commit

    $statusTarget = @{
        Token  = $Token
        Commit = $commit
        Ref    = $MergeRequest.source_branch
        Name   = $script:Config.StatusName
    }

    $pipelineId = Set-CommitStatus @statusTarget -State running -Description "Build en cours sur $env:COMPUTERNAME"

    if ($pipelineId) {
        Write-CiLog "Pipeline externe GitLab : $pipelineId"
    }
    else {
        Write-CiLog 'GitLab n''a renvoyé aucun identifiant de pipeline externe.' 'ATTENTION'
    }

    try {
        # Identifiants lus avant le téléchargement : s'ils sont inaccessibles,
        # on s'arrête sans télécharger à chaque passage.
        $buildCredentials = Get-BuildCredentials -Token $Token

        Save-CommitSources -Commit $commit -Token $Token
        $result = Invoke-CiScript -Commit $commit -RelativePath $script:Config.BuildScript -Step 'Build' `
            -Arguments @('-RepositoryRoot', $script:Config.BuildRoot, '-Environment', $script:Config.BuildEnvironment) `
            -EnvironmentVariables $buildCredentials
    }
    catch {
        # Erreur du runner : le statut passe à « échoué », mais le commit n'est
        # pas noté comme traité, il sera repris au passage suivant.
        $null = Set-CommitStatus @statusTarget -PipelineId $pipelineId `
            -State failed -Description "Erreur du runner sur $env:COMPUTERNAME : $($_.Exception.Message)"
        throw
    }

    $projectUrl = $MergeRequest.web_url -replace '/-/merge_requests/\d+$', ''
    $logLink = Publish-BuildLog -Token $Token -ProjectUrl $projectUrl -Results @($result)

    if ($commitInfo) {
        $author = "Commit de $($commitInfo.author_name)"
    }
    else {
        $author = "MR de $($MergeRequest.author.name)"
    }

    $where = '{0:mm\:ss} sur {1}' -f $result.Duration, $env:COMPUTERNAME

    if ($result.Success) {
        $state = 'success'
        $description = "$($result.Message) en $where. $author."
        Write-CiLog "MR !$($MergeRequest.iid) : $($result.Message)"
    }
    else {
        $state = 'failed'
        Write-CiLog "MR !$($MergeRequest.iid) : $($result.Message), voir $($result.LogFile)" 'ATTENTION'

        # La première erreur vient en dernier : GitLab limite la description à
        # 255 caractères, c'est donc elle qui est tronquée si besoin.
        $buildErrors = Get-BuildErrors -Result $result
        if ($buildErrors.Found) {
            $description = "$($result.Message), $($buildErrors.Lines.Count) erreur(s), en $where. $author. 1re erreur : $($buildErrors.Lines[0])"
        }
        else {
            $description = "$($result.Message) en $where. $author."
        }

        try {
            $note = New-FailureNote -MergeRequest $MergeRequest -Result $result -BuildErrors $buildErrors `
                -CommitInfo $commitInfo -PipelineId $pipelineId -LogLink $logLink
            Add-MergeRequestNote -Token $Token -MergeRequest $MergeRequest -Text $note
            Write-CiLog "MR !$($MergeRequest.iid) : commentaire d'échec publié."
        }
        catch {
            Write-CiLog "Commentaire d'échec non publié : $($_.Exception.Message)" 'ATTENTION'
        }
    }

    $linkUrl = $null
    if ($logLink) { $linkUrl = $logLink.Url }

    $null = Set-CommitStatus @statusTarget -PipelineId $pipelineId `
        -State $state -Description $description -TargetUrl $linkUrl

    Add-Processed -Commit $commit -Origin "MR !$($MergeRequest.iid)" -Result $state
}

# ── Publication de main ──────────────────────────────────────────────────────

function Invoke-ReleaseBuild {
    param(
        [Parameter(Mandatory)]
        [string] $Token
    )

    $head = Get-BranchHead -Token $Token
    $commit = $head.id
    $branch = $script:Config.CdBranch

    if (Test-AlreadyProcessed -Commit $commit -Name 'processed-cd.txt') {
        return $false
    }

    # Un commit de main antérieur aux scripts de publication ne peut pas être
    # publié : on le note comme ignoré, sans statut ni téléchargement.
    $missing = @(
        foreach ($relativePath in @($script:Config.PackageScript, $script:Config.PushScript)) {
            if (-not (Test-CommitFile -Token $Token -Commit $commit -RelativePath $relativePath)) {
                $relativePath
            }
        }
    )

    if ($missing.Count -gt 0) {
        Write-CiLog "Branche $branch : commit $commit ignoré, scripts absents du commit : $($missing -join ', ')" 'ATTENTION'
        Add-Processed -Commit $commit -Origin $branch -Result 'ignored' -Name 'processed-cd.txt'
        return $true
    }

    Write-CiLog "Branche $branch : commit $commit à publier"

    $commitInfo = Get-CommitInfo -Token $Token -Commit $commit

    $statusTarget = @{
        Token  = $Token
        Commit = $commit
        Ref    = $branch
        Name   = $script:Config.CdStatusName
    }

    $pipelineId = Set-CommitStatus @statusTarget -State running -Description "Package en cours sur $env:COMPUTERNAME"

    $results = @()

    try {
        # La version vient du pipeline externe : sans lui, pas de version.
        if (-not $pipelineId) {
            throw 'GitLab n''a renvoyé aucun identifiant de pipeline externe : version impossible.'
        }

        $version = $script:Config.VersionFormat -f $pipelineId
        Write-CiLog "Pipeline externe GitLab : $pipelineId, version $version"

        # Clé lue avant le téléchargement : si elle est inaccessible, on
        # s'arrête sans télécharger ni packager à chaque passage.
        $keyVariable = $script:Config.ArtifactoryKeyVariable
        $key = Get-GitLabVariable -Token $Token -Key $keyVariable
        $buildCredentials = Get-BuildCredentials -Token $Token

        Save-CommitSources -Commit $commit -Token $Token

        $outputDirectory = Join-Path $script:Config.PackageRoot $version
        if (Test-Path -LiteralPath $outputDirectory) {
            Remove-Item -LiteralPath $outputDirectory -Recurse -Force
        }

        $package = Invoke-CiScript -Commit $commit -RelativePath $script:Config.PackageScript -Step 'Package' `
            -Arguments @('-RepositoryRoot', $script:Config.BuildRoot, '-PackageVersion', $version, '-OutputDirectory', $outputDirectory) `
            -EnvironmentVariables $buildCredentials
        $results += $package

        if ($package.Success) {
            $packageFile = @(Get-ChildItem -LiteralPath $outputDirectory -Filter '*.nupkg' -File) | Select-Object -First 1
            if (-not $packageFile) {
                throw "Package introuvable après sa génération dans $outputDirectory"
            }

            Write-CiLog "Package : $($packageFile.FullName)"

            # La clé est transmise au script de push par l'environnement du
            # processus, jamais par la ligne de commande ni par un fichier.
            $push = Invoke-CiScript -Commit $commit -RelativePath $script:Config.PushScript -Step 'Push' `
                -Arguments @('-PackageFile', $packageFile.FullName, '-RepositoryRoot', $script:Config.BuildRoot, '-Environment', $script:Config.BuildEnvironment) `
                -EnvironmentVariables @{ $keyVariable = $key }
            $key = $null

            $results += $push
        }
    }
    catch {
        # Erreur du runner : statut « échoué », commit non noté, repris au
        # passage suivant.
        $null = Set-CommitStatus @statusTarget -PipelineId $pipelineId `
            -State failed -Description "Erreur du runner sur $env:COMPUTERNAME : $($_.Exception.Message)"
        throw
    }

    $final = $results[-1]
    $duration = [TimeSpan]::Zero
    foreach ($result in $results) { $duration += $result.Duration }

    $projectUrl = $head.web_url -replace '/-/commit/[0-9a-f]+$', ''
    $logLink = Publish-BuildLog -Token $Token -ProjectUrl $projectUrl -Results $results

    if ($commitInfo) {
        $author = "Commit de $($commitInfo.author_name)"
    }
    else {
        $author = 'Auteur inconnu'
    }

    $where = '{0:mm\:ss} sur {1}' -f $duration, $env:COMPUTERNAME

    if ($final.Success) {
        $state = 'success'
        $description = "Version $version publiée sur Artifactory en $where. $author."
        Write-CiLog "Branche $branch : version $version publiée sur Artifactory"
    }
    else {
        $state = 'failed'
        $description = "$($final.Message), version $version, en $where. $author."
        Write-CiLog "Branche $branch : $($final.Message), voir $($final.LogFile)" 'ATTENTION'
    }

    $linkUrl = $null
    if ($logLink) { $linkUrl = $logLink.Url }

    $null = Set-CommitStatus @statusTarget -PipelineId $pipelineId `
        -State $state -Description $description -TargetUrl $linkUrl

    Add-Processed -Commit $commit -Origin "$branch $version" -Result $state -Name 'processed-cd.txt'
    return $true
}

# ── Programme principal ──────────────────────────────────────────────────────

$lock = $null

try {
    $script:Config = Read-CiConfiguration -Path $ConfigFile

    New-Item -ItemType Directory -Path $script:Config.RunnerRoot -Force | Out-Null
    Initialize-CiLog

    # Un seul passage à la fois : le verrou est libéré à la fin du processus.
    $lockFile = Join-Path $script:Config.RunnerRoot 'cicd-int-poc.lock'
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

    $exitCode = 0

    # CI et CD sont indépendantes : une erreur de l'une n'empêche pas l'autre.
    try {
        $mergeRequests = @(Get-OpenMergeRequests -Token $token)
        $toProcess = @(
            $mergeRequests |
                Where-Object { -not (Test-AlreadyProcessed -Commit $_.sha) } |
                Sort-Object iid
        )

        if ($toProcess.Count -eq 0) {
            $ciSummary = "Aucune MR à traiter ($($mergeRequests.Count) ouverte(s))"
        }
        else {
            $ciSummary = $null
            Write-CiLog "$($toProcess.Count) MR à traiter sur $($mergeRequests.Count) ouverte(s)."

            foreach ($mergeRequest in $toProcess) {
                Invoke-MergeRequestBuild -MergeRequest $mergeRequest -Token $token
            }
        }
    }
    catch {
        Write-CiLog "CI : $($_.Exception.Message)" 'ERREUR'
        $ciSummary = $null
        $exitCode = 1
    }

    try {
        $published = Invoke-ReleaseBuild -Token $token
    }
    catch {
        Write-CiLog "CD : $($_.Exception.Message)" 'ERREUR'
        $published = $true
        $exitCode = 1
    }

    # Passage sans rien à faire : une seule ligne dans le journal.
    if ($ciSummary -and -not $published) {
        Write-CiLog "$ciSummary, $($script:Config.CdBranch) à jour."
    }

    exit $exitCode
}
catch {
    Write-CiLog $_.Exception.Message 'ERREUR'
    exit 1
}
finally {
    if ($lock) { $lock.Dispose() }
}
````

## cicd-int-poc.psd1

Configuration du runner, à mettre dans `D:\ci\runner`. Si le fichier installé a déjà été adapté, ajouter seulement les deux clés de la fin (`BuildUsernameVariable`, `BuildTokenVariable`).

````powershell
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

    # ── Build (CI, merge requests) ──────────────────────────────────────────
    # Script de build, relatif à la racine des sources téléchargées.
    BuildScript         = 'src\scripts\devops\int\build.ps1'

    # Environnement transmis aux scripts du dépôt
    # (src\scripts\devops\config\<env>.psd1).
    BuildEnvironment    = 'int'

    # Au-delà, le script (build, package ou push) est arrêté et déclaré en échec.
    BuildTimeoutMinutes = 30

    # Nom du statut affiché sur les commits des MR.
    StatusName = 'ci-int'

    # ── Publication (CD, branche main) ──────────────────────────────────────
    # Branche dont chaque nouveau commit est packagé et publié.
    CdBranch     = 'main'

    # Nom du statut affiché sur les commits de main.
    CdStatusName = 'cd-int'

    # Scripts de package et de push, relatifs à la racine des sources.
    PackageScript = 'src\scripts\devops\int\package.ps1'
    PushScript    = 'src\scripts\devops\int\push-artifactory.ps1'

    # Dossier des packages générés (un sous-dossier par version).
    PackageRoot   = 'D:\ci\packages'

    # Version du package : {0} est remplacé par le numéro du pipeline externe
    # GitLab (unique et croissant).
    VersionFormat = '1.0.{0}'

    # Variable de projet GitLab qui contient la clé Artifactory. Même nom que
    # Artifactory.ApiKeyVariable dans config\int.psd1 : le runner transmet la
    # clé au script de push sous ce nom.
    ArtifactoryKeyVariable = 'ARTIFACTORY_API_KEY'

    # Variables d'environnement transmises au build et au package pour la
    # restauration des paquets NuGet depuis Artifactory. Leurs valeurs sont
    # tirées de ArtifactoryKeyVariable (<compte>:<jeton>) ; les noms doivent
    # être ceux utilisés dans src\backend\nuget.config.
    BuildUsernameVariable = 'ARTIFACTORY_BUILD_USERNAME'
    BuildTokenVariable    = 'ARTIFACTORY_BUILD_TOKEN'
}
````

## build.ps1

À mettre dans le dépôt : `src/scripts/devops/int/build.ps1`.

````powershell
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
````

## package.ps1

À mettre dans le dépôt : `src/scripts/devops/int/package.ps1`.

````powershell
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

    # Paquets NuGet restaurés dans un dossier propre au build (vidé avec les
    # sources à chaque commit), et non dans le cache de la machine : chaque
    # build vérifie que les sources de nuget.config suffisent.
    $env:NUGET_PACKAGES       = Join-Path $RepositoryRoot '.nuget\packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $RepositoryRoot '.nuget\http-cache'

    Write-Host "Paquets NuGet : $env:NUGET_PACKAGES"

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
````

## push-artifactory.ps1

À mettre dans le dépôt : `src/scripts/devops/int/push-artifactory.ps1`.

````powershell
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
        # Piste adaptée au code HTTP renvoyé par Artifactory, s'il est connu.
        $message = $_.Exception.Message

        if ($message -match '\b401\b') {
            $hint = "401 : compte ou jeton refusés. Vérifier que la clé a la forme <compte>:<jeton>, avec le compte de build (usr-…) et son jeton."
        }
        elseif ($message -match '\b403\b') {
            $hint = "403 : le compte n'a pas le droit de déposer dans ce dépôt. Vérifier qu'il s'agit du compte de build (usr-…), et non du compte de déploiement (usr-cd-…)."
        }
        elseif ($message -match '\b409\b') {
            $hint = "409 : cette version existe déjà sur Artifactory. Chaque version correspond à un pipeline GitLab : un nouveau commit sur main produira une nouvelle version."
        }
        else {
            $hint = "Voir la sortie de dotnet nuget push ci-dessus."
        }

        throw ($message + [Environment]::NewLine + $hint)
    }

    Write-StyxSuccess "Package poussé avec succès : $($package.Name)"
    exit 0
}
catch {
    Write-StyxFailure $_.Exception.Message
    exit 1
}
````
