# Démarrage automatique de FETEAD

`start-fetead.ps1` démarre FETEAD **à l'ouverture de session Windows**, en arrière-plan
et **sans droits administrateur**.

Le script vérifie d'abord si quelque chose écoute déjà sur le port de FETEAD (7061) :
si oui (FETEAD déjà lancé, y compris depuis Visual Studio), il ne fait rien.

`stop-fetead.ps1` l'arrête, par exemple pour libérer le port avant de lancer FETEAD
depuis Visual Studio.

> Limite : FETEAD démarre quand on ouvre sa session, pas au démarrage de la machine
> (cela demanderait un service Windows, donc des droits administrateur).

## Installation

### 1. Préparer une copie dédiée de FETEAD

Ne pas lancer FETEAD depuis le dossier `bin` de Visual Studio : tant qu'il tourne,
ses fichiers sont verrouillés et les compilations échouent.

Publier une copie dans un dossier à part (à refaire après chaque mise à jour de FETEAD) :

```powershell
dotnet publish C:\chemin\vers\FeteadApi.Presentation\FeteadApi.Presentation.csproj -c Release -o C:\Tools\Fetead
```

Vérifier que le frontend est bien présent : FETEAD cherche `wwwroot\index.html` à côté
de l'exécutable. Si `C:\Tools\Fetead\wwwroot` n'existe pas, y copier le contenu du dossier `frontend`.

### 2. Adapter le script

En haut de `start-fetead.ps1` :

```powershell
$FeteadExe = "C:\Tools\Fetead\FeteadApi.Presentation.exe"   # exécutable publié
$Url       = "https://localhost:7061"                       # même adresse que Visual Studio
```

Si l'adresse change, la reporter aussi dans `stop-fetead.ps1`.

### 3. Tester le script à la main

```powershell
powershell -ExecutionPolicy Bypass -File C:\chemin\vers\start-fetead.ps1
```

- FETEAD arrêté → `FETEAD démarré : https://localhost:7061`
- Relancé une seconde fois → `FETEAD est déjà démarré (port 7061 occupé).`
- Ouvrir https://localhost:7061 dans le navigateur : le portail s'affiche.

### 4. Le lancer à chaque ouverture de session

Créer un raccourci dans le dossier Démarrage de Windows, avec cette commande PowerShell
(à lancer une seule fois, en adaptant le chemin du script) :

```powershell
$script   = "C:\chemin\vers\start-fetead.ps1"
$shortcut = Join-Path ([Environment]::GetFolderPath("Startup")) "Démarrer FETEAD.lnk"

$shell = New-Object -ComObject WScript.Shell
$link  = $shell.CreateShortcut($shortcut)
$link.TargetPath = "powershell.exe"
$link.Arguments  = "-WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`""
$link.Save()
```

Pour vérifier : `Win + R`, taper `shell:startup`, le raccourci doit y apparaître.

## Avec la base PostgreSQL locale

Les deux raccourcis (`tools/localdb-autostart` et celui-ci) peuvent cohabiter dans le dossier Démarrage,
dans n'importe quel ordre : si la base n'est pas encore prête, le module STYX Monitoring
se reconnecte tout seul toutes les 5 secondes.

## Travailler sur FETEAD dans Visual Studio

Les deux instances ne peuvent pas utiliser le même port en même temps.
Avant de lancer FETEAD depuis Visual Studio :

```powershell
powershell -ExecutionPolicy Bypass -File C:\chemin\vers\stop-fetead.ps1
```

Le script n'arrête que le processus `FeteadApi.Presentation`, jamais un autre programme.

## Désinstallation

Supprimer le raccourci « Démarrer FETEAD » du dossier `shell:startup`.

## Encodage

Les scripts sont enregistrés en **UTF-8 avec BOM** : sans BOM, Windows PowerShell 5.1 affiche mal
les accents des messages. Les garder ainsi en cas de modification.

## En cas de problème

Les journaux de FETEAD sont dans `%LOCALAPPDATA%\Fetead` :
`fetead.log` (sortie normale) et `fetead-erreurs.log` (erreurs). Ils sont remplacés à chaque démarrage.

- **Erreur de certificat HTTPS** : FETEAD utilise le certificat de développement .NET, comme dans
  Visual Studio. S'il manque : `dotnet dev-certs https --trust`.
- **Environnements du monitoring « Unavailable »** : les variables `StyxConnection*` doivent exister
  avant l'ouverture de session (variables utilisateur Windows), ce qui est le cas au démarrage de la machine.
