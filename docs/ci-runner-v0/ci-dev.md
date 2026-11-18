# ci-dev.ps1 — runner CI de dev, v0

Contenu du script, à copier dans `D:\ci\runner\ci-dev.ps1` (voir `README.md`, § 7).

```powershell
<#
.SYNOPSIS
    Runner CI de dev, v0 : à chaque lancement (toutes les minutes), cherche dans GitLab
    les merge requests ouvertes dont le dernier commit n'a pas encore été traité,
    et dépose les sources de ce commit dans le dossier de build.

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

# Le jeton est passé à git par variables d'environnement (et non en ligne de commande),
# pour qu'il n'apparaisse ni dans la liste des processus ni dans les journaux. Nécessite git 2.31 ou plus.
function Set-GitAuthentication {
    param([string]$Token)
    $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("oauth2:$Token"))
    $env:GIT_CONFIG_COUNT    = "1"
    $env:GIT_CONFIG_KEY_0    = "http.extraHeader"
    $env:GIT_CONFIG_VALUE_0  = "Authorization: Basic $basic"
    $env:GIT_TERMINAL_PROMPT = "0"
}

function Clear-GitAuthentication {
    Remove-Item Env:GIT_CONFIG_COUNT, Env:GIT_CONFIG_KEY_0, Env:GIT_CONFIG_VALUE_0 -ErrorAction SilentlyContinue
}

# ── Commandes git ────────────────────────────────────────────────────────────

# Lance git, recopie sa sortie dans le journal, échoue si le code de sortie n'est pas 0.
function Invoke-Git {
    param([string]$Description, [string[]]$Arguments)
    Write-Log $Description
    # Windows PowerShell 5.1 transforme en erreur tout message écrit sur la sortie d'erreur
    # (git y écrit sa progression) : on ne juge la commande que sur son code de sortie.
    $ErrorActionPreference = "Continue"
    & git @Arguments 2>&1 | ForEach-Object { Write-Log "    $_" }
    if ($LASTEXITCODE -ne 0) {
        throw "Échec ($Description), code de sortie $LASTEXITCODE."
    }
}

function Assert-GitVersion {
    $text = (git --version) -replace '[^\d\.]', ' '
    $version = [version](($text.Trim() -split '\s+')[0] -replace '^(\d+\.\d+).*$', '$1')
    if ($version -lt [version]"2.31") {
        throw "git 2.31 ou plus requis (version détectée : $version)."
    }
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

    Assert-GitVersion
    $project = Invoke-GitLabApi -Path "projects/$GitLabProjectId" -Token $token
    Set-GitAuthentication -Token $token
    $token = $null

    # 2. Pour chaque MR : sources du dernier commit déposées dans le dossier de build
    foreach ($mr in $toProcess) {
        Write-Log "MR !$($mr.iid) « $($mr.title) » ($($mr.source_branch)) : commit $($mr.sha)"

        if (-not (Test-Path (Join-Path $BuildFolder ".git"))) {
            Invoke-Git "Clonage du dépôt dans $BuildFolder" @("clone", "--no-checkout", $project.http_url_to_repo, $BuildFolder)
        }
        # GitLab expose le dernier commit de chaque MR sous refs/merge-requests/<numéro>/head.
        Invoke-Git "Récupération de la MR !$($mr.iid)" @("-C", $BuildFolder, "fetch", "origin", "refs/merge-requests/$($mr.iid)/head")
        Invoke-Git "Positionnement sur $($mr.sha)" @("-C", $BuildFolder, "checkout", "--force", "--detach", $mr.sha)
        Invoke-Git "Nettoyage du dossier de build" @("-C", $BuildFolder, "clean", "-fdx")

        Add-Processed -Commit $mr.sha -MergeRequestIid $mr.iid
        Write-Log "MR !$($mr.iid) : sources déposées dans $BuildFolder."
    }

    exit 0
}
catch {
    Write-Log $_.Exception.Message "ERREUR"
    exit 1
}
finally {
    Clear-GitAuthentication
    if ($lock) { $lock.Dispose() }
}
```
