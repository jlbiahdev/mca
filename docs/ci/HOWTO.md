# Mettre en place le runner CI sur un autre projet

Ce document décrit, étape par étape, l'installation du runner (`README.md` explique son fonctionnement). À la fin, chaque commit de MR est compilé et chaque merge dans `main` publie un package sur Artifactory, sans runner GitLab.

Durée : environ une heure, hors délais d'obtention des accès.

## 1. Prérequis

| Élément | Exigence |
|---|---|
| GitLab | rôle **Maintainer** sur le projet (création du jeton, de la variable, réglages de MR) |
| Machine | Windows Server, Windows PowerShell 5.1, lecteur `D:` |
| Compte Windows | compte de service administrateur de la machine, qui fera tourner la tâche |
| .NET | SDK .NET de la solution installé sur la machine (`dotnet --list-sdks`) |
| Réseau | la machine joint GitLab et Artifactory en HTTPS |
| Artifactory | une clé d'API autorisée à pousser dans le dépôt NuGet cible |

Le dépôt doit se compiler **sans accès extérieur** depuis la machine :

- les sources NuGet sont dans le dépôt, et `nuget.config` les limite à celles-ci (`<clear />`) ; nuget.org n'est pas joignable ;
- aucune référence ne pointe vers un partage réseau (`HintPath` en `\\serveur\...`) : la tâche n'y a pas accès ; copier les DLL dans le dépôt ;
- les fichiers `*.nupkg` et `*.dll` nécessaires sont bien versionnés (le `.gitignore` les exclut souvent : `git status --ignored` les signale par `!!`).

Le dépôt doit contenir, dans `src/scripts/devops/` :

| Fichier | Rôle |
|---|---|
| `int/build.ps1` | build en intégration, sans git (`-RepositoryRoot`, `-Environment`) |
| `int/package.ps1` | génération du package (`-RepositoryRoot`, `-PackageVersion`, `-OutputDirectory`) |
| `int/push-artifactory.ps1` | push du package (`-PackageFile`, `-RepositoryRoot`, `-Environment`) |
| `config/int.psd1` | configuration de l'environnement, dont `Artifactory.BaseUrl`, `Artifactory.Repository`, `Artifactory.ApiKeyVariable` |
| `cicd/ci/` | scripts du runner : `ci-int.ps1`, `ci-int.psd1`, `install-task.ps1`, `protect-token.ps1`, `diag-token.ps1` |

Vérifier sur un poste de développeur que la solution se restaure avec un dossier de paquets vide :

```powershell
dotnet restore .\src\backend\<solution>.sln --packages "$env:TEMP\nuget-test"
```

Résultat attendu : aucune erreur `NU1101`, `NU1102` ni `NU1301`.

## 2. Dans GitLab

### 2.1 Identifiant du projet

1. Ouvrir la page d'accueil du projet, menu **⋮** (en haut à droite) : **Copy project ID**.
2. Noter l'identifiant et l'adresse de GitLab (par exemple `https://XXXXX`, sans `/` final).

### 2.2 Jeton du runner

1. **Settings > Access tokens > Add new token** :

   | Champ | Valeur |
   |---|---|
   | Token name | `ci-int-<projet>-api` |
   | Expiration date | selon la règle de l'entreprise ; noter la date pour le renouvellement |
   | Select a role | **Maintainer** |
   | Select scopes | `api` |

2. **Create project access token**, puis copier le jeton : il ne sera plus affiché. Ne pas le coller dans un fichier ni une note.

Résultat attendu : le jeton apparaît dans **Active project access tokens**, avec le rôle Maintainer.

Si le menu **Access tokens** n'existe pas, l'instance interdit peut-être les jetons de projet : le signaler avant d'aller plus loin.

### 2.3 Clé Artifactory

**Settings > CI/CD > Variables > Add variable** :

| Champ | Valeur |
|---|---|
| Type | `Variable (default)` |
| Environments | `All (default)` |
| Visibility | `Masked` |
| Protect variable | décoché |
| Expand variable reference | décoché |
| Description | `Clé Artifactory du runner ci-int` |
| Key | `ARTIFACTORY_API_KEY` (même nom que `Artifactory.ApiKeyVariable` dans `config/int.psd1`) |
| Value | la clé Artifactory |

Résultat attendu : la variable apparaît dans la liste, valeur masquée.

### 2.4 Réglages des merge requests

1. Si le dépôt contient un `.gitlab-ci.yml` créé par défaut (détection de secrets, par exemple), le supprimer par une MR : faute de runner GitLab, ses pipelines restent « en attente » indéfiniment et bloquent les merges.
2. **Settings > Merge requests > Merge checks** : cocher **Pipelines must succeed**, puis **Save changes**. Une MR ne pourra être fusionnée qu'après un build réussi du runner.

   Pour fusionner la MR de suppression du `.gitlab-ci.yml`, qui n'a aucun pipeline, cocher cette option après ce merge.

## 3. Sur la machine

Toutes les commandes se lancent dans une console **Windows PowerShell administrateur**, ouverte avec le compte de service du runner.

### 3.1 Dossiers

```powershell
New-Item -ItemType Directory -Force D:\ci\runner, D:\ci\build, D:\ci\packages
```

Résultat attendu : la liste des trois dossiers.

### 3.2 Scripts du runner

