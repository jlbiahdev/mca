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

    # Jeton GitLab chiffré par protect-token.ps1 (DPAPI, niveau machine) ;
    # lecture limitée à SYSTEM, aux administrateurs et au compte du runner.
    TokenFile  = 'D:\ci\runner\gitlab-token.dat'

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

## protect-token.ps1

Nouveau à cette US : chiffre le jeton GitLab au niveau de la machine et limite la lecture du fichier à SYSTEM, aux administrateurs et au compte du runner. À copier aussi dans `D:\ci\runner`, en UTF-8 avec BOM ; il ne change plus aux US suivantes (il sert aussi à renouveler le jeton). L'option `-FromClixml <fichier>` reprend le jeton d'un fichier créé par `Export-Clixml`, sans le recoller.

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Chiffre le jeton GitLab du runner et restreint l'accès au fichier.

.DESCRIPTION
    Le jeton est demandé à l'invite (rien ne s'affiche) puis chiffré par
    Windows (DPAPI) au niveau de la MACHINE : la tâche planifiée peut le
    déchiffrer même quand aucune session n'est ouverte et sans mot de passe
    enregistré.

    Un jeton chiffré au niveau de la machine peut être déchiffré par tout
    compte capable de LIRE le fichier. Le script retire donc tous les droits
    hérités et n'autorise que trois comptes :
    - SYSTEM ;
    - le groupe Administrateurs ;
    - le compte du runner (par défaut, celui qui lance ce script).

    Un fichier existant est remplacé (renouvellement du jeton).

.PARAMETER Path
    Fichier du jeton chiffré. Par défaut : gitlab-token.dat, à côté de ce
    script.

.PARAMETER Account
    Compte du runner, autorisé à lire le fichier. Par défaut : le compte qui
    lance ce script.

.PARAMETER FromClixml
    Lit le jeton dans un fichier créé par Export-Clixml (ancien format, chiffré
    au niveau du compte) au lieu de le demander. À lancer avec le compte qui a
    créé ce fichier.

.EXAMPLE
    .\protect-token.ps1

.EXAMPLE
    .\protect-token.ps1 -FromClixml D:\ci\runner\gitlab-token.xml

.NOTES
    Code de sortie : 0 succès, 1 erreur.
#>
[CmdletBinding()]
param(
    [string] $Path,

    [string] $Account,

    [string] $FromClixml
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    # En Windows PowerShell 5.1, $PSScriptRoot est vide dans les valeurs par
    # défaut du bloc param() : les valeurs par défaut sont calculées ici.
    if ([string]::IsNullOrWhiteSpace($Path)) {
        $Path = Join-Path $PSScriptRoot 'gitlab-token.dat'
    }

    if ([string]::IsNullOrWhiteSpace($Account)) {
        $Account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    }

    $Path = [IO.Path]::GetFullPath($Path)

    # Vérifie que le compte existe avant de toucher au fichier.
    try {
        $accountSid = ([Security.Principal.NTAccount] $Account).Translate([Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Compte introuvable : $Account"
    }

    if ([string]::IsNullOrWhiteSpace($FromClixml)) {
        $secureToken = Read-Host 'Jeton GitLab' -AsSecureString
    }
    else {
        try {
            $secureToken = Import-Clixml -LiteralPath $FromClixml
        }
        catch {
            throw "Lecture impossible de $FromClixml : le fichier doit avoir été créé par le compte qui lance ce script, sur cette machine."
        }
    }

    $token = [Net.NetworkCredential]::new('', $secureToken).Password

    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'Aucun jeton saisi.'
    }

    Add-Type -AssemblyName System.Security

    # L'entropie lie le chiffrement à cet usage : un autre outil qui déchiffre
    # des données DPAPI de la machine ne lit pas ce fichier sans la connaître.
    $entropy   = [Text.Encoding]::UTF8.GetBytes('Styx.CiDev')
    $plain     = [Text.Encoding]::UTF8.GetBytes($token)
    $encrypted = [Security.Cryptography.ProtectedData]::Protect(
        $plain, $entropy, [Security.Cryptography.DataProtectionScope]::LocalMachine)

    [Array]::Clear($plain, 0, $plain.Length)
    $token = $null

    Set-Content -LiteralPath $Path -Value ([Convert]::ToBase64String($encrypted)) -Encoding ASCII

    # Droits : uniquement SYSTEM, Administrateurs et le compte du runner.
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)

    foreach ($rule in @($acl.Access)) {
        [void] $acl.RemoveAccessRuleSpecific($rule)
    }

    $allowed = @(
        [Security.Principal.SecurityIdentifier] 'S-1-5-18',      # SYSTEM
        [Security.Principal.SecurityIdentifier] 'S-1-5-32-544',  # Administrateurs
        $accountSid
    )

    foreach ($sid in $allowed) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'Allow')
        $acl.AddAccessRule($rule)
    }

    Set-Acl -LiteralPath $Path -AclObject $acl

    Write-Host ''
    Write-Host "Jeton chiffré dans $Path" -ForegroundColor Green
    Write-Host "  Lecture autorisée : SYSTEM, Administrateurs, $Account"
    Write-Host ''

    exit 0
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
```
