# README - Scripts STYX

## Objectif

Les scripts STYX permettent de :

- compiler la solution ;
- démarrer un environnement complet de développement ;
- démarrer un environnement complet de validation ;
- arrêter proprement tous les composants démarrés ;
- préparer les futures opérations de packaging et de publication.

---

# Utilisation courante

## Développement

Démarrer un environnement complet en mode Debug :

```powershell
.\debug_all.ps1
```

Arrêter tous les composants :

```powershell
.\stop_all.ps1
```

---

## Validation locale

Démarrer un environnement complet en mode Release :

```powershell
.\run_all.ps1
```

Arrêter tous les composants :

```powershell
.\stop_all.ps1
```

---

# Compilation uniquement

## Build Debug

```powershell
.\build_debug.ps1
```

Compile l'intégralité de la solution en mode Debug.

## Build Release

```powershell
.\build_release.ps1
```

Compile l'intégralité de la solution en mode Release.

## Build avancé

```powershell
.\build.ps1
```

Exemples :

```powershell
.\build.ps1 -Configuration Release
.\build.ps1 -NoRestore
.\build.ps1 -NoClean
```

---

# Environnement Debug

## Démarrage

```powershell
.\debug_all.ps1
```

Le script lance le code en mode **débogable** :

1. Vérifie que les ports sont libres.
2. Compile la solution en Debug.
3. Lance chaque composant depuis son projet :

```text
dotnet run --project <projet> --configuration Debug --no-build
```

en environnement **Development** : les user-secrets, `appsettings.Development.json` et `launchSettings.json` des projets s'appliquent. Aucune copie n'est faite.

4. Démarre automatiquement :
   - TaskFlow Runner
   - HPC Lite Agent
   - HPC Lite Scheduler
   - STYX Web API

Le composant HPC Lite Runner n'est pas démarré automatiquement.

En fin de démarrage, le script affiche pour chaque composant le PID de l'hôte `dotnet` et celui de l'application.

## Déboguer

Visual Studio > Déboguer > Attacher au processus, puis choisir le PID de l'application affiché par le script.

## Secrets

En Debug, les secrets (chaîne de connexion) viennent des **user-secrets** de chaque projet :

```powershell
dotnet user-secrets list --project <projet>
```

## Fichier de session

```text
.run\debug\session.json
```

---

# Environnement Release

## Démarrage

```powershell
.\run_all.ps1
```

Le script :

1. Vérifie les secrets obligatoires (variables d'environnement) et la disponibilité des ports.
2. Compile la solution en Release.
3. Crée :

```text
<RepositoryRoot>\.run\release
```

4. Copie les binaires Release.
5. Démarre automatiquement :
   - TaskFlow Runner
   - HPC Lite Agent
   - HPC Lite Scheduler
   - STYX Web API

Le composant HPC Lite Runner est copié mais n'est pas démarré automatiquement.

## Fichier de session

```text
.run\release\session.json
```

---

# Arrêt des environnements

## Arrêt standard

```powershell
.\stop_all.ps1
```

Le script recherche automatiquement les sessions actives :

```text
.run\debug\session.json
.run\release\session.json
```

et tente d'arrêter tous les composants enregistrés.

Les composants arrêtés sont :

- TaskFlow Runner
- HPC Lite Agent
- HPC Lite Scheduler
- STYX Web API

Les composants sont arrêtés dans l'ordre inverse du démarrage, avec leurs processus enfants (en Debug, l'application lancée par `dotnet run`). Chaque composant reçoit d'abord une demande de fermeture (arrêt propre). Un composant n'est arrêté que si son PID et son heure de démarrage correspondent à ceux de la session : un processus ayant récupéré le même PID n'est jamais touché.

Les fichiers de session sont supprimés une fois l'arrêt terminé avec succès.

---

## Arrêt forcé

```powershell
.\stop_all.ps1 -Force
```

Si un composant est toujours actif après le délai d'arrêt, son arbre de processus est arrêté de force. Sans `-Force`, le script s'arrête en erreur et conserve la session.

