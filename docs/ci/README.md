# Runner CI/CD d'intégration (`cicd-int-poc`)

Ce document explique le fonctionnement du runner et son exploitation. Sa mise en place pour un autre projet est décrite dans `HOWTO.md`.

## 1. Rôle

La création de runners GitLab est bloquée sur l'instance : GitLab ne peut rien exécuter. Le runner les remplace par un script PowerShell installé sur la machine d'intégration (int), lancé chaque minute par une tâche planifiée, qui interroge GitLab par son API.

- **CI** : à chaque nouveau commit d'une merge request, la solution est compilée et le résultat s'affiche dans la MR (statut `ci-int`).
- **CD** : à chaque nouveau commit de `main` (après un merge), le package `Styx.Publish` est généré et poussé sur Artifactory (statut `cd-int`). Le déploiement n'est pas fait par le runner.

Les résultats sont visibles dans GitLab comme ceux d'un pipeline : GitLab crée un pipeline « externe » pour chaque commit qui reçoit un statut.

## 2. Déroulé d'un passage

Chaque minute, la tâche lance le script, qui fait un seul passage puis s'arrête. Un verrou empêche deux passages simultanés : si le précédent n'est pas fini (build long), le nouveau s'arrête aussitôt (`Un passage est déjà en cours : rien à faire.`).

### CI : les merge requests

Pour chaque MR ouverte dont le dernier commit n'est pas dans `processed.txt` :

