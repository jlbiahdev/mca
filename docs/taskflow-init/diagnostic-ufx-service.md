# Diagnostic — tâches UFX en échec quand Taskflow tourne en service Windows

Objectif : reproduire l'échec des tâches UFX avec Taskflow en service Windows, et relever les traces qui permettent d'en identifier la cause.

Rappel : en service, Taskflow tourne dans la **session 0**, sans bureau interactif, même avec le compte de service en ouverture de session. Le compte détermine **qui** exécute Taskflow ; la session détermine **où**. Causes possibles de l'échec :

- une **fenêtre d'Excel bloquante**, invisible en session 0 (premier lancement, mode protégé, sécurité des macros, récupération de document…) : Excel attend une réponse que personne ne peut donner ;
- des **droits COM/DCOM** qui empêchent le lancement d'Excel depuis un service ;
- une **autre dépendance au bureau** dans le code de la tâche UFX.

Toutes les commandes se lancent dans une console **PowerShell en administrateur**.

---

## 1. Mettre Taskflow en service avec le compte de service

Lancer `taskflow_service_create.ps1` (voir `taskflow_service.md`), avec `$ServiceAccount` renseigné, comme à l'origine.

```powershell
.\taskflow_service_create.ps1
```

Résultat attendu : `Service TaskFlow.Runner démarré.`

**Noter l'heure.**

---

## 2. Lancer une tâche UFX

Lancer une tâche UFX, puis attendre qu'elle échoue, ou qu'elle reste bloquée quelques minutes.

---

## 3. Relever les traces

### 3.1 Excel est-il bloqué dans la session 0 ?

```powershell
Get-Process EXCEL -ErrorAction SilentlyContinue | Select-Object Id, SessionId, StartTime
```

Lecture : une ligne avec `SessionId` à `0` qui ne disparaît pas indique qu'Excel attend probablement une réponse à une fenêtre invisible.

### 3.2 Fenêtres et alertes d'Office

Office enregistre ses messages et ses alertes dans le journal `OAlerts` :

```powershell
Get-WinEvent -LogName OAlerts -MaxEvents 10 | Format-List TimeCreated, Message
```

### 3.3 Erreurs du journal Application (30 dernières minutes)

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Level = 1,2,3; StartTime = (Get-Date).AddMinutes(-30) } | Format-List TimeCreated, ProviderName, Id, Message
```

### 3.4 Refus de lancement COM/DCOM

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-DistributedCOM'; StartTime = (Get-Date).AddMinutes(-30) } | Format-List TimeCreated, Id, Message
```

### 3.5 Journal de Taskflow

Relever, pour cette tâche UFX, le message d'erreur et la pile d'appels s'il y en a une.

Une commande qui ne renvoie rien est aussi une information : la noter.

---

## 4. Revenir à la tâche planifiée

```powershell
.\taskflow_service_remove.ps1
```

Résultat attendu : `Service TaskFlow.Runner supprimé.`, puis `Tâche planifiée TaskFlow.Runner réactivée et lancée.`

S'il reste un Excel bloqué :

```powershell
Stop-Process -Name EXCEL -Force
```

---

## 5. Résultats

| Trace | Résultat |
|---|---|
| Heure du test | |
| 3.1 Excel en session 0 | |
| 3.2 OAlerts | |
| 3.3 Journal Application | |
| 3.4 DCOM | |
| 3.5 Journal Taskflow | |
