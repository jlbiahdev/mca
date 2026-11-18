# taskflow_init.ps1 — tâche planifiée Taskflow

Recrée la tâche planifiée `TaskFlow.Runner`, qui lance Taskflow dans la session du compte de service (session interactive, nécessaire aux tâches UFX).

- **Déclencheurs** : à l'ouverture de session du compte, et toutes les 5 minutes. Si Taskflow tourne déjà, `IgnoreNew` empêche une deuxième instance ; s'il s'est arrêté, il est relancé.
- **Réglages** : droits administrateur (`RunLevel Highest`), sans limite de durée, 3 relances à 1 minute d'intervalle si la tâche échoue.

## Utilisation

1. Créer le fichier `taskflow_init.ps1`, y coller le bloc de code ci-dessous, l'enregistrer en UTF-8 avec BOM.
2. Si la tâche existe déjà, la supprimer d'abord :

   ```powershell
   Unregister-ScheduledTask -TaskName "TaskFlow.Runner" -Confirm:$false
   ```

3. Dans une console PowerShell **en administrateur** :

   ```powershell
   .\taskflow_init.ps1
   ```

   Résultat attendu : une ligne `TaskFlow.Runner` à l'état `Ready`.

4. Démarrer Taskflow sans attendre :

   ```powershell
   Start-ScheduledTask -TaskName "TaskFlow.Runner"
   Get-Process Socgen.TaskFlow.Runner
   ```

   Résultat attendu : la deuxième commande affiche le processus.

## Vérification de la relance

1. Dans le Planificateur de tâches, la tâche a deux déclencheurs : « À l'ouverture de session » et « Une fois, répéter toutes les 5 minutes indéfiniment ».
2. `Stop-Process -Name Socgen.TaskFlow.Runner` : Taskflow revient dans les 5 minutes (`Get-Process Socgen.TaskFlow.Runner`).

## Limite

La tâche ne tourne que si la session du compte est ouverte. Fermer la fenêtre du bureau à distance ne la ferme pas ; « Se déconnecter » ou un redémarrage de la machine, si. Après un redémarrage, se reconnecter une fois en bureau à distance : Taskflow repart à l'ouverture de session.

## Script

```powershell
$RestartEveryMinutes = 5                                        # délai maximal de relance

$taskName = "TaskFlow.Runner"
$exe      = "D:\Applications\taskflow\Socgen.TaskFlow.Runner.exe"
$compte   = "DDEVTIG\YSXAU093D_ADMIN"

$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)

# Deux déclencheurs : à l'ouverture de session, et toutes les N minutes.
# Si Taskflow tourne déjà, IgnoreNew empêche une deuxième instance ; s'il s'est arrêté, il est relancé.
$onLogon = New-ScheduledTaskTrigger -AtLogOn -User $compte
$restart = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes $RestartEveryMinutes)
$trigger = @($onLogon, $restart)

$principal = New-ScheduledTaskPrincipal -UserId $compte -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings
```
