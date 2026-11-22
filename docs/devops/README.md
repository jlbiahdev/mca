# DevOps Styx

Scripts PowerShell pour compiler, lancer, packager, publier et déployer Styx, sur le poste du développeur comme sur la machine d'intégration. Ils remplacent les runners GitLab, dont la création est bloquée sur l'instance.

Tous les scripts demandent **Windows PowerShell 5.1** et doivent être enregistrés en **UTF-8 avec BOM** (sinon les accents sont corrompus et certains scripts refusent de démarrer : voir `repair_utf8_bom.ps1`).

## Contenu

```text
devops/
├── dev/        poste du développeur : build, lancement, arrêt, package, push
├── int/        intégration : build, package et push, sans git
├── cicd/
│   ├── ci/     runner d'intégration : build des MR, package et publication sur Artifactory
│   └── cd/     téléchargement du package et déploiement sur les serveurs
│       ├── 02_download_deploy/
│       ├── 03_deploy/
│       └── tests_download_deploy/
├── howto/      mise en place du runner CI sur un autre projet : HOWTO.md et fichiers modèles
├── config/     configuration par environnement (dev.psd1, int.psd1)
├── modules/    fonctions communes (StyxBuild, Styx.Processes)
├── README.md   ce document
├── runner.md   runner CI : fonctionnement et exploitation
├── env.example.ps1
├── mytree.ps1
└── repair_utf8_bom.ps1
```

| Dossier | Pour qui | Détail |
|---|---|---|
| `dev/` | développeur, sur son poste | `dev/README.md` |
| `int/` | appelé par le runner sur la machine d'intégration | ci-dessous |
| `cicd/ci/` | équipe qui exploite le runner | `runner.md` |
| `howto/` | équipe qui installe le runner sur un autre projet | `howto/HOWTO.md` |
| `cicd/cd/` | équipe qui déploie sur les serveurs | le README de chaque sous-dossier |
| `config/` | tous | ci-dessous |
| `modules/` | utilisé par les scripts, pas lancé directement | ci-dessous |

## Les environnements

| | `dev` | `int` |
|---|---|---|
| Où | poste du développeur | machine d'intégration |
| Sources | dépôt git cloné ; la racine est trouvée par git | sources téléchargées par le runner, sans git (`-RepositoryRoot` obligatoire) |
| Configuration du build | Debug ou Release, au choix | toujours Release |
| Lancé par | le développeur | le runner `ci-int` |

Chaque environnement a son fichier dans `config/` : hôte et ports, configuration .NET (configuration, runtime, self-contained), dossiers de travail, partage, Artifactory, variables transmises aux applications. **Ces fichiers ne contiennent aucun secret** : ils indiquent seulement le **nom** des variables d'environnement qui portent les secrets (chaîne de connexion, clé Artifactory).

## `dev/` — sur le poste du développeur

| Script | Rôle |
|---|---|
| `build.ps1`, `build_debug.ps1`, `build_release.ps1` | compilation de la solution |
| `run_all.ps1`, `stop_all.ps1` | lancement et arrêt de tous les composants (API, HpcLite, Taskflow) |
| `generate_package.ps1` | génération du package NuGet `Styx.Publish` |
| `push-artifactory.ps1` | push d'un package sur Artifactory |

Options, prérequis et résultats : `dev/README.md`.

## `int/` — en intégration

Versions des scripts de dev qui fonctionnent sans git, appelées par le runner avec la racine des sources téléchargées.

| Script | Rôle | Paramètres |
|---|---|---|
| `build.ps1` | restauration et compilation en Release (configuration lue dans `config/int.psd1`) | `-RepositoryRoot`, `-Environment` (`int` par défaut) |
| `package.ps1` | compilation des applications et génération de `Styx.Publish.<version>.nupkg` | `-RepositoryRoot`, `-PackageVersion`, `-OutputDirectory` |
| `push-artifactory.ps1` | push du package ; adresse et dépôt lus dans `config/int.psd1`, clé lue dans la variable d'environnement nommée par `Artifactory.ApiKeyVariable` | `-PackageFile`, `-RepositoryRoot`, `-Environment` |

Ces scripts sont livrés avec le code : c'est la version du script contenue dans un commit qui construit ce commit. Une modification de la structure de la solution (projet ajouté, déplacé) se fait donc dans la même MR que la mise à jour de `package.ps1`.

Exemple, à la main sur la machine d'intégration :

```powershell
.\int\build.ps1 -RepositoryRoot D:\ci\build
```

## `cicd/ci/` — le runner d'intégration (CI)

