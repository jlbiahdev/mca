# Service Windows Taskflow — création et suppression

Deux scripts pour tester Taskflow en **service Windows** à la place de la tâche planifiée, puis revenir en arrière si besoin :

| Script | Rôle |
|---|---|
| `taskflow_service_create.ps1` | Désactive la tâche planifiée, arrête Taskflow, crée le service `TaskFlow.Runner` et le démarre |
| `taskflow_service_remove.ps1` | Arrête et supprime le service, puis réactive la tâche planifiée et relance Taskflow par elle |

Une seule des deux formes doit tourner à la fois : sinon, deux instances de Taskflow.

## Utilisation

1. Créer chaque fichier `.ps1` en y collant le bloc de code correspondant, enregistré en UTF-8 avec BOM.
2. Renseigner la configuration en tête de chaque script.
3. Console PowerShell **en administrateur**.

**Passer en service :**

```powershell
.\taskflow_service_create.ps1
```

Résultat attendu : `Tâche planifiée TaskFlow.Runner désactivée.`, `Service TaskFlow.Runner créé.`, puis `Service TaskFlow.Runner démarré.`

**Revenir à la tâche planifiée :**

```powershell
.\taskflow_service_remove.ps1
```

Résultat attendu : `Service TaskFlow.Runner supprimé.`, `Tâche planifiée TaskFlow.Runner réactivée et lancée.`

## Compte du service

- `$ServiceAccount` vide : le service tourne sous le **compte système** (LocalSystem). Pour le test des tâches UFX, c'est ce cas qui utilise les dossiers `C:\Windows\System32\config\systemprofile\Desktop` et `C:\Windows\SysWOW64\config\systemprofile\Desktop` (à créer vides avant le test, s'ils n'existent pas).
- `$ServiceAccount` renseigné : le service tourne sous ce compte ; son mot de passe est demandé dans une fenêtre (jamais tapé en ligne de commande). Le compte doit avoir le droit « Ouvrir une session en tant que service ».

## Relance en cas d'arrêt

Le script de création configure Windows pour **redémarrer le service** une minute après chaque arrêt anormal.

## Script de création — `taskflow_service_create.ps1`

```powershell
#Requires -RunAsAdministrator
# Passe Taskflow en service Windows : désactive la tâche planifiée, arrête Taskflow,
# crée le service et le démarre.

# >>> DEBUT CONFIGURATION
$ServiceName    = "TaskFlow.Runner"
$TaskName       = "TaskFlow.Runner"                                       # tâche planifiée à désactiver
$Executable     = "D:\Applications\taskflow\Socgen.TaskFlow.Runner.exe"
$ServiceAccount = ""          # vide = compte système (LocalSystem) ; sinon DOMAINE\compte
# <<< FIN CONFIGURATION

$ErrorActionPreference = "Stop"

try {
    if (-not (Test-Path $Executable)) {
        throw "Exécutable introuvable : $Executable"
    }
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        throw "Le service $ServiceName existe déjà. Le supprimer d'abord avec taskflow_service_remove.ps1."
    }

    # 1. La tâche planifiée ne doit plus lancer Taskflow
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $TaskName
        Disable-ScheduledTask -TaskName $TaskName | Out-Null
        Write-Host "Tâche planifiée $TaskName désactivée."
    }

    # 2. Arrêt de Taskflow s'il tourne encore (lancé par la tâche ou à la main)
    $process = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Executable)) -ErrorAction SilentlyContinue
    if ($process) {
        $process | Stop-Process -Force
        Write-Host "Taskflow arrêté ($($process.Count) processus)."
    }

    # 3. Création du service (chemin sans espace : pas de guillemets nécessaires)
    if ([string]::IsNullOrWhiteSpace($ServiceAccount)) {
        New-Service -Name $ServiceName -DisplayName $ServiceName -BinaryPathName $Executable -StartupType Automatic | Out-Null
    }
    else {
        $credential = Get-Credential -UserName $ServiceAccount -Message "Mot de passe du compte du service $ServiceName"
        New-Service -Name $ServiceName -DisplayName $ServiceName -BinaryPathName $Executable -StartupType Automatic -Credential $credential | Out-Null
    }
    Write-Host "Service $ServiceName créé."

    # 4. Redémarrage automatique après un arrêt anormal : 3 fois, à 1 minute d'intervalle ;
    #    compteur remis à zéro après 24 h sans incident.
    sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

    # 5. Démarrage
    Start-Service -Name $ServiceName
    Start-Sleep -Seconds 10
    $status = (Get-Service -Name $ServiceName).Status
    if ($status -ne "Running") {
        throw "Le service $ServiceName ne tourne pas après 10 secondes (état : $status). Voir l'Observateur d'événements."
    }
    Write-Host "Service $ServiceName démarré."
    exit 0
}
catch {
    Write-Host "ERREUR : $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
```

## Script de suppression — `taskflow_service_remove.ps1`

```powershell
#Requires -RunAsAdministrator
# Supprime le service Taskflow, puis réactive la tâche planifiée et relance Taskflow par elle.

# >>> DEBUT CONFIGURATION
$ServiceName = "TaskFlow.Runner"
$TaskName    = "TaskFlow.Runner"                                          # tâche planifiée à réactiver
$Executable  = "D:\Applications\taskflow\Socgen.TaskFlow.Runner.exe"
# <<< FIN CONFIGURATION

$ErrorActionPreference = "Stop"

try {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-Host "Le service $ServiceName n'existe pas."
    }
    else {
        # 1. Arrêt du service
        if ($service.Status -ne "Stopped") {
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
            Write-Host "Service $ServiceName arrêté."
        }

        # 2. Suppression (Remove-Service n'existe pas en Windows PowerShell 5.1)
        sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Suppression du service $ServiceName impossible (code $LASTEXITCODE)."
        }
        Write-Host "Service $ServiceName supprimé."
    }

    # 3. Aucun Taskflow ne doit rester en cours avant de relancer la tâche
    $process = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Executable)) -ErrorAction SilentlyContinue
    if ($process) {
        $process | Stop-Process -Force
        Write-Host "Taskflow arrêté ($($process.Count) processus)."
    }

    # 4. Retour à la tâche planifiée
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Enable-ScheduledTask -TaskName $TaskName | Out-Null
        Start-ScheduledTask -TaskName $TaskName
        Write-Host "Tâche planifiée $TaskName réactivée et lancée."
    }
    else {
        Write-Host "Tâche planifiée $TaskName introuvable : la recréer avec taskflow_init.ps1." -ForegroundColor Yellow
    }
    exit 0
}
catch {
    Write-Host "ERREUR : $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
```
