# TaskFlow Runner : passer du service Windows à une tâche planifiée

Cette procédure remplace le service Windows `TaskFlow.Runner` par une tâche planifiée qui lance le runner dans la session ouverte du compte de service.

Elle est nécessaire pour les tâches UFX : Excel, piloté par COM, échoue quand le runner tourne en session 0 (service), et fonctionne quand il tourne dans une session utilisateur.

> Procédure non encore jouée de bout en bout : la dérouler d'abord sur la machine POC.

## Prérequis

- Une session Windows du compte de service est ouverte en permanence sur le serveur.
- Tu disposes des droits administrateur sur le serveur.

## Valeurs à relever

Les commandes utilisent trois valeurs à remplacer, relevées à l'étape 2 :

| Valeur | Exemple de forme |
|---|---|
| `<CHEMIN_EXE>` | `D:\Applications\...\<nom>.exe` |
| `<NOM_PROCESSUS>` | nom de l'exe sans `.exe` |
| `<DOMAINE\compte>` | `DOMAINE\compte` |

## Procédure

### 1. Ouvrir PowerShell en administrateur

Clic droit sur PowerShell → *Exécuter en tant qu'administrateur*.

Résultat attendu : la fenêtre s'ouvre avec « Administrateur » dans son titre.

### 2. Relever le chemin de l'exécutable et le compte du service

```powershell
Get-CimInstance Win32_Service -Filter "Name='TaskFlow.Runner'" | Select-Object Name, PathName, StartName, State
```

Résultat attendu : une ligne avec le chemin de l'exécutable (`PathName`) et le compte du service (`StartName`). Note ces valeurs.

Si rien ne s'affiche, le service porte un autre nom sur cette machine. Retrouve-le avec :

```powershell
Get-Service *TaskFlow*
```

### 3. Vérifier que le service ne définit pas de variables d'environnement

```powershell
reg query HKLM\SYSTEM\CurrentControlSet\Services\TaskFlow.Runner /v Environment
```

Résultat attendu : un message d'erreur indiquant que la valeur est introuvable. C'est le cas normal, il n'y a rien à faire.

Si des variables s'affichent (par exemple `DOTNET_ENVIRONMENT`), arrête-toi ici : il faudra les reporter dans la tâche avant de continuer.

### 4. Arrêter le service

```powershell
Stop-Service TaskFlow.Runner
```

Résultat attendu : aucun message.

### 5. Désactiver le service

Pour éviter que deux runners tournent en même temps :

```powershell
Set-Service TaskFlow.Runner -StartupType Disabled
```

Résultat attendu : aucun message.

### 6. Créer la tâche planifiée

```powershell
$exe = "<CHEMIN_EXE>"
$compte = "<DOMAINE\compte>"
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $compte
$principal = New-ScheduledTaskPrincipal -UserId $compte -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName "TaskFlow.Runner" -Action $action -Trigger $trigger -Principal $principal -Settings $settings
```

Résultat attendu : un tableau avec `TaskName : TaskFlow.Runner` et `State : Ready`.

Ce que fait cette commande :

- `Interactive` : la tâche tourne dans la session ouverte, sans mot de passe à saisir.
- `RunLevel Highest` : le runner garde les mêmes droits élevés qu'en service.
- `ExecutionTimeLimit 0` : supprime l'arrêt automatique après 3 jours, actif par défaut.
- `IgnoreNew` : empêche une deuxième instance de démarrer.

### 7. Démarrer la tâche sans fermer la session

```powershell
Start-ScheduledTask -TaskName "TaskFlow.Runner"
```

Résultat attendu : aucun message dans PowerShell. Une fenêtre console du runner s'ouvre dans la session.

**Ne ferme pas cette fenêtre** : la fermer arrête le runner.

### 8. Vérifier la session du runner

```powershell
Get-Process <NOM_PROCESSUS> | Select-Object Id, SessionId
```

Résultat attendu : un `SessionId` différent de 0.

### 9. Lancer un job contenant des tâches UFX

Résultat attendu : les tâches UFX se terminent avec succès dans le log du TaskFlow.

## Revenir en arrière

Exécute ces commandes une par une.

```powershell
Unregister-ScheduledTask -TaskName "TaskFlow.Runner" -Confirm:$false
```

Résultat attendu : aucun message.

```powershell
Set-Service TaskFlow.Runner -StartupType Automatic
```

Résultat attendu : aucun message.

```powershell
Start-Service TaskFlow.Runner
```

Résultat attendu : aucun message ; le service est de nouveau à l'état `Running`.

## À savoir

- **Après un redémarrage du serveur**, le runner ne repart qu'à l'ouverture de session du compte. Il faut décider qui se connecte, ou configurer l'ouverture de session automatique.
- **Redémarrage en cas de plantage** : `RestartCount` relance la tâche quand elle échoue au démarrage. Il n'est pas établi qu'il la relance si le runner plante en cours de route. À tester en tuant le processus une fois la tâche installée.
- **Script de déploiement** : `download_deploy.ps1` installe aujourd'hui TaskFlow comme service. Il faudra l'adapter pour qu'il crée cette tâche à la place, une fois l'approche validée.