---

## Timeout d'arrêt

Par défaut, le script attend jusqu'à 10 secondes l'arrêt de chaque processus.

Il est possible de modifier cette durée :

```powershell
.\stop_all.ps1 -TimeoutSeconds 30
```

---

# Configuration de l'environnement

Les scripts `debug_all.ps1` et `run_all.ps1` lisent :

```text
devops\config\dev.psd1
```

Ce fichier contient tout ce qui varie d'un environnement à l'autre : hôte, ports, runtime, dossiers de travail, partage, Artifactory et variables transmises aux applications. Il ne contient aucun secret.

Autre environnement ou autre fichier :

```powershell
.\run_all.ps1 -Environment int
.\run_all.ps1 -ConfigFile C:\chemin\mon-poste.psd1
```

Les scripts vérifient :

- que les ports sont des entiers compris entre 1 et 65535 ;
- qu'ils sont différents ;
- qu'ils ne sont pas déjà utilisés sur le poste.

## Secrets (Release)

Les secrets ne sont dans aucun fichier du dépôt. En Debug, ils viennent des user-secrets des projets (voir plus haut). En Release, `dev.psd1` liste seulement le nom des variables d'environnement qui doivent les porter (`Applications.SecretVariables`), par exemple :

```text
ConnectionStrings__StyxConnection
```

Sur le poste, définir la variable une fois au niveau utilisateur, puis ouvrir un nouveau terminal :

```powershell
[Environment]::SetEnvironmentVariable('ConnectionStrings__StyxConnection', '<chaîne de connexion>', 'User')
```

En Release, le démarrage s'arrête avant le build si une variable obligatoire manque.

---

# Répertoires générés

## Debug

```text
<RepositoryRoot>\.run\debug
```

Contient uniquement le fichier de session : en Debug, les composants sont lancés depuis leurs projets.

## Release

```text
<RepositoryRoot>\.run\release
```

Contient :

- les binaires copiés ;
- le fichier de session ;
- l'environnement d'exécution Release.

---

# Composants STYX

Les scripts de démarrage lancent les composants dans l'ordre suivant :

1. TaskFlow Runner
2. HPC Lite Agent
3. HPC Lite Scheduler
4. STYX Web API

Le composant HPC Lite Runner est préparé et copié mais n'est jamais démarré directement par les scripts.

---

# Gestion des erreurs

Le fichier de session est mis à jour après chaque composant démarré : même interrompu (Ctrl+C), `stop_all.ps1` retrouve les composants déjà lancés.

Si un démarrage échoue :

- les composants déjà démarrés sont arrêtés ;
- le fichier de session est supprimé ;
- un message d'erreur détaillé est affiché.

Si un arrêt échoue :

- le fichier de session est conservé ;
- une nouvelle tentative peut être effectuée ultérieurement.

---

# Scripts disponibles

| Script | Description |
|----------|----------|
| `build_debug.ps1` | Compile STYX en Debug |
| `build_release.ps1` | Compile STYX en Release |
| `build.ps1` | Build avancé avec options |
| `debug_all.ps1` | Compile et lance les composants depuis leurs projets en Debug (débogable, user-secrets) |
| `run_all.ps1` | Compile et démarre l'environnement Release complet |
| `stop_all.ps1` | Arrête toutes les sessions STYX actives |
| `StyxBuild.psm1` | Module commun : configuration, build, commandes natives |
| `Styx.Processes.psm1` | Module commun : démarrage, sessions et arrêt des composants |

---

# Workflow recommandé

## Développement

```powershell
.\debug_all.ps1
```

Travail de développement...

```powershell
.\stop_all.ps1
```

## Validation

```powershell
.\run_all.ps1
```

Tests fonctionnels...

```powershell
.\stop_all.ps1
```

---

# Fonctionnalités à venir

Cette documentation sera enrichie avec les scripts de :

- publication locale ;
- création de packages ;
- publication dans Artifactory.
