# Démarrage automatique de la base PostgreSQL locale

`start-postgres.ps1` démarre la base PostgreSQL locale **à l'ouverture de session Windows**,
indépendamment de FETEAD et **sans droits administrateur**.

Le script vérifie d'abord l'état de PostgreSQL (`pg_ctl status`) : si la base tourne déjà,
il ne fait rien.

> Limite : la base démarre quand on ouvre sa session, pas au démarrage de la machine
> (cela demanderait un service Windows, donc des droits administrateur).

## Installation

### 1. Adapter les chemins

En haut de `start-postgres.ps1` :

```powershell
$PgBin  = "C:\pgsql\bin"    # dossier qui contient pg_ctl.exe
$PgData = "C:\pgsql\data"   # dossier des données PostgreSQL
```

Si la configuration de LocalDb Control (section `LocalDbControl` de `appsettings.json` de FETEAD)
contient ces chemins, reprendre les mêmes.

### 2. Tester le script à la main

```powershell
powershell -ExecutionPolicy Bypass -File C:\chemin\vers\start-postgres.ps1
```

- Base arrêtée → `PostgreSQL démarré.`
- Relancé une seconde fois → `PostgreSQL est déjà démarré.`

### 3. Le lancer à chaque ouverture de session

Créer un raccourci dans le dossier Démarrage de Windows, avec cette commande PowerShell
(à lancer une seule fois, en adaptant le chemin du script) :

```powershell
$script   = "C:\chemin\vers\start-postgres.ps1"
$shortcut = Join-Path ([Environment]::GetFolderPath("Startup")) "Démarrer PostgreSQL local.lnk"

$shell = New-Object -ComObject WScript.Shell
$link  = $shell.CreateShortcut($shortcut)
$link.TargetPath = "powershell.exe"
$link.Arguments  = "-WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`""
$link.Save()
```

Pour vérifier : `Win + R`, taper `shell:startup`, le raccourci doit y apparaître.

## Désinstallation

Supprimer le raccourci « Démarrer PostgreSQL local » du dossier `shell:startup`.

## Encodage

Le script est enregistré en **UTF-8 avec BOM** : sans BOM, Windows PowerShell 5.1 affiche mal
les accents des messages. Le garder ainsi en cas de modification.

## En cas de problème

Le journal de PostgreSQL est écrit dans `demarrage.log`, dans le dossier des données.