1. publication du statut `ci-int` « en cours » sur le commit ; GitLab crée le pipeline externe ;
2. téléchargement des sources du commit (archive zip générée par GitLab : git n'est pas nécessaire sur la machine) dans `D:\ci\build`, vidé au préalable ;
3. lancement du script de build des sources (`src\scripts\devops\int\build.ps1`) : c'est la version du script livrée avec le commit qui compile ce commit ;
4. dépôt du journal du build dans le projet GitLab ;
5. publication du statut « réussi » ou « échoué », avec la durée, la machine, l'auteur du commit et un lien vers le journal ;
6. en cas d'échec, commentaire dans la MR : commit et auteur, durée, lien vers le journal, erreurs de compilation (50 lignes au plus) ;
7. inscription du commit dans `processed.txt`, avec son résultat.

Un commit n'est construit qu'une fois. Un nouveau commit sur la MR relance un build.

### CD : la branche `main`

Si le dernier commit de `main` n'est pas dans `processed-cd.txt` :

1. vérification, par l'API, que ce commit contient les scripts de package et de push ; sinon (commit antérieur à la mise en place de la CD), il est noté `ignored`, sans statut ni téléchargement ;
2. publication du statut `cd-int` « en cours » ; le numéro du pipeline externe donne la version : `1.0.<pipeline>` ;
3. lecture de la clé Artifactory dans la variable GitLab `ARTIFACTORY_API_KEY` ;
4. téléchargement des sources, puis `src\scripts\devops\int\package.ps1` (compilation en Release et génération du package dans `D:\ci\packages\<version>`) et `src\scripts\devops\int\push-artifactory.ps1` ; la clé est transmise au script de push par l'environnement du processus, jamais par la ligne de commande ni dans un fichier ;
5. dépôt des journaux du package et du push dans GitLab, publication du statut « réussi » (`Version 1.0.<n> publiée sur Artifactory…`) ou « échoué » ;
6. inscription du commit dans `processed-cd.txt`.

Depuis une version publiée, on retrouve son pipeline (`#<n>`) et donc son commit dans GitLab.

### Erreurs

- **Échec du build, du package ou du push** : c'est le résultat du commit. Il est publié dans GitLab, le commit est noté et n'est pas retenté.
- **Erreur du runner** (GitLab injoignable, téléchargement impossible, script de build introuvable, clé illisible) : le statut passe à « échoué » avec le message, mais le commit n'est pas noté : il est repris au passage suivant, une fois la cause corrigée.
- Une erreur de la CI n'empêche pas la CD, et inversement.

## 3. Fichiers et dossiers

| Emplacement | Contenu |
|---|---|
| `D:\ci\runner\cicd-int-poc.ps1` | le runner |
| `D:\ci\runner\cicd-int-poc.psd1` | sa configuration (aucun secret) |
| `D:\ci\runner\gitlab-token.dat` | le jeton GitLab chiffré |
| `D:\ci\runner\install-task.ps1`, `protect-token.ps1`, `diag-token.ps1` | installation de la tâche, chiffrement du jeton, diagnostic du jeton |
| `D:\ci\runner\processed.txt` | commits de MR traités : `<commit>  MR !<n>  <résultat>  <date>` |
| `D:\ci\runner\processed-cd.txt` | commits de `main` traités : `<commit>  main <version>  <résultat>  <date>` |
| `D:\ci\runner\logs\cicd-int-poc-<date>.log` | journal du jour : une ligne par passage au minimum |
| `D:\ci\runner\builds\<date>-<commit>-<étape>.log` | journal de chaque build, package ou push (`.err.log` : sortie d'erreur) |
| `D:\ci\build` | sources du commit en cours, **vidé à chaque commit** |
| `D:\ci\packages\<version>` | packages générés |

Les scripts du runner sont aussi versionnés dans le dépôt (`src\scripts\devops\cicd\`) ; ceux de la machine en sont une copie.

## 4. Tâche planifiée

- Nom : `Styx.CicdIntPoc`, créée par `install-task.ps1`, avec le compte qui lance ce script.
- Fréquence : toutes les minutes, sans fin ; un lancement est ignoré si le précédent tourne encore.
- Mode : « Do not store password » : la tâche tourne que la session soit ouverte ou non, y compris après un redémarrage, sans mot de passe enregistré (une stratégie de l'entreprise interdit de l'enregistrer). Contrepartie : la tâche n'accède pas aux ressources réseau authentifiées par le compte Windows (partages). Les appels à GitLab et à Artifactory, authentifiés par jeton, ne sont pas concernés.
- Privilèges les plus élevés du compte, et PowerShell lancé sans console (`conhost.exe --headless`) : aucune fenêtre ne s'affiche.

## 5. Jeton GitLab

- Jeton de projet `cicd-int-poc-api`, rôle **Maintainer**, scope `api`. Le rôle Maintainer est nécessaire pour lire la variable de la clé Artifactory ; le scope `api` pour publier des statuts, déposer des fichiers et commenter.
- Il est chiffré par Windows (DPAPI) au niveau de la machine, ce qui permet à la tâche de le lire sans session ouverte. Comme tout compte capable de lire le fichier pourrait le déchiffrer, `protect-token.ps1` limite la lecture à SYSTEM, aux administrateurs et au compte du runner.
- Il a une date d'expiration (3 mois) : le renouveler avant (§ 7).

La clé Artifactory n'est pas sur la machine : elle est dans la variable de projet GitLab `ARTIFACTORY_API_KEY` (masquée), et une seule mise à jour suffit à chaque renouvellement.

## 6. Configuration (`cicd-int-poc.psd1`)

| Clé | Rôle |
|---|---|
| `GitLabUrl` | adresse de GitLab, sans `/` final |
| `ProjectId` | identifiant numérique du projet |
| `TokenFile` | fichier du jeton chiffré |
| `RunnerRoot` | dossier du runner (journaux, commits traités) |
| `BuildRoot` | dossier des sources, vidé à chaque commit (une racine de lecteur est refusée) |
| `BuildScript` | script de build, relatif à la racine des sources |
| `BuildEnvironment` | environnement transmis aux scripts du dépôt (`config\<env>.psd1`) |
| `BuildTimeoutMinutes` | au-delà, le build, le package ou le push est arrêté et déclaré en échec |
| `StatusName` | nom du statut des MR (`ci-int`) |
| `CdBranch` | branche publiée (`main`) |
| `CdStatusName` | nom du statut de `main` (`cd-int`) |
| `PackageScript`, `PushScript` | scripts de package et de push, relatifs à la racine des sources |
| `PackageRoot` | dossier des packages générés |
| `VersionFormat` | format de version, `{0}` = numéro du pipeline (`1.0.{0}`) |
| `ArtifactoryKeyVariable` | variable GitLab de la clé Artifactory, même nom que `Artifactory.ApiKeyVariable` dans `config\int.psd1` |

Le fichier est lu à chaque passage : une modification est prise en compte à la minute suivante.

## 7. Exploitation

### Suivre l'activité

- Dans GitLab : statut des MR, **Build > Pipelines**, page d'un commit de `main` ; un clic sur le job (`ci-int` ou `cd-int`) ouvre le journal.
- Sur la machine : le journal du jour, `D:\ci\runner\logs\cicd-int-poc-<date>.log`. Sans activité, une ligne par minute : `Aucune MR à traiter (<n> ouverte(s)), main à jour.`

### Reconstruire ou republier un commit

Supprimer la ligne du commit dans `processed.txt` (MR) ou `processed-cd.txt` (`main`) : il est traité au passage suivant. Pour `main`, uniquement si le push n'a pas abouti : republier une version déjà présente sur Artifactory échoue (conflit 409).

### Arrêter et relancer

```powershell
Disable-ScheduledTask -TaskName 'Styx.CicdIntPoc'
```

```powershell
Enable-ScheduledTask -TaskName 'Styx.CicdIntPoc'
```

Lancer un passage à la main (tâche désactivée, pour voir le déroulé dans la console) :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\cicd-int-poc.ps1
```

### Renouveler le jeton GitLab

1. Dans GitLab, **Settings > Access tokens** : créer un nouveau jeton (mêmes rôle et scope).
2. Sur la machine, avec le compte du runner :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\protect-token.ps1
   ```

   Coller le jeton à l'invite `Jeton GitLab:` (rien ne s'affiche). Résultat attendu : `Jeton chiffré dans D:\ci\runner\gitlab-token.dat`.
3. Vérifier le passage suivant dans le journal, puis révoquer l'ancien jeton.

### Renouveler la clé Artifactory

Modifier la valeur de la variable `ARTIFACTORY_API_KEY` dans **Settings > CI/CD > Variables**. Rien à faire sur la machine.

## 8. Dépannage

| Message (journal ou statut GitLab) | Cause | Action |
|---|---|---|
| `GitLab refuse le jeton (401)` | jeton expiré ou révoqué | renouveler le jeton (§ 7) |
| `GitLab refuse l'accès (403)` | rôle ou scope insuffisant, ou mauvais jeton chiffré | vérifier le jeton dans GitLab ; `diag-token.ps1` affiche le nom et les scopes du jeton chiffré |
| `Impossible de déchiffrer le jeton GitLab` | fichier chiffré sur une autre machine | relancer `protect-token.ps1` sur cette machine |
| `Lecture du jeton GitLab refusée` | la tâche ne tourne pas avec le compte autorisé | relancer `protect-token.ps1` avec le compte du runner, ou `install-task.ps1` |
| `Variable GitLab 'ARTIFACTORY_API_KEY' illisible` | variable absente, ou jeton sans rôle Maintainer | créer la variable ; vérifier le rôle du jeton |
| `Script introuvable dans les sources (Build)` | chemin `BuildScript` erroné, ou script absent du commit | corriger la configuration ou le dépôt ; le commit est repris |
| `commit <x> ignoré, scripts absents du commit` | commit de `main` antérieur aux scripts de publication | aucune : le commit suivant sera publié |
| `Build échoué (code 1)` | erreur de compilation | lire le commentaire de la MR ou le journal du job |
| `Push échoué (code 1)` | clé refusée, ou version déjà présente (409) | lire le journal du push ; renouveler la clé ; republier (§ 7) |
| `… interrompu après 30 min` | build trop long ou bloqué | augmenter `BuildTimeoutMinutes`, ou chercher le blocage dans le journal |
| `Journal non déposé dans GitLab` | dépôt de fichier refusé | sans effet sur le résultat ; le journal reste sur la machine |
| Plus aucune ligne dans le journal du jour | tâche désactivée, ou machine arrêtée | `Get-ScheduledTask -TaskName 'Styx.CicdIntPoc'` ; relancer `install-task.ps1` |
| Une fenêtre s'ouvre chaque minute | tâche créée sans `conhost --headless` | relancer `install-task.ps1` |

## 9. Limites connues

- Les journaux (`logs`, `builds`), les packages (`D:\ci\packages`) et les journaux déposés dans GitLab ne sont pas purgés.
- L'archive des sources pèse environ 580 Mo (paquets NuGet embarqués dans le dépôt, faute d'Artifactory NuGet complet) : environ 1 min 30 de téléchargement et d'extraction par build.
- Les accents du journal de build sont mal encodés.
- La fusion d'une MR reste manuelle ; GitLab propose « Set to auto-merge » sur chaque MR.