Le runner `ci-int` est lancé chaque minute par une tâche planifiée sur la machine d'intégration :

- **merge requests** : chaque nouveau commit est compilé (`int/build.ps1`) ; le résultat s'affiche dans la MR (statut `ci-int`), avec le journal et, en cas d'échec, un commentaire listant les erreurs ;
- **publication** : chaque nouveau commit de `main` est packagé (`int/package.ps1`) et poussé sur Artifactory (`int/push-artifactory.ps1`), en version `1.0.<numéro du pipeline GitLab>` (statut `ci-int-publish`).

Publier le package fait partie de l'intégration : le runner produit l'artefact, il ne déploie rien.

| Fichier | Rôle |
|---|---|
| `ci-int.ps1`, `ci-int.psd1` | le runner et sa configuration |
| `install-task.ps1` | création de la tâche planifiée `Styx.CiInt` |
| `protect-token.ps1` | chiffrement du jeton GitLab sur la machine |
| `diag-token.ps1` | diagnostic du jeton chiffré |
| `gitlab-token.dat` | jeton GitLab chiffré par `protect-token.ps1` |

Le dossier contient le runner tel qu'il est installé, avec ses vraies valeurs, et s'utilise tel quel. Fonctionnement, exploitation et dépannage : `runner.md`.

## `howto/` — installer le runner ailleurs

`HOWTO.md` décrit la mise en place du runner sur un autre projet, étape par étape. Les fichiers qu'il décrit sont à côté de lui, sans valeur réelle (configuration à renseigner, pas de jeton) : `ci-int.ps1`, `ci-int.psd1`, `install-task.ps1`, `protect-token.ps1`, `diag-token.ps1`.

## `cicd/cd/` — le déploiement (CD)

| Dossier | Contenu |
|---|---|
| `02_download_deploy/` | `download_deploy.ps1` : télécharge le package `Styx.Publish` depuis Artifactory et met à jour, sur le serveur, les applications demandées (`deploy-api.ps1`, `deploy-hpclite.ps1`, `deploy-taskflow.ps1`) ; voir son `README.md` |
| `03_deploy/` | `deploy.ps1` et les scripts par application ; voir son `README.md` |
| `tests_download_deploy/` | scénarios de test de `download_deploy.ps1`, rangés par jalon (`jalon_0` à `jalon_19`), lancés par `run_jalon.ps1` ; outils communs dans `_common` et `_tools` ; voir son `README.md` |

Le téléchargement s'authentifie sur Artifactory avec `ARTIFACTORY_USERNAME` et `ARTIFACTORY_TOKEN` ; le push du runner CI utilise une clé d'API distincte. Le déploiement n'est pas encore automatisé.

## `modules/`

| Module | Contenu |
|---|---|
| `StyxBuild.psm1` | affichage homogène (étapes, succès, erreurs), vérification des commandes, lancement de commandes externes, lecture de la configuration d'environnement, racine du dépôt, lecture des secrets, version de package, dossiers de travail |
| `Styx.Processes.psm1` | lancement et arrêt des processus Styx (utilisé par `run_all.ps1` et `stop_all.ps1`) |

## Scripts à la racine

| Script | Rôle |
|---|---|
| `env.example.ps1` | modèle des variables d'environnement à définir sur le poste (secrets). Le copier, le renseigner, et ne jamais versionner la copie |
| `mytree.ps1` | affichage de l'arborescence d'un dossier |
| `repair_utf8_bom.ps1` | réenregistre en UTF-8 avec BOM les scripts d'un dossier, en retirant les caractères invalides |

Exemple, après une copie de scripts :

```powershell
.\repair_utf8_bom.ps1 D:\ci\runner
```

## Règles communes

- **Aucun secret dans le dépôt** : ni dans les scripts, ni dans `config/`, ni dans les journaux. Sur le poste, les secrets sont des variables d'environnement ; pour le runner, le jeton GitLab est chiffré sur la machine et la clé Artifactory est une variable GitLab masquée.
- **Les sources NuGet sont dans le dépôt** (`nugets/`), et `src/backend/nuget.config` se limite à elles : nuget.org est interdit, et l'Artifactory NuGet est incomplet. Un nouveau paquet se versionne avec `git add -f`, car le `.gitignore` exclut les `.nupkg`.
- **Pas de référence à un partage réseau** dans les projets : le runner n'y a pas accès. Les DLL externes sont copiées dans le dépôt.
- **Code de sortie** : 0 en cas de succès, 1 en cas d'erreur, pour tous les scripts.
