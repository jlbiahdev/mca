# ci-dev.ps1 — runner CI de dev, v0

Contenu du script, à copier dans `D:\ci\runner\ci-dev.ps1` (voir `README.md`, § 7).

```powershell
<#
.SYNOPSIS
    Runner CI de dev, v0 : à chaque lancement (toutes les minutes), cherche dans GitLab
    les merge requests ouvertes dont le dernier commit n'a pas encore été traité,
    et dépose les sources de ce commit dans le dossier de build
    (archive zip générée par GitLab : git n'est pas nécessaire).

    v0 : récupération des sources uniquement. Le build et la mise à jour du statut
    dans GitLab viendront dans les versions suivantes.

.EXAMPLE
    .\ci-dev.ps1
#>
[CmdletBinding()]
param()

# >>> DEBUT CONFIGURATION ─────────────────────────────────────────────────────

$GitLabUrl       = ""        # à renseigner : ex. https://gitlab.exemple.fr (sans / final)
$GitLabProjectId = 0         # à renseigner : identifiant numérique du projet

$RunnerRoot      = "D:\ci\runner"                    # script, jeton, état, journaux
$BuildFolder     = "D:\ci\build"                     # sources déposées pour le build
$GitLabTokenFile = "D:\ci\runner\gitlab-token.xml"   # jeton chiffré (voir prérequis)

# <<< FIN CONFIGURATION ───────────────────────────────────────────────────────

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"   # la barre de progression ralentit fortement les téléchargements en PowerShell 5.1
$script:LogFile = $null

$StateFile = Join-Path $RunnerRoot "processed.txt"   # fichier « j'ai travaillé »
$LogFolder = Join-Path $RunnerRoot "logs"
$LockFile  = Join-Path $RunnerRoot "ci-dev.lock"

# ── Journal ──────────────────────────────────────────────────────────────────

# Le journal n'est créé qu'au premier message : un lancement sans travail (le cas
# le plus fréquent, toutes les minutes) ne laisse aucun fichier.
function Write-Log {
    param(
        [string]$Message,
        [ValidateSet("INFO", "ATTENTION", "ERREUR")]
        [string]$Level = "INFO"
    )
    if (-not $script:LogFile) {
        New-Item -ItemType Directory -Path $LogFolder -Force | Out-Null
        $script:LogFile = Join-Path $LogFolder ("ci-dev-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    }
    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    $color = switch ($Level) { "ATTENTION" { "Yellow" } "ERREUR" { "Red" } default { "Gray" } }
    Write-Host $line -ForegroundColor $color
    Add-Content -Path $script:LogFile -Value $line -Encoding UTF8
}

# ── Jeton GitLab ─────────────────────────────────────────────────────────────

function Get-GitLabToken {
    if (-not (Test-Path $GitLabTokenFile)) {
        throw "Fichier du jeton GitLab introuvable : $GitLabTokenFile (voir les prérequis)."
    }
    try {
        $secureToken = Import-Clixml -Path $GitLabTokenFile
    }
    catch {
        throw "Impossible de déchiffrer le jeton GitLab. Le fichier doit avoir été créé par le compte qui lance ce script, sur cette machine."
    }
    return [System.Net.NetworkCredential]::new("", $secureToken).Password
}

function Invoke-GitLabApi {
    param([string]$Path, [string]$Token)
    $uri = "$GitLabUrl/api/v4/$Path"
    try {
        return Invoke-RestMethod -Uri $uri -Method Get -Headers @{ "PRIVATE-TOKEN" = $Token }
    }
    catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        switch ($status) {
            401     { throw "GitLab refuse le jeton (401) : jeton invalide ou expiré." }
            403     { throw "GitLab refuse l'accès (403) : vérifier le rôle et les scopes du jeton." }
            404     { throw "Projet $GitLabProjectId introuvable (404)." }
            default { throw "Appel GitLab impossible ($Path) : $($_.Exception.Message)" }
        }
    }
}

# Télécharge les sources d'un commit en zip (archive générée par GitLab) et les dépose
# dans le dossier de build, après l'avoir vidé. Aucun besoin de git sur la machine.
function Save-CommitSources {
    param([string]$Commit, [string]$Token)

    $workFolder = Join-Path $RunnerRoot "work"
    $zipFile    = Join-Path $workFolder "sources.zip"
    $extractDir = Join-Path $workFolder "extract"
    if (Test-Path $workFolder) { Remove-Item $workFolder -Recurse -Force }
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

    Write-Log "Téléchargement des sources du commit $Commit"
    $uri = "$GitLabUrl/api/v4/projects/$GitLabProjectId/repository/archive.zip?sha=$Commit"
    try {
        Invoke-WebRequest -Uri $uri -Headers @{ "PRIVATE-TOKEN" = $Token } -OutFile $zipFile -UseBasicParsing
    }
    catch {
        throw "Téléchargement des sources impossible : $($_.Exception.Message)"
    }
    Write-Log ("Archive reçue : {0:N1} Mo" -f ((Get-Item $zipFile).Length / 1MB))

    Expand-Archive -Path $zipFile -DestinationPath $extractDir -Force
    # GitLab range tout le contenu dans un dossier racine <projet>-<commit>-<commit>.
    $rootFolders = @(Get-ChildItem -Path $extractDir -Directory)
    if ($rootFolders.Count -ne 1) {
        throw "Archive inattendue : un seul dossier racine attendu, $($rootFolders.Count) trouvé(s)."
    }

    Write-Log "Remplacement du contenu de $BuildFolder"
    if (Test-Path $BuildFolder) {
        Get-ChildItem -Path $BuildFolder -Force | Remove-Item -Recurse -Force
    }
    else {
        New-Item -ItemType Directory -Path $BuildFolder -Force | Out-Null
    }
    Get-ChildItem -Path $rootFolders[0].FullName -Force | Move-Item -Destination $BuildFolder

    Remove-Item $workFolder -Recurse -Force
}

# ── Fichier « j'ai travaillé » ───────────────────────────────────────────────
# Une ligne par commit traité : <commit> <tab> MR !<numéro> <tab> <date>

function Test-AlreadyProcessed {
    param([string]$Commit)
    if (-not (Test-Path $StateFile)) { return $false }
    return [bool](Select-String -Path $StateFile -Pattern "^$Commit\t" -Quiet)
}

function Add-Processed {
    param([string]$Commit, [int]$MergeRequestIid)
    $line = "{0}`tMR !{1}`t{2}" -f $Commit, $MergeRequestIid, (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    Add-Content -Path $StateFile -Value $line -Encoding ASCII
}

# ── Programme principal ──────────────────────────────────────────────────────

$lock = $null
try {
    if ([string]::IsNullOrWhiteSpace($GitLabUrl) -or $GitLabProjectId -le 0) {
        throw "Configuration incomplète : renseigner `$GitLabUrl et `$GitLabProjectId en tête du script."
    }
    # Le dossier de build est entièrement vidé à chaque MR : on refuse une racine de lecteur.
    if ([System.IO.Path]::GetPathRoot($BuildFolder) -eq $BuildFolder.TrimEnd('\') + '\') {
        throw "Le dossier de build ne peut pas être la racine d'un lecteur : $BuildFolder"
    }
    New-Item -ItemType Directory -Path $RunnerRoot -Force | Out-Null

    # Une seule exécution à la fois : le verrou est libéré automatiquement à la fin du processus.
    try {
        $lock = [System.IO.File]::Open($LockFile, "OpenOrCreate", "ReadWrite", "None")
    }
    catch {
        Write-Host "Une exécution est déjà en cours. Rien à faire."
        exit 0
    }

    # TLS 1.2 n'est pas actif par défaut dans Windows PowerShell 5.1.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $token = Get-GitLabToken

    # 1. MR ouvertes dont le dernier commit n'a pas encore été traité
    $mergeRequests = @(Invoke-GitLabApi -Path "projects/$GitLabProjectId/merge_requests?state=opened&per_page=100" -Token $token)
    $toProcess = @($mergeRequests | Where-Object { -not (Test-AlreadyProcessed -Commit $_.sha) } | Sort-Object iid)

    if ($toProcess.Count -eq 0) {
        Write-Host "Aucune MR à traiter ($($mergeRequests.Count) ouverte(s))."
        exit 0
    }

    Write-Log "Runner CI sur $env:COMPUTERNAME, lancé par $env:USERDOMAIN\$env:USERNAME"
    Write-Log "$($toProcess.Count) MR à traiter sur $($mergeRequests.Count) ouverte(s)."

    # 2. Pour chaque MR : sources du dernier commit déposées dans le dossier de build
    foreach ($mr in $toProcess) {
        Write-Log "MR !$($mr.iid) « $($mr.title) » ($($mr.source_branch)) : commit $($mr.sha)"
        Save-CommitSources -Commit $mr.sha -Token $token
        Add-Processed -Commit $mr.sha -MergeRequestIid $mr.iid
        Write-Log "MR !$($mr.iid) : sources déposées dans $BuildFolder."
    }
    $token = $null

    exit 0
}
catch {
    Write-Log $_.Exception.Message "ERREUR"
    exit 1
}
finally {
    if ($lock) { $lock.Dispose() }
}
```
