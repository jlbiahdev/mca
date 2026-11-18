# taskflow-permanent.ps1

Contenu du script, à copier dans `taskflow-permanent.ps1` (voir `README.md`, § 6).

```powershell
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Programme Taskflow pour qu'il tourne en permanence dans la session du compte de service :
    - tâche planifiée lancée à l'ouverture de session, et relancée toutes les 5 minutes s'il s'est arrêté ;
    - réouverture automatique de la session après une déconnexion (ForceAutoLogon).

    Crée la tâche si elle n'existe pas ; si elle existe, met à jour ses déclencheurs et ses réglages
    (l'exécutable et le compte de la tâche existante ne sont pas modifiés).

.PARAMETER ValidationOnly
    Fait toutes les vérifications et affiche ce qui serait fait, sans rien modifier.

.EXAMPLE
    .\taskflow-permanent.ps1 -ValidationOnly
    .\taskflow-permanent.ps1
#>
[CmdletBinding()]
param(
    [switch]$ValidationOnly
)

# >>> DEBUT CONFIGURATION ─────────────────────────────────────────────────────

$ServiceAccount     = "DOMAINE\compte"                                        # à renseigner
$TaskName           = "TaskFlow.Runner"                                       # nom de la tâche sur la machine
$TaskflowExecutable = "D:\Applications\taskflow\Socgen.TaskFlow.Runner.exe"
$RestartEveryMinutes = 5                                                      # délai maximal de relance

# <<< FIN CONFIGURATION ───────────────────────────────────────────────────────

$ErrorActionPreference = "Stop"
$WinlogonKey = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"

function Write-Step {
    param([string]$Message, [ValidateSet("INFO", "ATTENTION", "ERREUR")][string]$Level = "INFO")
    $color = switch ($Level) { "ATTENTION" { "Yellow" } "ERREUR" { "Red" } default { "Gray" } }
    Write-Host ("[{0}] [{1}] {2}" -f (Get-Date -Format "HH:mm:ss"), $Level, $Message) -ForegroundColor $color
}

try {
    # 1. Vérifications

    if ($ServiceAccount -eq "DOMAINE\compte" -or $ServiceAccount -notmatch '^[^\\]+\\[^\\]+$') {
        throw "Configuration incomplète : renseigner `$ServiceAccount (DOMAINE\compte) en tête du script."
    }
    if (-not (Test-Path $TaskflowExecutable)) {
        throw "Exécutable de Taskflow introuvable : $TaskflowExecutable"
    }

    # L'ouverture de session automatique (Autologon) est un prérequis : sans elle, ForceAutoLogon n'a pas d'effet.
    $winlogon      = Get-ItemProperty -Path $WinlogonKey
    $autoLogonOn   = ($winlogon.AutoAdminLogon -eq "1")
    $autoLogonUser = "{0}\{1}" -f $winlogon.DefaultDomainName, $winlogon.DefaultUserName
    if (-not $autoLogonOn) {
        throw "L'ouverture de session automatique n'est pas configurée : la configurer d'abord avec Autologon (voir les prérequis)."
    }
    if ($autoLogonUser -ne $ServiceAccount) {
        throw "L'ouverture de session automatique est configurée pour « $autoLogonUser » et non pour $ServiceAccount."
    }
    Write-Step "Ouverture de session automatique configurée pour $ServiceAccount."

    $existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($existingTask) {
        $taskExe = $existingTask.Actions[0].Execute
        if ($taskExe -ne $TaskflowExecutable) {
            Write-Step "La tâche $TaskName lance « $taskExe » au lieu de $TaskflowExecutable : exécutable conservé tel quel." "ATTENTION"
        }
    }

    # 2. Plan

    if ($existingTask) {
        Write-Step "Tâche $TaskName : mise à jour des déclencheurs (ouverture de session + toutes les $RestartEveryMinutes minutes) et des réglages."
    }
    else {
        Write-Step "Tâche $TaskName : création (ouverture de session + toutes les $RestartEveryMinutes minutes)."
    }
    if ($winlogon.ForceAutoLogon -eq "1") {
        Write-Step "ForceAutoLogon : déjà activé."
    }
    else {
        Write-Step "ForceAutoLogon : activation (réouverture de la session après une déconnexion)."
    }

    if ($ValidationOnly) {
        Write-Step "MODE VALIDATION : rien n'a été modifié."
        exit 0
    }

    # 3. Tâche planifiée

    $onLogon = New-ScheduledTaskTrigger -AtLogOn -User $ServiceAccount
    # Relance : toutes les N minutes, Windows lance la tâche ; si Taskflow tourne déjà,
    # « IgnoreNew » empêche une deuxième instance. Sans durée, la répétition est illimitée.
    $restart = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes $RestartEveryMinutes)
    # Sans limite de durée (72 h par défaut) et une seule instance à la fois.
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

    if ($existingTask) {
        Set-ScheduledTask -TaskName $TaskName -Trigger $onLogon, $restart -Settings $settings | Out-Null
        Write-Step "Tâche $TaskName mise à jour."
    }
    else {
        $action = New-ScheduledTaskAction -Execute $TaskflowExecutable -WorkingDirectory (Split-Path $TaskflowExecutable)
        # Session interactive : nécessaire aux tâches UFX (Excel). Aucun mot de passe stocké dans la tâche.
        $principal = New-ScheduledTaskPrincipal -UserId $ServiceAccount -LogonType Interactive
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $onLogon, $restart `
            -Principal $principal -Settings $settings | Out-Null
        Write-Step "Tâche $TaskName créée."
    }

    # 4. Réouverture automatique de la session après une déconnexion

    if ($winlogon.ForceAutoLogon -ne "1") {
        Set-ItemProperty -Path $WinlogonKey -Name ForceAutoLogon -Value "1" -Type String
        Write-Step "ForceAutoLogon activé."
    }

    Write-Step "Programmation terminée. Taskflow sera lancé au plus tard dans $RestartEveryMinutes minutes s'il ne tourne pas."
    exit 0
}
catch {
    Write-Step $_.Exception.Message "ERREUR"
    exit 1
}
```
