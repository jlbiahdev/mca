# US 2 — Lancement automatique : code

Ticket : voir `epic.md`, US 2. Copier les fichiers dans `D:\ci\runner`, en **UTF-8 avec BOM**, en remplaçant ceux de l'étape précédente (reprendre `GitLabUrl` et `ProjectId`).

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
    Runner CI de dev (US 2) : passage automatique, journal et verrou.

.DESCRIPTION
    Lancé chaque minute par la tâche planifiée Styx.CiDev, le script fait un
    passage : il vérifie la connexion à GitLab et l'écrit dans le journal du
    jour. Un verrou empêche deux passages simultanés.

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

    $projectPath = 'projects/{0}' -f $script:Config.ProjectId
    $project = Invoke-GitLabApi -Path $projectPath -Token $token

    Write-CiLog "Passage du runner sur $env:COMPUTERNAME ($env:USERDOMAIN\$env:USERNAME) : connexion GitLab OK, projet « $($project.path_with_namespace) »."
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

## install-task.ps1

Nouveau à cette US : crée la tâche planifiée `Styx.CiDev`. À copier aussi dans `D:\ci\runner`, en UTF-8 avec BOM ; il ne change plus aux US suivantes.

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Crée, ou remplace, la tâche planifiée qui lance le runner CI chaque minute.

.DESCRIPTION
    La tâche tourne avec le compte qui lance ce script : c'est lui qui a
    chiffré le jeton GitLab, et lui seul peut le relire.

    Par défaut, la tâche tourne seulement quand la session de ce compte est
    ouverte (même verrouillée) : aucun mot de passe n'est enregistré.

    Avec -RunWhenLoggedOff, elle tourne aussi session fermée : le mot de passe
    du compte est demandé et enregistré par Windows. Une stratégie de
    l'entreprise peut l'interdire (erreur 0x80070520).

    La tâche tourne avec les privilèges les plus élevés du compte : un compte
    administrateur garde ses droits d'écriture sur les dossiers du runner,
    créés depuis une console administrateur. La fenêtre de PowerShell est
    masquée.

    Si la tâche existe déjà, elle est remplacée ; en cas d'échec, l'ancienne
    tâche est conservée.

.PARAMETER TaskName
    Nom de la tâche. Par défaut : Styx.CiDev.

.PARAMETER ScriptPath
    Script du runner. Par défaut : ci-dev.ps1, à côté de ce script.

.PARAMETER IntervalMinutes
    Intervalle entre deux lancements, en minutes. Par défaut : 1.

.PARAMETER ExecutionTimeLimitHours
    Durée au-delà de laquelle Windows arrête un passage. Par défaut : 2.

.PARAMETER RunWhenLoggedOff
    La tâche tourne aussi session fermée (mot de passe enregistré).

.EXAMPLE
    .\install-task.ps1

.EXAMPLE
    .\install-task.ps1 -RunWhenLoggedOff

.NOTES
    Code de sortie : 0 succès, 1 erreur.
#>
[CmdletBinding()]
param(
    [string] $TaskName = 'Styx.CiDev',

    [string] $ScriptPath,

    [ValidateRange(1, 60)]
    [int] $IntervalMinutes = 1,

    [ValidateRange(1, 24)]
    [int] $ExecutionTimeLimitHours = 2,

    [switch] $RunWhenLoggedOff
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    # En Windows PowerShell 5.1, $PSScriptRoot est vide dans les valeurs par
    # défaut du bloc param() : le chemin par défaut est donc calculé ici.
    if ([string]::IsNullOrWhiteSpace($ScriptPath)) {
        $ScriptPath = Join-Path $PSScriptRoot 'ci-dev.ps1'
    }

    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        throw "Script du runner introuvable : $ScriptPath"
    }

    $ScriptPath       = (Resolve-Path -LiteralPath $ScriptPath).Path
    $workingDirectory = Split-Path -Parent $ScriptPath
    $user             = [Security.Principal.WindowsIdentity]::GetCurrent().Name

    $action = New-ScheduledTaskAction `
        -Execute 'powershell.exe' `
        -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}"' -f $ScriptPath) `
        -WorkingDirectory $workingDirectory

    # Répétition sans fin, à partir de minuit aujourd'hui.
    $trigger = New-ScheduledTaskTrigger `
        -Once `
        -At (Get-Date).Date `
        -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)

    # Pas de second lancement si le précédent tourne encore.
    $settings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -StartWhenAvailable `
        -ExecutionTimeLimit (New-TimeSpan -Hours $ExecutionTimeLimitHours)

    if ($RunWhenLoggedOff) {
        $securePassword = Read-Host "Mot de passe de $user" -AsSecureString
        $password = [Net.NetworkCredential]::new('', $securePassword).Password

        try {
            Register-ScheduledTask `
                -TaskName $TaskName `
                -Action $action `
                -Trigger $trigger `
                -Settings $settings `
                -User $user `
                -Password $password `
                -RunLevel Highest `
                -Force | Out-Null
        }
        catch {
            throw (
                "Création de la tâche refusée : $($_.Exception.Message)" + [Environment]::NewLine +
                "Avec le code 0x80070520, une stratégie interdit d'enregistrer le mot de passe : relancer sans -RunWhenLoggedOff."
            )
        }
        finally {
            $password = $null
        }

        $mode = 'session ouverte ou fermée (mot de passe enregistré par Windows)'
    }
    else {
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest

        Register-ScheduledTask `
            -TaskName $TaskName `
            -Action $action `
            -Trigger $trigger `
            -Settings $settings `
            -Principal $principal `
            -Force | Out-Null

        $mode = 'seulement quand la session de ce compte est ouverte (même verrouillée)'
    }

    $task = Get-ScheduledTask -TaskName $TaskName

    Write-Host ''
    Write-Host "Tâche $TaskName créée, état : $($task.State)" -ForegroundColor Green
    Write-Host "  Compte    : $user"
    Write-Host "  Exécution : $mode"
    Write-Host "  Script    : $ScriptPath"
    Write-Host "  Fréquence : toutes les $IntervalMinutes minute(s)"
    Write-Host ''

    exit 0
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
```