1. Copier dans `D:\ci\runner` les scripts de `src/scripts/devops/cicd/ci/` du dépôt (`ci-int.ps1`, `ci-int.psd1`, `install-task.ps1`, `protect-token.ps1`, `diag-token.ps1`) (git n'est pas nécessaire sur la machine : copie depuis un poste de développeur par la connexion Bureau à distance, ou téléchargement de chaque fichier depuis GitLab, bouton **Download**).
2. Débloquer les fichiers copiés :

   ```powershell
   Get-ChildItem D:\ci\runner | Unblock-File
   ```

   Résultat attendu : aucun message.

### 3.3 Configuration

1. Ouvrir `D:\ci\runner\ci-int.psd1` dans un éditeur et renseigner `GitLabUrl` et `ProjectId` (§ 2.1). Adapter les autres clés si besoin (§ 5).
2. Enregistrer tous les scripts en UTF-8 **avec BOM**, sinon Windows PowerShell 5.1 affiche mal les accents et peut refuser les scripts :

   ```powershell
   Get-ChildItem D:\ci\runner -Include *.ps1, *.psd1 -Recurse | ForEach-Object { Set-Content -LiteralPath $_.FullName -Value (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8) -Encoding UTF8 -NoNewline }
   ```

   Résultat attendu : aucun message.

3. Vérifier la configuration :

   ```powershell
   Import-PowerShellDataFile D:\ci\runner\ci-int.psd1
   ```

   Résultat attendu : la liste des clés et de leurs valeurs, sans erreur.

### 3.4 Jeton chiffré

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\protect-token.ps1
```

Attendre l'invite `Jeton GitLab:`, coller le jeton (rien ne s'affiche), puis Entrée.

Résultat attendu :

```text
Jeton chiffré dans D:\ci\runner\gitlab-token.dat
  Lecture autorisée : SYSTEM, Administrateurs, <domaine>\<compte>
```

Vider ensuite le presse-papiers (copier n'importe quel autre texte).

### 3.5 Premier passage, à la main

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\ci-int.ps1
```

Résultat attendu, selon l'état du projet :

- s'il y a des MR ouvertes, une ligne `MR !<n> « <titre> » …` par MR, puis le build de chacune ;
- pour `main` : `Branche main : commit <x> à publier`, puis le package et le push (`version 1.0.<n> publiée sur Artifactory`), ou `commit <x> ignoré, scripts absents du commit` si `main` ne contient pas encore les scripts de publication ;
- sans rien à faire : `Aucune MR à traiter (<n> ouverte(s)), main à jour.`

En cas d'erreur, voir le dépannage de `README.md` (§ 8).

### 3.6 Tâche planifiée

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\install-task.ps1
```

Résultat attendu :

```text
Tâche Styx.CiInt créée, état : Ready
  Compte    : <domaine>\<compte>
  Exécution : ...
  Script    : D:\ci\runner\ci-int.ps1
  Fréquence : toutes les 1 minute(s)
```

Aucun mot de passe n'est demandé : c'est normal (« Do not store password »).

Une à deux minutes plus tard, le journal du jour reçoit une ligne par minute :

```powershell
Get-Content D:\ci\runner\logs\ci-int-$(Get-Date -Format yyyyMMdd).log -Tail 3
```

Résultat attendu : `Aucune MR à traiter (<n> ouverte(s)), main à jour.`, une ligne par minute.

## 4. Vérification de bout en bout

1. **Session fermée** : se déconnecter de la machine (**Sign out**, pas seulement fermer la fenêtre), attendre deux minutes, se reconnecter. Résultat attendu : le journal du jour a continué à recevoir une ligne par minute.
2. **Build réussi** : sur une branche de test, faire un commit qui compile et créer la MR. Résultat attendu, dans les deux minutes : la MR affiche un pipeline en cours (`ci-int`), puis « passed » ; un clic sur le job ouvre le journal du build.
3. **Build en échec** : sur la même MR, commit d'une ligne invalide dans un fichier `.cs`. Résultat attendu : pipeline « failed », commentaire dans la MR avec l'erreur de compilation, bouton **Merge** bloqué (« Pipeline must succeed »).
4. **Correction** : commit qui corrige. Résultat attendu : pipeline « passed », MR fusionnable.
5. **Publication** : fusionner la MR. Résultat attendu, au passage suivant : sur la page du commit de fusion, pipeline « passed » (`ci-int-publish`), et le package `Styx.Publish` en version `1.0.<numéro du pipeline>` sur Artifactory.
6. **Secret** : rechercher les premiers caractères de la clé Artifactory et du jeton dans `D:\ci\runner\logs` et `D:\ci\runner\builds`. Résultat attendu : aucun résultat ; le journal du push affiche `--api-key ***`.

## 5. Adaptations

| Besoin | Où |
|---|---|
| Autre script de build, de package ou de push | `BuildScript`, `PackageScript`, `PushScript` dans `ci-int.psd1` |
| Autre environnement | `BuildEnvironment` dans `ci-int.psd1`, et `config\<env>.psd1` dans le dépôt |
| Autre branche publiée | `PublishBranch` |
| Noms des statuts affichés dans GitLab | `StatusName`, `PublishStatusName` |
| Format de version | `VersionFormat` (`{0}` = numéro du pipeline) |
| Autres dossiers | `RunnerRoot`, `BuildRoot`, `PackageRoot`, `TokenFile` |
| Build plus long | `BuildTimeoutMinutes` |
| Autre nom de tâche | `install-task.ps1 -TaskName <nom>` |
| Tâche uniquement quand la session est ouverte | `install-task.ps1 -InteractiveOnly` |

Après une modification de la configuration, rien à relancer : elle est lue à chaque passage.

## 6. À noter pour l'exploitation

- Date d'expiration du jeton GitLab : la reporter dans le calendrier de l'équipe ; le renouvellement est décrit dans `README.md` (§ 7).
- Les dossiers `logs`, `builds` et `packages` grossissent sans purge automatique.
