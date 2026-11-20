# README - Scripts STYX (poste de développement)

## Objectif

Les scripts STYX permettent de :

- compiler la solution ;
- démarrer localement l'ensemble des composants à partir d'un build Release ;
- vérifier, en option, l'injection de dépendances des applications (environnement Development) ;
- arrêter proprement les composants démarrés.

---

# Utilisation courante

Démarrer les composants :

```powershell
.\run_all.ps1
```

Démarrer les composants avec la validation de l'injection de dépendances et les user-secrets :

```powershell
.\run_all.ps1 -Development
```

Arrêter les composants :

```powershell
.\stop_all.ps1
```

---

# Démarrage

```powershell
.\run_all.ps1
```

Le script :

1. Vérifie qu'aucune session n'est active, qu'aucun Runner ne travaille depuis `.run` et que les ports sont libres.
2. Vérifie les secrets obligatoires (sauf avec `-Development`).
3. Compile la solution en Release.
4. Copie les binaires dans le dossier unique :

```text
<RepositoryRoot>\.run
```

5. Démarre automatiquement, dans l'ordre :
   - TaskFlow Runner
   - HPC Lite Agent
   - HPC Lite Scheduler
   - STYX Web API

Le dossier `.run` ne contient qu'un jeu de binaires : celui du dernier lancement.

## Le Runner

Le HPC Lite Runner n'est ni démarré ni arrêté par les scripts : le Scheduler choisit un runner libre en base, et l'Agent lance l'exécutable indiqué dans la colonne `exe_path`. Sur chaque poste, toutes les lignes de runner doivent donc pointer vers :

```text
<RepositoryRoot>\.run\hpclite\runner\Styx.HpcLite.Runner.exe
```

Le chemin exact est affiché en fin de démarrage. Ajouter des lignes en base permet d'avoir plusieurs instances du Runner : elles utilisent toutes le même exécutable.

Si un Runner travaille encore au moment du démarrage, `run_all.ps1` s'arrête sans rien remplacer : attendre la fin de son job puis relancer.

## Option -Development

```powershell
.\run_all.ps1 -Development
```

Les composants (et le Runner, qui hérite de l'environnement de l'Agent) tournent en environnement **Development**. .NET y active des contrôles absents en Test et en production :

- un service *scoped* résolu depuis le conteneur racine provoque une erreur ;
- un service impossible à construire est signalé au démarrage.

Les user-secrets des projets sont également chargés : la variable de chaîne de connexion n'est alors pas exigée.

À utiliser après toute modification de l'enregistrement des services.

## Fichier de session

```text
.run\session.json
```

Le fichier est mis à jour après chaque composant démarré : même interrompu (Ctrl+C), `stop_all.ps1` retrouve les composants déjà lancés.

---

# Arrêt

## Arrêt standard

```powershell
.\stop_all.ps1
```

Les composants de la session sont arrêtés dans l'ordre inverse du démarrage. Chaque composant reçoit d'abord une demande de fermeture (arrêt propre). Un composant n'est arrêté que si son PID et son heure de démarrage correspondent à ceux de la session : un processus ayant récupéré le même PID n'est jamais touché.

Le Runner n'est pas arrêté : il s'arrête de lui-même à la fin de son job.

Le fichier de session est supprimé une fois l'arrêt terminé avec succès. Les anciennes sessions `.run\debug` et `.run\release` éventuelles sont traitées de la même façon.

## Arrêt forcé

```powershell
.\stop_all.ps1 -Force
```

Si un composant est toujours actif après le délai d'arrêt, son arbre de processus est arrêté de force. Sans `-Force`, le script s'arrête en erreur et conserve la session.

## Timeout d'arrêt

Par défaut, le script attend jusqu'à 10 secondes l'arrêt de chaque composant :

```powershell
.\stop_all.ps1 -TimeoutSeconds 30
```

---

# Compilation uniquement

```powershell
.\build_debug.ps1
.\build_release.ps1
.\build.ps1 -Configuration Release -NoRestore
.\build.ps1 -NoClean
```

---

# Configuration de l'environnement

Les scripts lisent :

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

## Secrets

Les secrets ne sont dans aucun fichier du dépôt. `dev.psd1` liste seulement le nom des variables d'environnement qui doivent les porter (`Applications.SecretVariables`), par exemple :

```text
ConnectionStrings__StyxConnection
```

Sur le poste, définir la variable une fois au niveau utilisateur, puis ouvrir un nouveau terminal :

```powershell
[Environment]::SetEnvironmentVariable('ConnectionStrings__StyxConnection', '<chaîne de connexion>', 'User')
```

Sans `-Development`, le démarrage s'arrête avant le build si une variable obligatoire manque. Avec `-Development`, les user-secrets des projets peuvent la fournir.

---

# Gestion des erreurs

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
| `run_all.ps1` | Compile en Release, copie dans `.run` et démarre les composants (`-Development` pour la validation DI) |
| `stop_all.ps1` | Arrête les composants de la session (pas le Runner) |
| `StyxBuild.psm1` | Module commun : configuration, build, commandes natives |
| `Styx.Processes.psm1` | Module commun : démarrage, session et arrêt des composants |
