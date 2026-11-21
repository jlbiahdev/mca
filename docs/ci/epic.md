# Epic — Runner CI de dev

**Objectif** : à chaque commit poussé sur une merge request, la solution est compilée sur la machine de POC et le résultat s'affiche dans la MR.

La création de runners GitLab est bloquée sur l'instance. Le runner est donc un script PowerShell (`ci-dev.ps1`), lancé chaque minute par une tâche planifiée, qui interroge GitLab par son API.

**Fini quand** :

- un commit poussé sur une MR est compilé dans les deux minutes, sans intervention ;
- la MR affiche le statut `ci-dev` « en cours », puis « réussi » ou « échoué » ;
- aucun secret n'apparaît dans le dépôt, les scripts, les journaux ni la console.

## Principes

- **Un seul passage par lancement** : la tâche planifiée lance le script chaque minute ; le script fait un passage puis s'arrête. Il redémarre seul après un redémarrage de la machine, et un plantage n'arrête pas la surveillance.
- **Pas de git sur la machine** : les sources d'un commit sont téléchargées en zip par l'API GitLab.
- **Configuration hors du code** : tout ce qui varie d'une machine à l'autre est dans `ci-dev.psd1`, à côté du script. Ce fichier ne contient aucun secret.
- **Même build qu'en local** : le runner lance le script de build des sources téléchargées (`devops\dev\build.ps1 -RepositoryRoot <sources>`). C'est la version du script livrée avec le commit qui compile ce commit.
- **Jeton chiffré** : le jeton GitLab est chiffré par Windows (DPAPI). Seul le compte qui l'a chiffré, sur cette machine, peut le relire : c'est aussi le compte de la tâche planifiée.

## Découpage

Chaque user story ajoute une seule chose à la précédente et se teste seule. Le code de chaque étape (`ci-dev.ps1` et `ci-dev.psd1`) est complet : on remplace les deux fichiers à chaque user story.

| US | Titre | Le script sait… |
|---|---|---|
| 1 | Connexion à GitLab | lire le projet GitLab avec le jeton chiffré |
| 2 | Lancement automatique | tourner chaque minute, écrire un journal, éviter deux passages simultanés |
| 3 | Détection des MR | lister les MR ouvertes et leur dernier commit |
| 4 | Mémoire des commits traités | ne traiter chaque commit qu'une fois |
| 5 | Récupération des sources | télécharger les sources du commit |
| 6 | Build | compiler les sources |
| 7 | Statut dans GitLab | publier « en cours », puis « réussi » ou « échoué » |
| 8 | Build en échec et erreurs du runner | signaler un échec avec son journal, reprendre après une erreur du runner |

Dossiers sur la machine de POC :

