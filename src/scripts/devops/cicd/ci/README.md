# Le runner CI (`ci-int`)

Ce document explique comment surveiller le runner et quoi faire en cas de problème. Pour l'installer, voir le [mode d'emploi d'installation](../../howto/HOWTO.md).

## 1. À quoi sert le runner

Le runner est un petit programme installé sur la machine de build. Une **tâche planifiée** de Windows le lance toutes les minutes. À chaque fois, il regarde dans GitLab s'il y a du travail :

- **Une merge request a reçu du nouveau code** : il vérifie que le code se compile, puis affiche le résultat dans la merge request : une coche verte si tout va bien, une croix rouge sinon (statut `ci-int`).
- **La branche `main` a changé** (une merge request vient d'être fusionnée) : il fabrique le **package** de l'application et le dépose sur **Artifactory**, l'entrepôt des packages (statut `ci-int-publish`). Chaque package reçoit un numéro de version : `1.0.<numéro>`.

Le runner fabrique et publie ; il n'installe rien sur les serveurs. L'installation est décrite dans [Le déploiement](../cd/README.md).

## 2. Vérifier qu'il fonctionne

**Dans GitLab** : ouvrir une merge request. Sous sa description, le résultat `ci-int` s'affiche pour le dernier commit. Un clic dessus ouvre le journal de la compilation.

**Sur la machine de build** : ouvrir le journal du jour, `D:\ci\runner\logs\ci-int-<date>.log` (par exemple `ci-int-20261127.log`).

Vous devez voir une nouvelle ligne chaque minute. Quand il n'y a rien à faire, c'est :

```text
Aucune MR à traiter (<n> ouverte(s)), main à jour.
```

Si aucune ligne n'apparaît depuis plusieurs minutes, voir la partie 4, « En cas de problème ».

## 3. Les gestes courants

Toutes les commandes se tapent sur la machine de build, dans une fenêtre PowerShell ouverte avec le compte du runner. Une commande par cadre : la copier, la coller, appuyer sur **Entrée**.

### Arrêter le runner

```powershell
Disable-ScheduledTask -TaskName 'Styx.CiInt'
```

Vous devez voir une ligne avec `Styx.CiInt` et l'état `Disabled`. Le runner ne se lance plus.

### Relancer le runner

```powershell
Enable-ScheduledTask -TaskName 'Styx.CiInt'
```

Vous devez voir l'état `Ready`. Le runner reprend dans la minute.

### Lancer un passage à la main

Utile pour suivre ce qu'il fait, à l'écran. Arrêter d'abord le runner (ci-dessus), puis :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\ci-int.ps1
```

Les messages s'affichent à l'écran. Relancer ensuite le runner.

### Refaire un commit

Le runner ne traite chaque commit qu'une fois. Pour qu'il le refasse :

1. Ouvrir `D:\ci\runner\processed.txt` (merge requests) ou `D:\ci\runner\processed-publish.txt` (branche `main`) dans le Bloc-notes.
2. Supprimer la ligne qui commence par le numéro du commit, puis enregistrer.

Le commit est refait au passage suivant. Pour `main`, seulement si la publication a échoué : on ne peut pas publier deux fois la même version.

### Renouveler le jeton GitLab

Le jeton GitLab expire au bout de trois mois. Le remplacer avant la date d'expiration :

1. Dans GitLab, **Settings > Access tokens** : créer un nouveau jeton, avec le même rôle (**Maintainer**) et le même scope (`api`). Copier le jeton.
2. Sur la machine de build :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\protect-token.ps1
   ```

   Coller le jeton quand `Jeton GitLab:` s'affiche, puis **Entrée**. Rien ne s'affiche pendant le collage : c'est normal.

   Vous devez voir : `Jeton chiffré dans D:\ci\runner\gitlab-token.dat`.
3. Attendre une minute et vérifier qu'une nouvelle ligne apparaît dans le journal du jour.
4. Dans GitLab, supprimer l'ancien jeton.

### Renouveler la clé Artifactory

La clé Artifactory sert à télécharger les briques logicielles pendant la compilation, et à déposer le package. Elle n'est pas sur la machine, mais dans GitLab :

1. Créer un nouveau jeton : voir [Artifactory](../../artifactory.md), partie 2.
2. Dans GitLab, **Settings > CI/CD > Variables**, modifier la variable `ARTIFACTORY_API_KEY`. Sa valeur est le compte, deux-points, puis le jeton : `usr-<kear_uid>:<jeton>`.

Rien à faire sur la machine.

## 4. En cas de problème

| Ce que vous voyez (journal ou GitLab) | Ce qu'il faut faire |
|---|---|
| Plus aucune nouvelle ligne dans le journal | vérifier la tâche : `Get-ScheduledTask -TaskName 'Styx.CiInt'` doit afficher `Ready` ou `Running`. Sinon, relancer le runner (partie 3) ou refaire l'étape 3.6 du mode d'emploi d'installation |
| Une fenêtre s'ouvre chaque minute | refaire l'étape 3.6 du mode d'emploi d'installation |
| `GitLab refuse le jeton (401)` | le jeton a expiré ou a été supprimé : renouveler le jeton GitLab (partie 3) |
| `GitLab refuse l'accès (403)` | le jeton n'a pas le rôle Maintainer ou le scope `api` : renouveler le jeton GitLab avec les bons droits |
| `Impossible de déchiffrer le jeton GitLab` | le jeton a été enregistré sur une autre machine : relancer `protect-token.ps1` sur celle-ci |
| `Lecture du jeton GitLab refusée` | la tâche ne tourne pas avec le compte du runner : relancer `protect-token.ps1` avec ce compte |
| `Variable GitLab 'ARTIFACTORY_API_KEY' illisible` | la variable n'existe pas dans GitLab, ou le jeton n'a pas le rôle Maintainer |
| `Variable GitLab 'ARTIFACTORY_API_KEY' mal formée` | la valeur doit être `<compte>:<jeton>`, avec les deux-points : corriger la variable |
| `Build échoué` avec `NU1301` et `401` ou `403` dans le journal | Artifactory refuse le téléchargement des briques logicielles : vérifier la variable `ARTIFACTORY_API_KEY` (compte `usr-…`, pas `usr-cd-…`) |
| `Build échoué` dans une merge request | le code ne se compile pas : ce n'est pas un problème du runner. Le commentaire de la merge request donne l'erreur |
| `Push échoué` | lire le journal du push : clé refusée (401 ou 403) ou version déjà publiée (409) ; voir [Artifactory](../../artifactory.md) |
| `Script introuvable dans les sources` | il manque un script dans le code : prévenir un développeur |
| `commit <x> ignoré, scripts absents du commit` | rien à faire : c'est un ancien commit, le suivant sera publié |
| `… interrompu après 30 min` | la compilation a été trop longue : prévenir un développeur |
| `Journal non déposé dans GitLab` | sans conséquence : le journal reste sur la machine |

Pour tout autre message, transmettre le journal du jour à l'équipe du projet.

## 5. Comment il fonctionne (pour les développeurs)

### Un passage

Le script fait un seul passage, puis s'arrête. Si le passage précédent n'est pas fini (une compilation longue), le nouveau s'arrête aussitôt : `Un passage est déjà en cours : rien à faire.`

**Merge requests** : pour chaque merge request dont le dernier commit n'est pas dans `processed.txt` :

1. statut `ci-int` « en cours » sur le commit (GitLab crée un pipeline « externe ») ;
2. lecture de la clé `ARTIFACTORY_API_KEY` dans GitLab ; le compte et le jeton sont transmis au script de compilation dans `ARTIFACTORY_BUILD_USERNAME` et `ARTIFACTORY_BUILD_TOKEN`, que `nuget.config` utilise ;
3. téléchargement du code du commit (archive zip fournie par GitLab, sans git) dans `D:\ci\build`, vidé au préalable ;
4. lancement de `src\scripts\devops\int\build.ps1`, la version livrée avec ce commit ; les briques logicielles sont téléchargées dans `D:\ci\build\.nuget` ;
5. dépôt du journal dans GitLab, puis statut « réussi » ou « échoué » (durée, machine, auteur, lien vers le journal) ;
6. en cas d'échec, commentaire dans la merge request avec les erreurs (50 lignes au plus) ;
7. inscription du commit dans `processed.txt`.

**Branche `main`** : si son dernier commit n'est pas dans `processed-publish.txt` :

1. si le commit ne contient pas les scripts de package et de push, il est noté `ignored` ;
2. statut `ci-int-publish` « en cours » ; le numéro du pipeline externe donne la version `1.0.<numéro>` ;
3. lecture de la clé, téléchargement du code, puis `package.ps1` (avec le compte et le jeton de build) et `push-artifactory.ps1` (avec la clé). Ils passent par l'environnement du processus, jamais par la ligne de commande ni dans un fichier ;
4. dépôt des journaux, statut « réussi » ou « échoué », inscription dans `processed-publish.txt`.

**Erreurs** : un échec de compilation, de package ou de push est le résultat du commit : il est affiché et le commit n'est pas refait. Une erreur du runner lui-même (GitLab injoignable, clé illisible ou mal formée, script introuvable) affiche « échoué » mais ne note pas le commit : il est refait au passage suivant. Une erreur sur les merge requests n'empêche pas la publication de `main`, et inversement.

### Fichiers et dossiers

| Emplacement | Contenu |
|---|---|
| `D:\ci\runner\ci-int.ps1`, `ci-int.psd1` | le runner et ses réglages (aucun secret) |
| `D:\ci\runner\gitlab-token.dat` | le jeton GitLab, chiffré |
| `D:\ci\runner\install-task.ps1`, `protect-token.ps1`, `diag-token.ps1` | installation de la tâche, chiffrement du jeton, diagnostic du jeton |
| `D:\ci\runner\processed.txt`, `processed-publish.txt` | commits déjà traités, avec leur résultat |
| `D:\ci\runner\logs\ci-int-<date>.log` | journal du jour |
| `D:\ci\runner\builds\<date>-<commit>-<étape>.log` | journal de chaque compilation, package ou push |
| `D:\ci\build` | code du commit en cours, **vidé à chaque commit** ; briques logicielles dans `.nuget` |
| `D:\ci\packages\<version>` | packages fabriqués |

Les scripts du runner sont rangés dans le dépôt, dans ce dossier.

### Tâche planifiée

- Nom `Styx.CiInt`, créée par `install-task.ps1` avec le compte du runner ; toutes les minutes, sans fin.
- Mode « Do not store password » : elle tourne même sans session ouverte et après un redémarrage, sans mot de passe enregistré. En contrepartie, elle n'a pas accès aux partages réseau ; GitLab et Artifactory, eux, fonctionnent par jeton.
- PowerShell lancé sans fenêtre (`conhost.exe --headless`).

### Jeton GitLab

- Jeton de projet, rôle **Maintainer** (pour lire la variable de la clé Artifactory), scope `api`.
- Chiffré par Windows au niveau de la machine ; `protect-token.ps1` en limite la lecture à SYSTEM, aux administrateurs et au compte du runner.

### Réglages (`ci-int.psd1`)

Le fichier est relu à chaque passage : une modification compte dès la minute suivante.

| Réglage | Rôle |
|---|---|
| `GitLabUrl`, `ProjectId` | adresse de GitLab (sans `/` à la fin) et numéro du projet |
| `TokenFile` | fichier du jeton chiffré |
| `RunnerRoot` | dossier du runner |
| `BuildRoot` | dossier du code, vidé à chaque commit (une racine de lecteur est refusée) |
| `BuildScript`, `PackageScript`, `PushScript` | scripts lancés, à partir de la racine du code |
| `BuildEnvironment` | environnement transmis aux scripts (`config\<env>.psd1`) |
| `BuildTimeoutMinutes` | durée au-delà de laquelle un script est arrêté et déclaré en échec |
| `StatusName`, `PublishStatusName` | noms des statuts (`ci-int`, `ci-int-publish`) |
| `PublishBranch` | branche publiée (`main`) |
| `PackageRoot`, `VersionFormat` | dossier des packages, format de version (`1.0.{0}`) |
| `ArtifactoryKeyVariable` | variable GitLab de la clé Artifactory |
| `BuildUsernameVariable`, `BuildTokenVariable` | variables qui transmettent le compte et le jeton de build (`ARTIFACTORY_BUILD_USERNAME`, `ARTIFACTORY_BUILD_TOKEN`), mêmes noms que dans `nuget.config` |

### Limites connues

- Les journaux et les packages ne sont jamais effacés.
- Les briques logicielles sont téléchargées à chaque compilation : une compilation complète prend environ 4 min 30.
- Les accents des journaux de compilation et de package s'affichent mal.
- La fusion d'une merge request reste manuelle.
