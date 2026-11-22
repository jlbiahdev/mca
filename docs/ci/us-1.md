# US 1 — Connexion à GitLab : code

Ticket : voir `epic.md`, US 1. Copier les fichiers dans `D:\ci\runner`, en **UTF-8 avec BOM**, en remplaçant ceux de l'étape précédente (reprendre `GitLabUrl` et `ProjectId`).

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
}
```

## ci-dev.ps1

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Runner CI de dev (US 1) : vérifie la connexion à GitLab.

.DESCRIPTION
    Lancé à la main, le script lit la configuration (ci-dev.psd1) et le jeton
    GitLab chiffré, lit le projet par l'API GitLab et affiche le résultat.

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
        'GitLabUrl', 'ProjectId', 'TokenFile', 'RunnerRoot'
    )

    foreach ($key in $requiredKeys) {
        if (-not $config.ContainsKey($key) -or [string]::IsNullOrWhiteSpace([string] $config[$key])) {
            throw "Configuration incomplète : la clé '$key' est absente ou vide dans $Path"
        }
    }

    if ([int] $config.ProjectId -le 0) {
        throw "Configuration : ProjectId doit être l'identifiant numérique du projet GitLab."
    }

    $config.GitLabUrl = ([string] $config.GitLabUrl).TrimEnd('/')

    return $config
}

# ── Journal ──────────────────────────────────────────────────────────────────
# Affichage dans la console uniquement (le fichier de journal arrive à l'US 2).

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

# ── Programme principal ──────────────────────────────────────────────────────

try {
    $script:Config = Read-CiConfiguration -Path $ConfigFile

    # TLS 1.2 n'est pas actif par défaut dans Windows PowerShell 5.1.
    [Net.ServicePointManager]::SecurityProtocol = `
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $token = Get-GitLabToken

    $projectPath = 'projects/{0}' -f $script:Config.ProjectId
    $project = Invoke-GitLabApi -Path $projectPath -Token $token

    Write-CiLog "Connexion GitLab OK : projet « $($project.path_with_namespace) » ($($script:Config.GitLabUrl))"
    exit 0
}
catch {
    Write-CiLog $_.Exception.Message 'ERREUR'
    exit 1
}
```