| Dossier | Contenu |
|---|---|
| `D:\ci\runner` | `ci-dev.ps1`, `ci-dev.psd1`, jeton chiffré, `processed.txt`, `logs\` (journal du jour), `builds\` (journal de chaque build) |
| `D:\ci\build` | Sources du commit en cours (vidé à chaque commit) |

---

## US 1 — Connexion à GitLab

**Contexte** : avant toute automatisation, vérifier que la machine de POC joint GitLab avec un jeton, et que ce jeton est stocké sans jamais apparaître en clair.

**Code** : `ci-dev.ps1` et `ci-dev.psd1`, étape US 1.

**À faire** (console PowerShell, **connecté avec le compte qui fera tourner la tâche planifiée**) :

1. Dans GitLab, projet > Settings > Access tokens > **Add new token** :
   - nom : `ci-dev-poc` ;
   - rôle : **Developer** ;
   - scopes : `read_api` et `read_repository` ;
   - expiration : selon la règle de l'entreprise (noter la date).

   Copier le jeton affiché : il ne sera plus montré.

2. Créer les dossiers :

   ```powershell
   New-Item -ItemType Directory -Path D:\ci\runner, D:\ci\build -Force
   ```

   Résultat attendu : les deux dossiers sont listés.

3. Chiffrer le jeton :

   ```powershell
   Read-Host "Jeton GitLab" -AsSecureString | Export-Clixml -Path D:\ci\runner\gitlab-token.xml
   ```

   Coller le jeton à l'invite (rien ne s'affiche), puis Entrée. Résultat attendu : aucun message, et le fichier `D:\ci\runner\gitlab-token.xml` existe.

4. Copier `ci-dev.ps1` et `ci-dev.psd1` dans `D:\ci\runner`, en UTF-8 avec BOM. Dans `ci-dev.psd1`, renseigner `GitLabUrl` et `ProjectId`.

5. Lancer le script :

   ```powershell
   D:\ci\runner\ci-dev.ps1
   ```

   Résultat attendu : `Connexion GitLab OK : projet « <groupe>/<projet> » (<adresse>)`.

**Fini quand** :

- le script affiche `Connexion GitLab OK` ;
- avec un jeton faux, il affiche `GitLab refuse le jeton (401)` ;
- le jeton n'apparaît dans aucun fichier en clair ni dans l'historique de la console (`Get-History`).

**Dépendances** : aucune.

## US 2 — Lancement automatique

**Contexte** : le script doit passer chaque minute sans intervention, et laisser une trace de chaque passage.

**Code** : étape US 2. Ajouts : journal du jour (`D:\ci\runner\logs\ci-dev-AAAAMMJJ.log`) et verrou (`ci-dev.lock`) qui empêche deux passages simultanés.

**À faire** (console PowerShell **administrateur**, avec le compte de l'US 1) :

1. Remplacer `ci-dev.ps1` et `ci-dev.psd1` dans `D:\ci\runner` (reprendre `GitLabUrl` et `ProjectId`).

2. Préparer l'action :

   ```powershell
   $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "D:\ci\runner\ci-dev.ps1"' -WorkingDirectory 'D:\ci\runner'
   ```

3. Préparer le déclenchement, chaque minute :

   ```powershell
   $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 1)
   ```

4. Préparer les réglages (pas de second lancement si le précédent tourne encore, arrêt au bout de 2 heures) :

   ```powershell
   $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
   ```

5. Créer la tâche, avec le compte de l'US 1 (son mot de passe est demandé ; la tâche tourne même sans session ouverte) :

   ```powershell
   Register-ScheduledTask -TaskName 'Styx.CiDev' -Action $action -Trigger $trigger -Settings $settings -User "$env:USERDOMAIN\$env:USERNAME" -Password ([Net.NetworkCredential]::new('', (Read-Host 'Mot de passe du compte' -AsSecureString)).Password)
   ```

   Résultat attendu : la tâche `Styx.CiDev` est listée, à l'état `Ready`.

6. Après deux minutes, vérifier les lancements :

   ```powershell
   Get-ScheduledTaskInfo -TaskName 'Styx.CiDev'
   ```

   Résultat attendu : `LastRunTime` date de moins d'une minute, `LastTaskResult` vaut `0`.

7. Lire le journal du jour :

   ```powershell
   Get-Content "D:\ci\runner\logs\ci-dev-$(Get-Date -Format yyyyMMdd).log" -Tail 3
   ```

   Résultat attendu : une ligne `Passage du runner sur <machine> (<compte>) : connexion GitLab OK` par minute.

**Fini quand** :

- la tâche se lance chaque minute (`LastTaskResult` = `0`) ;
- le journal contient une ligne par passage ;
- un second lancement à la main pendant un passage affiche `Un passage est déjà en cours : rien à faire.`

**Attention** : la tâche garde le mot de passe du compte. S'il change, la tâche échoue : la recréer (étape 5). Le jeton chiffré ne dépend pas du mot de passe.

**Dépendances** : US 1.

## US 3 — Détection des MR

**Contexte** : vérifier que le runner voit les merge requests ouvertes et leur dernier commit, sans encore rien télécharger.

**Code** : étape US 3. Ajout : liste des MR ouvertes (`Get-OpenMergeRequests`).

**À faire** :

1. Remplacer `ci-dev.ps1` et `ci-dev.psd1`.
2. Créer une branche de test, y pousser un commit, puis créer une MR depuis cette branche.
3. Attendre une minute, puis lire le journal du jour.

**Fini quand** :

- sans MR ouverte, le journal indique `Aucune MR ouverte.` ;
- avec la MR de test, le journal indique `MR !<numéro> « <titre> » (<branche>) : commit <commit>` à chaque passage ;
- après un nouveau push sur la branche, le commit affiché change au passage suivant.

**Dépendances** : US 2.

## US 4 — Mémoire des commits traités

**Contexte** : un commit ne doit être traité qu'une fois, même si la MR reste ouverte des jours.

**Code** : étape US 4. Ajout : fichier `D:\ci\runner\processed.txt`, une ligne par commit traité (`<commit>`, `MR !<numéro>`, date).

**À faire** :

1. Remplacer `ci-dev.ps1` et `ci-dev.psd1`.
2. Avec la MR de test de l'US 3 ouverte, attendre deux passages.
3. Pousser un nouveau commit sur la branche de la MR, puis attendre un passage.

**Fini quand** :

- au premier passage, le journal indique le commit puis `commit noté comme traité`, et `processed.txt` contient le commit ;
- aux passages suivants, le journal indique `Aucune MR à traiter (1 ouverte(s)).` ;
- le nouveau commit est traité au passage suivant le push, et seulement lui.

**Pour retraiter un commit** : supprimer sa ligne dans `processed.txt`.

**Dépendances** : US 3.

## US 5 — Récupération des sources

**Contexte** : le runner télécharge les sources du commit, sans git : GitLab génère un zip du commit à la demande.

**Code** : étape US 5. Ajouts : clé `BuildRoot` dans `ci-dev.psd1`, téléchargement et extraction (`Save-CommitSources`). Le commit n'est noté comme traité qu'une fois les sources déposées.

**À faire** :

1. Remplacer `ci-dev.ps1` et `ci-dev.psd1`.
2. Pousser un nouveau commit sur la branche de la MR de test, puis attendre un passage.

**Fini quand** :

- le journal indique `Téléchargement des sources du commit <commit>`, la taille de l'archive, puis `Sources déposées dans D:\ci\build` ;
- `D:\ci\build` contient la racine du dépôt (dont `src` et `devops`), au contenu du commit ;
- en cas d'échec du téléchargement, le commit n'est pas noté dans `processed.txt` et il est repris au passage suivant.

**Attention** : `D:\ci\build` est vidé à chaque commit. Le script refuse une racine de lecteur (`D:\`).

**Dépendances** : US 4.

## US 6 — Build

**Contexte** : le runner compile les sources avec le même script qu'en local : `devops\dev\build.ps1 -RepositoryRoot D:\ci\build`, en Release, avec l'environnement `dev`.

**Code** : étape US 6. Ajouts : clés `BuildScript`, `BuildConfiguration`, `BuildEnvironment` et `BuildTimeoutMinutes` dans `ci-dev.psd1` ; lancement du build (`Invoke-CiBuild`), dont la sortie va dans `D:\ci\runner\builds\<date>-<commit>.log`. Le commit est noté dans `processed.txt` avec son résultat (`success` ou `failed`).

**Prérequis** :

- le SDK .NET 8 est installé : `dotnet --list-sdks` affiche une ligne `8.0.xxx` ;
- `dotnet restore` fonctionne sur la machine (sources NuGet accessibles depuis le compte de la tâche) ;
- les scripts `devops` sont dans le dépôt, à l'emplacement indiqué par `BuildScript`.

**À faire** :

1. Remplacer `ci-dev.ps1` et `ci-dev.psd1`.
2. Pousser sur la branche de la MR de test un commit **qui compile**, puis attendre la fin du build.

**Fini quand** :

- le journal indique le script de build lancé, le chemin du journal du build, puis `MR !<numéro> : Build réussi` ;
- le journal du build contient la sortie de `build.ps1` ;
- `processed.txt` contient le commit avec `success` ;
- un build qui dépasse `BuildTimeoutMinutes` est arrêté et déclaré en échec.

**Dépendances** : US 5.

## US 7 — Statut dans GitLab

**Contexte** : le résultat du build doit s'afficher dans la MR. Le runner publie le statut du commit par l'API Commit Status. GitLab range ces statuts dans un pipeline **externe**, qui a un identifiant : cette US vérifie aussi ce point, dont dépend le format de version du runner-cd (US 4.2 de l'Epic CI/CD).

**Code** : étape US 7. Ajouts : clé `StatusName` dans `ci-dev.psd1` ; publication du statut (`Set-CommitStatus`) : « en cours » avant le téléchargement, puis « réussi » ou « échoué ». Si la réponse de GitLab ne contient pas l'identifiant du pipeline, le script le lit sur le commit.

**À faire** :

1. Créer un nouveau jeton avec le scope `api` (publication des statuts), rôle Developer.
2. Le chiffrer à la place de l'ancien (même commande qu'à l'US 1, étape 3), puis révoquer l'ancien jeton dans GitLab.
3. Remplacer `ci-dev.ps1` et `ci-dev.psd1`.
4. Pousser un commit qui compile sur la branche de la MR de test.

**Fini quand** :

- pendant le build, la MR affiche le statut `ci-dev` « en cours », puis « réussi » ;
- le journal indique `Pipeline externe GitLab : <identifiant>` ;
- le pipeline apparaît dans Build > Pipelines, avec le statut `ci-dev` ;
- le statut « en cours » et le statut final sont dans le même pipeline ;
- le résultat (identifiant renvoyé ou non, pipeline visible ou non) est noté dans ce ticket et reporté dans l'US 4.2.

**Dépendances** : US 6.

## US 8 — Build en échec et erreurs du runner

**Contexte** : un build en échec doit être signalé dans la MR avec de quoi le diagnostiquer. Une erreur du runner (GitLab injoignable, téléchargement impossible) ne doit pas faire perdre le commit.

**Code** : étape US 8 (version finale). Ajouts :

- la description du statut indique le résultat, la durée, la machine et le chemin du journal du build ;
- en cas d'erreur du runner, le statut passe à « échoué » avec le message d'erreur, et le commit n'est pas noté comme traité : il est repris au passage suivant.

**À faire** :

1. Remplacer `ci-dev.ps1` et `ci-dev.psd1`.
2. Pousser sur la branche de la MR de test un code **qui ne compile pas**.
3. Vérifier dans Settings > Merge requests > Merge checks si « Pipelines must succeed » bloque le merge sur ce statut.
4. Corriger le code et pousser.

**Fini quand** :

- la MR affiche `ci-dev` « échoué », avec le chemin du journal du build dans la description ;
- le journal du build contient l'erreur de compilation ;
- après correction, le statut repasse à « réussi » ;
- on sait si GitLab bloque le merge sur ce statut (noté dans ce ticket).

**Limite connue** : une erreur du runner qui persiste (jeton expiré, par exemple) est retentée à chaque passage, et le statut « échoué » est republié chaque minute jusqu'à sa correction.

**Dépendances** : US 7.

---

## Hors périmètre

- **Runner-cd** : package et publication sur Artifactory après un merge dans `main` (Epic CI/CD, US 4.1).
- **Nettoyage** des journaux (`logs`, `builds`) : un fichier par jour et un par build, à purger plus tard si besoin.
- **Tests unitaires** dans le build, quand ils existeront.
