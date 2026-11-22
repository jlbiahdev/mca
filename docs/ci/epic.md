# Epic — Runner CI de dev

**Objectif** : à chaque commit poussé sur une merge request, la solution est compilée sur la machine de POC et le résultat s'affiche dans la MR.

La création de runners GitLab est bloquée sur l'instance. Le runner est donc un script PowerShell (`ci-dev.ps1`), lancé chaque minute par une tâche planifiée, qui interroge GitLab par son API.

**Fini quand** :

- un commit poussé sur une MR est compilé dans les deux minutes, sans intervention ;
- la MR affiche le statut `ci-dev` « en cours », puis « réussi » ou « échoué » ;
- aucun secret n'apparaît dans le dépôt, les scripts, les journaux ni la console ;
- le fonctionnement est expliqué (README) et la mise en place est décrite pour une autre équipe (How-to), rejouée de bout en bout (US 9).

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
| 9 | Documentation | — (README : fonctionnement ; How-to : mise en place pour une autre équipe) |
| 10 | Publication sur Artifactory (CD) | packager et publier chaque nouveau commit de `main`, sous le nom `cicd-int-poc` (réalisée avant l'US 9) |

Dossiers sur la machine de POC :

| Dossier | Contenu |
|---|---|
| `D:\ci\runner` | `ci-dev.ps1`, `ci-dev.psd1`, `install-task.ps1`, jeton chiffré, `processed.txt`, `logs\` (journal du jour), `builds\` (journal de chaque build) |
| `D:\ci\build` | Sources du commit en cours (vidé à chaque commit) |

## Mettre à jour le code à chaque US

Cette procédure sert à toutes les US.

1. Ouvrir dans un éditeur le fichier `ci-dev.psd1` de l'US, copier son contenu dans `D:\ci\runner\ci-dev.psd1`, et y reporter `GitLabUrl` et `ProjectId` (relevés à l'US 1).
2. Faire de même pour `D:\ci\runner\ci-dev.ps1`.
3. Enregistrer les deux fichiers en UTF-8 **avec BOM**, sinon Windows PowerShell 5.1 affiche mal les accents et peut refuser le script :

   ```powershell
   foreach ($f in 'D:\ci\runner\ci-dev.ps1', 'D:\ci\runner\ci-dev.psd1') { Set-Content -LiteralPath $f -Value (Get-Content -LiteralPath $f -Raw -Encoding UTF8) -Encoding UTF8 -NoNewline }
   ```

   Résultat attendu : aucun message. En Windows PowerShell 5.1, `-Encoding UTF8` lit le fichier en UTF-8 et l'écrit avec le BOM.

4. Vérifier la configuration :

   ```powershell
   Import-PowerShellDataFile D:\ci\runner\ci-dev.psd1
   ```

   Résultat attendu : la liste des clés et de leurs valeurs, sans erreur.

---

## US 1 — Connexion à GitLab

**Contexte** : avant toute automatisation, vérifier que la machine de POC joint GitLab avec un jeton, et que ce jeton est stocké sans jamais apparaître en clair.

**Code** : `ci-dev.ps1` et `ci-dev.psd1`, étape US 1.

**Prérequis** : rôle **Maintainer** sur le projet dans GitLab (nécessaire pour créer un jeton de projet).

### Dans GitLab

1. Ouvrir le projet dans GitLab.
2. Relever **l'adresse de GitLab** : le début de l'adresse dans le navigateur, sans le chemin du projet ni `/` final. Exemple : `https://gitlab.exemple.fr`.
3. Relever **l'identifiant du projet** : sur la page d'accueil du projet, menu **⋮** (en haut à droite) > **Copy project ID**. C'est un nombre.
4. Dans le menu de gauche : **Settings > Access tokens**, puis **Add new token**.
5. Remplir le formulaire :

   | Champ | Valeur |
   |---|---|
   | Token name | `ci-dev-poc` |
   | Token description | `Runner CI de dev (machine de POC)` |
   | Expiration date | Selon la règle de l'entreprise. Noter la date : il faudra renouveler le jeton avant. |
   | Select a role | **Developer** |
   | Select scopes | `read_api` et `read_repository` |

6. Cliquer sur **Create project access token**.
7. Copier le jeton avec l'icône de copie, à côté du champ **Your new project access token**. Il ne sera plus jamais affiché : le garder dans le presse-papiers jusqu'à l'étape 4 de la partie suivante, sans le coller ailleurs.

Résultat attendu : le jeton `ci-dev-poc` apparaît dans la liste **Active project access tokens**, avec le rôle Developer et les deux scopes.

**Si le menu Access tokens n'existe pas**, ou si la création est refusée : l'instance interdit peut-être les jetons de projet. Le signaler avant d'aller plus loin. Solution de repli à valider : un jeton personnel (avatar > **Edit profile > Access tokens**), avec les mêmes scopes ; les actions du runner apparaîtront alors au nom de son propriétaire.

### Sur la machine de POC

Console PowerShell, **connecté avec le compte qui fera tourner la tâche planifiée** (le jeton chiffré ne sera lisible que par ce compte).

1. Vérifier la version de PowerShell :

   ```powershell
   $PSVersionTable.PSVersion
   ```

   Résultat attendu : `Major` vaut `5`, `Minor` vaut `1`.

2. Vérifier l'accès réseau à GitLab (remplacer `<serveur>` par le nom du serveur, sans `https://`) :

   ```powershell
   Test-NetConnection <serveur> -Port 443
   ```

   Résultat attendu : `TcpTestSucceeded : True`. Sinon, un proxy ou un pare-feu bloque : le signaler.

3. Créer les dossiers :

   ```powershell
   New-Item -ItemType Directory -Path D:\ci\runner, D:\ci\build -Force
   ```

   Résultat attendu : les deux dossiers sont listés.

4. Chiffrer le jeton :

   ```powershell
   Read-Host "Jeton GitLab" -AsSecureString | Export-Clixml -Path D:\ci\runner\gitlab-token.xml
   ```

   Coller le jeton à l'invite (rien ne s'affiche), puis Entrée. Résultat attendu : aucun message, et le fichier `D:\ci\runner\gitlab-token.xml` existe. Vider ensuite le presse-papiers (copier n'importe quel autre texte).

5. Installer le code de l'US 1 (voir « Mettre à jour le code à chaque US »), avec l'adresse et l'identifiant relevés dans GitLab.

6. Lancer le script :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\ci-dev.ps1
   ```

   Résultat attendu : `Connexion GitLab OK : projet « <groupe>/<projet> » (<adresse>)`.

### Vérifications

1. Ouvrir `D:\ci\runner\gitlab-token.xml` dans le Bloc-notes : le contenu est chiffré (une longue suite de chiffres et de lettres), le jeton n'y figure pas en clair.
2. Avec une erreur volontaire dans `ProjectId` (par exemple `999999`), le script affiche `Ressource GitLab introuvable (404)`. Remettre la bonne valeur.

**Fini quand** :

- le script affiche `Connexion GitLab OK` ;
- le jeton n'apparaît en clair dans aucun fichier ni dans l'historique de la console (`Get-History`) ;
- l'adresse, l'identifiant du projet et la date d'expiration du jeton sont notés pour la documentation.

**Dépendances** : aucune.

## US 2 — Lancement automatique

**Contexte** : le script doit passer chaque minute sans intervention, et laisser une trace de chaque passage.

**Code** : étape US 2. Ajouts :

- dans `ci-dev.ps1` : journal du jour (`D:\ci\runner\logs\ci-dev-AAAAMMJJ.log`) et verrou (`ci-dev.lock`) qui empêche deux passages simultanés ;
- nouveau script `install-task.ps1` : crée, ou remplace, la tâche planifiée `Styx.CiDev`. La tâche tourne avec les privilèges les plus élevés du compte (un compte administrateur garde ses droits sur `D:\ci`, créé depuis une console administrateur), sans fenêtre (PowerShell est lancé par `conhost.exe --headless`).

**Deux modes d'exécution** :

| Mode | Commande | La tâche tourne… | Mot de passe |
|---|---|---|---|
| Par défaut | `install-task.ps1` | quand la session du compte est ouverte, même verrouillée ; elle s'arrête à la déconnexion ou au redémarrage, jusqu'à la connexion suivante | non enregistré |
| Session fermée | `install-task.ps1 -RunWhenLoggedOff` | tout le temps | enregistré par Windows ; une stratégie de l'entreprise peut l'interdire (erreur `0x80070520`) |

### Dans GitLab

Rien à faire.

### Sur la machine de POC

Console PowerShell ouverte avec le compte de l'US 1 (celui qui a chiffré le jeton).

1. Installer le code de l'US 2 (voir « Mettre à jour le code à chaque US »), et copier `install-task.ps1` dans `D:\ci\runner` de la même façon.

2. Lancer le runner une fois à la main :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\ci-dev.ps1
   ```

   Résultat attendu : `Passage du runner sur <machine> (<compte>) : connexion GitLab OK, projet « <groupe>/<projet> ».`

3. Créer la tâche :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\install-task.ps1
   ```

   Résultat attendu :

   ```text
   Tâche Styx.CiDev créée, état : Ready
     Compte    : <DOMAINE>\<utilisateur>
     Exécution : seulement quand la session de ce compte est ouverte (même verrouillée)
     Script    : D:\ci\runner\ci-dev.ps1
     Fréquence : toutes les 1 minute(s)
   ```

   Pour le mode session fermée, ajouter `-RunWhenLoggedOff` : le mot de passe du compte est demandé. En cas d'erreur `0x80070520`, la stratégie de l'entreprise l'interdit : garder le mode par défaut, ou demander un compte de service autorisé.

### Vérifications

1. Après deux minutes :

   ```powershell
   Get-ScheduledTaskInfo -TaskName 'Styx.CiDev'
   ```

   Résultat attendu : `LastRunTime` date de moins d'une minute, `LastTaskResult` vaut `0`.

2. Vérifier que la répétition est sans fin :

   ```powershell
   (Get-ScheduledTask -TaskName 'Styx.CiDev').Triggers.Repetition
   ```

   Résultat attendu : `Interval` vaut `PT1M` et `Duration` est vide.

3. Lire le journal du jour :

   ```powershell
   Get-Content "D:\ci\runner\logs\ci-dev-$(Get-Date -Format yyyyMMdd).log" -Tail 3
   ```

   Résultat attendu : une ligne `Passage du runner…` par minute.

**En cas d'échec** :

- `LastTaskResult` vaut `267011` : la tâche ne s'est pas encore lancée, attendre une minute ;
- `LastTaskResult` signale un refus d'ouverture de session (mode `-RunWhenLoggedOff`) : le compte n'a pas le droit **Ouvrir une session en tant que tâche** sur la machine ; ce droit est accordé par un administrateur ;
- `LastTaskResult` vaut `1` : le script a rencontré une erreur, voir le journal du jour ;
- le journal ne contient pas les passages de la tâche : lancer le script à la main dans une console **non administrateur** pour voir l'erreur (par exemple `Verrou impossible à créer … Access … denied` : la tâche n'a pas les droits d'écriture sur `D:\ci\runner` ; recréer la tâche avec `install-task.ps1`, qui la lance avec les privilèges les plus élevés).

**Pour arrêter le runner** : `Disable-ScheduledTask -TaskName 'Styx.CiDev'` ; pour le relancer : `Enable-ScheduledTask -TaskName 'Styx.CiDev'`. Pour changer de mode, relancer `install-task.ps1` : la tâche est remplacée.

**Fini quand** :

- la tâche se lance chaque minute (`LastTaskResult` = `0`) ;
- aucune fenêtre ne s'affiche lors des passages ;
- le journal contient une ligne par passage ;
- le mode retenu (par défaut ou session fermée) est noté dans ce ticket ;
- un lancement à la main pendant qu'un passage tourne affiche `Un passage est déjà en cours : rien à faire.` (difficile à provoquer à cette étape, où un passage dure moins d'une seconde : à revérifier pendant un build, à l'US 6).

**Attention** : en mode session fermée, la tâche garde le mot de passe du compte. S'il change, la tâche échoue : relancer `install-task.ps1 -RunWhenLoggedOff`. Le jeton chiffré ne dépend pas du mot de passe.

**Dépendances** : US 1.

## US 3 — Détection des MR

**Contexte** : vérifier que le runner voit les merge requests ouvertes et leur dernier commit, sans encore rien télécharger.

**Code** : étape US 3. Ajout : liste des MR ouvertes (`Get-OpenMergeRequests`).

### Dans GitLab

1. Créer une branche de test : **Code > Branches > New branch**, nom `test/ci-dev`, créée depuis `main`.
2. Modifier un fichier sans effet sur le build (par exemple le `README.md` à la racine) : ouvrir le fichier sur la branche `test/ci-dev`, **Edit > Edit single file**, ajouter une ligne, puis **Commit changes** sur la branche `test/ci-dev`.
3. Créer la MR : **Code > Merge requests > New merge request**, source `test/ci-dev`, cible `main`, **Compare branches and continue**.
4. Cocher **Mark as draft** (personne ne doit la fusionner), puis **Create merge request**.

Résultat attendu : la MR est ouverte, marquée « Draft ». Noter son numéro (`!<numéro>`).

### Sur la machine de POC

1. Installer le code de l'US 3 (voir « Mettre à jour le code à chaque US »).
2. Attendre une minute.

### Vérifications

1. Lire le journal du jour :

   ```powershell
   Get-Content "D:\ci\runner\logs\ci-dev-$(Get-Date -Format yyyyMMdd).log" -Tail 5
   ```

   Résultat attendu : `1 MR ouverte(s).` puis `MR !<numéro> « <titre> » (test/ci-dev) : commit <commit>`. S'il y a d'autres MR ouvertes dans le projet, elles apparaissent aussi.

2. Dans GitLab, sur la branche `test/ci-dev`, faire un second commit (même méthode qu'à l'étape 2). Après une minute, le commit affiché dans le journal a changé : c'est celui de la page **Commits** de la MR.

**Fini quand** :

- la MR de test et son dernier commit apparaissent dans le journal à chaque passage ;
- après un nouveau commit, le commit affiché change au passage suivant ;
- sans MR ouverte, le journal indique `Aucune MR ouverte.`

**Dépendances** : US 2.

## US 4 — Mémoire des commits traités

**Contexte** : un commit ne doit être traité qu'une fois, même si la MR reste ouverte des jours.

**Code** : étape US 4. Ajout : fichier `D:\ci\runner\processed.txt`, une ligne par commit traité (`<commit>`, `MR !<numéro>`, date).

### Dans GitLab

Garder la MR de test de l'US 3 ouverte.

### Sur la machine de POC

1. Installer le code de l'US 4 (voir « Mettre à jour le code à chaque US »).
2. Attendre deux minutes.

### Vérifications

1. Lire le journal du jour (`Get-Content … -Tail 5`, comme à l'US 3).

   Résultat attendu : au premier passage, le commit puis `MR !<numéro> : commit noté comme traité.` ; aux passages suivants, `Aucune MR à traiter (1 ouverte(s)).`

2. Lire le fichier des commits traités :

   ```powershell
   Get-Content D:\ci\runner\processed.txt
   ```

   Résultat attendu : une ligne avec le commit, `MR !<numéro>` et la date.

3. Dans GitLab, faire un nouveau commit sur `test/ci-dev`. Après une minute, le journal indique ce commit, et `processed.txt` a une ligne de plus.

**Fini quand** :

- chaque commit n'est traité qu'une fois ;
- un nouveau commit est traité au passage suivant, et seulement lui.

**Pour retraiter un commit** : supprimer sa ligne dans `processed.txt`.

**Dépendances** : US 3.

## US 5 — Récupération des sources

**Contexte** : le runner télécharge les sources du commit, sans git : GitLab génère un zip du commit à la demande.

**Code** : étape US 5. Ajouts : clé `BuildRoot` dans `ci-dev.psd1`, téléchargement et extraction (`Save-CommitSources`). Le commit n'est noté comme traité qu'une fois les sources déposées.

### Dans GitLab

Rien à faire : le scope `read_repository` du jeton (US 1) autorise le téléchargement.

### Sur la machine de POC

Console PowerShell **administrateur**, ouverte avec le compte du runner.

1. Installer le code de l'US 5 (voir « Mettre à jour le code à chaque US »), ainsi que `protect-token.ps1`, `diag-token.ps1` et le nouvel `install-task.ps1` (voir le commentaire de l'US 4 : jeton chiffré au niveau de la machine, tâche sans session ni mot de passe).

2. Rechiffrer le jeton au niveau de la machine, à partir du fichier de l'US 1, sans le recoller :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\protect-token.ps1 -FromClixml D:\ci\runner\gitlab-token.xml
   ```

   Résultat attendu : `Jeton chiffré dans D:\ci\runner\gitlab-token.dat`, puis `Lecture autorisée : SYSTEM, Administrateurs, <DOMAINE>\<compte>`.

3. Vérifier que le fichier contient le bon jeton :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\diag-token.ps1
   ```

   Résultat attendu : `gitlab-token.dat` et `gitlab-token.xml` ont la même empreinte, avec le nom `ci-dev-poc`.

4. Recréer la tâche :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\install-task.ps1
   ```

   Résultat attendu : `Exécution : session ouverte ou fermée, sans mot de passe enregistré`.

5. Supprimer `D:\ci\runner\gitlab-token.xml`, qui ne sert plus.

6. Dans GitLab, faire un nouveau commit sur `test/ci-dev`, puis attendre une minute.

### Vérifications

1. Lire le journal du jour.

   Résultat attendu : `Téléchargement des sources du commit <commit>`, `Archive reçue : <taille> Mo`, puis `Sources déposées dans D:\ci\build`.

2. Lister les sources :

   ```powershell
   Get-ChildItem D:\ci\build
   ```

   Résultat attendu : la racine du dépôt, dont les dossiers `src` et `devops`.

3. Vérifier que le script de build est là où le runner le cherchera à l'US 6 :

   ```powershell
   Test-Path D:\ci\build\devops\dev\build.ps1
   ```

   Résultat attendu : `True`. Sinon, noter l'emplacement réel : il faudra l'indiquer dans `BuildScript` à l'US 6.

**Fini quand** :

- les sources du dernier commit de la MR sont dans `D:\ci\build` ;
- le commit est noté dans `processed.txt` après le dépôt des sources ;
- en cas d'échec du téléchargement, le commit n'est pas noté et il est repris au passage suivant.

**Attention** : `D:\ci\build` est vidé à chaque commit. Le script refuse une racine de lecteur (`D:\`).

**Dépendances** : US 4.

## US 6 — Build

**Contexte** : le runner compile les sources avec un script dédié à l'intégration, `src\scripts\devops\int\build.ps1`, qui s'appuie sur le même module que les scripts de dev (`StyxBuild.psm1`). La machine de POC n'a pas git : le script reçoit la racine des sources (`-RepositoryRoot`, obligatoire). Il compile toujours dans la configuration de l'environnement `int` (`src\scripts\devops\config\int.psd1`, `DotNet.Configuration = 'Release'`) : pas de Debug en intégration. Le runner lance `int\build.ps1 -RepositoryRoot D:\ci\build -Environment int`.

**Code** : étape US 6. Ajouts : clés `BuildScript`, `BuildEnvironment` et `BuildTimeoutMinutes` dans `ci-dev.psd1` ; lancement du build (`Invoke-CiBuild`), dont la sortie va dans `D:\ci\runner\builds\<date>-<commit>.log`. Le commit est noté dans `processed.txt` avec son résultat (`success` ou `failed`).

### Dans GitLab

Ajouter au dépôt, par une MR (branche de travail, puis merge dans `main`) :

| Fichier du dépôt | Contenu |
|---|---|
| `src/scripts/devops/int/build.ps1` | build d'intégration : `-RepositoryRoot` obligatoire, sans git, configuration de l'environnement (Release) |
| `src/scripts/devops/config/int.psd1` | configuration de la machine de POC (valeurs `XXXXX` à renseigner) |

Les fichiers sont dans `us-6-scripts.md`.

Rebaser ensuite la branche de la MR de test (`test/ci-dev`) sur `main`, pour que ses commits contiennent ces scripts.

### Sur la machine de POC

Console PowerShell ouverte avec le compte de la tâche.

1. Vérifier le SDK .NET :

   ```powershell
   dotnet --list-sdks
   ```

   Résultat attendu : une ligne `8.0.xxx`.

2. Compiler une fois à la main les sources déposées à l'US 5, pour vérifier la restauration NuGet et le build avec ce compte :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\build\src\scripts\devops\int\build.ps1 -RepositoryRoot D:\ci\build
   ```

   Résultat attendu : le build se termine par un message de succès. En cas d'échec de la restauration (sources NuGet inaccessibles), le signaler : le runner échouerait de la même façon.

3. Installer le code de l'US 6 (voir « Mettre à jour le code à chaque US ») ; si l'US 5 a montré un autre emplacement du script de build, le reporter dans `BuildScript`.

4. Dans GitLab, faire sur `test/ci-dev` un commit **qui compile**, puis attendre la fin du build (quelques minutes).

### Vérifications

1. Lire le journal du jour.

   Résultat attendu : `Build : D:\ci\build\src\scripts\devops\int\build.ps1 (environnement int)`, `Journal du build : D:\ci\runner\builds\<date>-<commit>.log`, puis `MR !<numéro> : Build réussi`.

2. Lire la fin du journal du build (chemin affiché dans le journal du jour) :

   ```powershell
   Get-ChildItem D:\ci\runner\builds\*.log | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content -Tail 20
   ```

   Résultat attendu : la sortie de `build.ps1`, terminée par le message de succès.

3. Pendant un build, lancer le script à la main : il affiche `Un passage est déjà en cours : rien à faire.` (vérification du verrou, US 2).

4. Lire `processed.txt` : la dernière ligne contient le commit et `success`.

**Fini quand** :

- un commit qui compile donne `Build réussi` dans le journal et `success` dans `processed.txt` ;
- le journal du build contient la sortie complète de `build.ps1` ;
- un build qui dépasse `BuildTimeoutMinutes` est arrêté et déclaré en échec.

**Dépendances** : US 5.

## US 7 — Statut dans GitLab

**Contexte** : le résultat du build doit s'afficher dans la MR. Le runner publie le statut du commit par l'API Commit Status. GitLab range ces statuts dans un pipeline **externe**, qui a un identifiant : cette US vérifie aussi ce point, dont dépend le format de version du runner-cd (US 4.2 de l'Epic CI/CD).

**Code** : étape US 7. Ajouts : clé `StatusName` dans `ci-dev.psd1` ; publication du statut (`Set-CommitStatus`) : « en cours » avant le téléchargement, puis « réussi » ou « échoué ». Si la réponse de GitLab ne contient pas l'identifiant du pipeline, le script le lit sur le commit.

### Dans GitLab

La publication d'un statut demande le scope `api`, que le jeton de l'US 1 n'a pas : il faut un nouveau jeton.

1. **Settings > Access tokens > Add new token**, avec les mêmes valeurs qu'à l'US 1, sauf :

   | Champ | Valeur |
   |---|---|
   | Token name | `ci-int-poc-api` |
   | Select a role | **Developer** (le minimum pour publier un statut : Reporter ne suffit pas) |
   | Select scopes | `api` |

2. **Create project access token**, puis copier le jeton (il ne sera plus affiché).

### Sur la machine de POC

1. Chiffrer le nouveau jeton à la place de l'ancien, avec le compte du runner (`protect-token.ps1`, livré à l'US 5) :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\protect-token.ps1
   ```

   Attendre l'invite `Jeton GitLab:`, y coller le jeton (rien ne s'affiche), puis Entrée. Résultat attendu : `Jeton chiffré dans D:\ci\runner\gitlab-token.dat`.

2. Installer le code de l'US 7 (voir « Mettre à jour le code à chaque US »).

3. Lancer le script une fois à la main, sans nouveau commit :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\ci-dev.ps1
   ```

   Résultat attendu : `Aucune MR à traiter (1 ouverte(s)).` : le nouveau jeton fonctionne.

### Retour dans GitLab

1. Révoquer l'ancien jeton : **Settings > Access tokens**, ligne `ci-dev-poc`, **Revoke**.
2. Faire sur `test/ci-dev` un commit qui compile.

### Vérifications

1. Pendant le build, la page de la MR affiche un pipeline en cours, avec le statut `ci-dev`. À la fin, il passe à « réussi » (coche verte).
2. Le journal du jour indique `Pipeline externe GitLab : <identifiant>`.
3. **Build > Pipelines** : le pipeline apparaît, avec ce même identifiant ; en l'ouvrant, on voit le job `ci-dev`.
4. Le statut « en cours » et le statut final sont dans le même pipeline (un seul pipeline pour le commit).

**Fini quand** :

- la MR affiche `ci-dev` « en cours », puis « réussi » ;
- le résultat du test est noté dans ce ticket et reporté dans l'US 4.2 : identifiant renvoyé ou non, pipeline visible ou non, un ou plusieurs pipelines par commit ;
- l'ancien jeton est révoqué.

**Dépendances** : US 6.

## US 8 — Build en échec et erreurs du runner

**Contexte** : un build en échec doit être signalé dans la MR avec de quoi le diagnostiquer **depuis GitLab**, sans accès à la machine du runner, et avec l'auteur du commit. Une erreur du runner (GitLab injoignable, téléchargement impossible) ne doit pas faire perdre le commit.

L'API des statuts n'accepte pas de journal : un statut n'a qu'une description (255 caractères au plus) et un lien. Le pipeline externe est attribué au compte technique du jeton (« Created by » vide) ; l'auteur est donc écrit dans la description et dans le commentaire.

**Code** : étape US 8 (version finale). Ajouts :

- après chaque build, le journal complet (sortie standard et sortie d'erreur) est déposé dans le projet GitLab (API d'upload) ; le lien du statut pointe vers lui : cliquer sur le job `ci-dev` ouvre le journal ;
- la description du statut indique le résultat, la durée, la machine et l'auteur du commit ; en cas d'échec, le nombre d'erreurs et la première erreur de compilation ;
- en cas d'échec, un commentaire est publié dans la MR : commit (titre et auteur), auteur de la MR, durée, machine, pipeline, lien vers le journal complet, et les erreurs de compilation (50 lignes au plus, sans doublon ; à défaut, les dernières lignes du journal) ;
- si le dépôt du journal ou le commentaire échoue, le build garde son résultat : un avertissement est écrit dans le journal du runner, et le commentaire indique le chemin du journal sur la machine ;
- en cas d'erreur du runner, le statut passe à « échoué » avec le message d'erreur, et le commit n'est pas noté comme traité : il est repris au passage suivant.

Le jeton de l'US 7 (scope `api`, rôle Developer) suffit pour déposer un fichier et publier un commentaire.

### Sur la machine de POC

1. Installer le code de l'US 8 (voir « Mettre à jour le code à chaque US »).

### Dans GitLab

1. Faire sur la branche de test (MR !6, `feature/47-test-failed-build`) un commit **qui ne compile pas** (par exemple une ligne de code invalide dans un fichier `.cs`).
2. Attendre la fin du build.

### Vérifications

1. La MR affiche `ci-dev` « échoué » (croix rouge). La description du job indique `Build échoué (code 1), <n> erreur(s), en <durée> sur <machine>. Commit de <auteur>. 1re erreur : <fichier>(<ligne>,<colonne>): error CS…`.
2. En cliquant sur le job `ci-dev` (pipeline de la MR ou **Build > Pipelines**), le journal complet du build s'ouvre.
3. La MR contient un commentaire « **ci-dev : Build échoué (code 1)** » avec l'auteur du commit et de la MR, le lien vers le journal et la liste des erreurs.
4. Le journal du runner contient `Journal du build déposé dans GitLab : <adresse>` et `commentaire d'échec publié`.
5. **Blocage du merge** (« Pipelines must succeed » est coché, voir US 7) : le bouton **Merge** de la MR est bloqué tant que le statut est « échoué ».
6. Corriger le code (nouveau commit) : après le build, le statut repasse à « réussi », sa description indique l'auteur du commit, le job ouvre le journal, et aucun commentaire n'est ajouté.
7. **Erreur du runner** :
   - désactiver la tâche : `Disable-ScheduledTask -TaskName 'Styx.CiDev'` ;
   - dans `ci-dev.psd1`, remplacer `BuildScript` par un chemin qui n'existe pas (par exemple `src\scripts\devops\int\absent.ps1`) ;
   - faire un commit sur la branche de test, puis lancer le script à la main : il affiche `Script de build introuvable dans les sources`, la MR affiche `ci-dev` « échoué » avec ce message, et le commit n'est pas dans `processed.txt` ;
   - remettre la bonne valeur de `BuildScript` et relancer le script : le commit est compilé et la MR repasse à « réussi » ;
   - réactiver la tâche : `Enable-ScheduledTask -TaskName 'Styx.CiDev'`.

**Fini quand** :

- un build en échec est signalé dans la MR avec ses erreurs, l'auteur du commit et un lien vers le journal complet, lisible depuis GitLab ;
- après correction, le statut repasse à « réussi » ;
- après une erreur du runner, le commit est traité au passage suivant.

**Limites connues** :

- une erreur du runner qui persiste (jeton expiré, par exemple) est retentée à chaque passage, et un statut « échoué » est republié chaque minute jusqu'à sa correction ; aucun commentaire n'est publié dans ce cas, pour ne pas en ajouter un par minute ;
- chaque build dépose un journal dans le projet GitLab (quelques dizaines de Ko) ; ces fichiers ne sont pas supprimés automatiquement.

### Nettoyage

Fermer la MR de test (**Close merge request**) et supprimer sa branche.

**Dépendances** : US 7.

## US 10 — Publication sur Artifactory (CD)

**Contexte** : la CI valide les MR. Il reste à produire la version publiée : après chaque merge dans `main`, le runner génère le package et le pousse sur Artifactory. Cette US reprend l'US 4.1 de l'Epic CI/CD (package et publication) et applique le résultat de l'US 4.2 (version issue du pipeline externe, validée à l'US 7). Le déploiement reste hors périmètre.

**Décisions** :

- **Un seul runner pour la CI et la CD** : le même script fait les deux à chaque passage. Il est renommé `cicd-int-poc` (configuration, tâche `Styx.CicdIntPoc`, journaux), et les statuts deviennent `ci-int` (MR) et `cd-int` (`main`).
- **Version** : `1.0.<numéro du pipeline externe>` (clé `VersionFormat`), unique et croissante ; depuis une version, on retrouve son pipeline et son commit dans GitLab.
- **Clé Artifactory dans une variable GitLab** : une seule mise à jour à chaque renouvellement de la clé, quel que soit le nombre de machines. La lecture d'une variable par l'API demande le rôle **Maintainer** pour le jeton du runner. Le jeton reste chiffré sur la machine du runner, lisible seulement par SYSTEM, les administrateurs et le compte du runner, et a une date d'expiration.
- **Scripts d'intégration** : comme `int/build.ps1`, le package et le push ont leur version d'intégration, sans git : `int/package.ps1` (dérivé de `dev/generate_package.ps1`, version et dossier de sortie en paramètres) et `int/push-artifactory.ps1` (configuration lue dans `config\int.psd1`).
- L'entropie du chiffrement du jeton garde son nom historique (`Styx.CiDev`) : la changer obligerait à rechiffrer le jeton.

**Code** : étape US 10 (`us-10.md` : runner, configuration, `install-task.ps1`, `diag-token.ps1` ; `us-10-scripts.md` : scripts du dépôt). À chaque passage, après la CI :

- le runner lit le dernier commit de `main` ; s'il n'est pas dans `processed-cd.txt`, il publie le statut `cd-int` « en cours » et en déduit la version ;
- il lit la clé Artifactory (variable GitLab), télécharge les sources, lance `int/package.ps1` puis `int/push-artifactory.ps1`, en transmettant la clé par l'environnement du processus (jamais en ligne de commande ni dans un fichier) ;
- il dépose les journaux du package et du push dans GitLab, publie « réussi » ou « échoué », et note le commit dans `processed-cd.txt` ;
- une erreur du runner (variable illisible, téléchargement impossible) publie « échoué » sans noter le commit : il est repris au passage suivant. La clé est lue avant le téléchargement, pour ne pas télécharger à chaque passage si elle est inaccessible ;
- une erreur de la CI n'empêche pas la CD, et inversement.

### Dans GitLab

1. **Nouveau jeton** : **Settings > Access tokens > Add new token**, avec les mêmes valeurs qu'à l'US 7, sauf :

   | Champ | Valeur |
   |---|---|
   | Token name | `cicd-int-poc-api` |
   | Select a role | **Maintainer** (nécessaire pour lire la variable de la clé Artifactory) |
   | Select scopes | `api` |

   Copier le jeton (il ne sera plus affiché).

2. **Variable de la clé Artifactory** : **Settings > CI/CD > Variables > Add variable** :

   | Champ | Valeur |
   |---|---|
   | Key | `ARTIFACTORY_API_KEY` (même nom que `Artifactory.ApiKeyVariable` dans `config\int.psd1`) |
   | Value | la clé Artifactory |
   | Mask variable | coché |
   | Protect variable | décoché |

3. **Dépôt** : par une MR, ajouter `src/scripts/devops/int/package.ps1` et `src/scripts/devops/int/push-artifactory.ps1` (`us-10-scripts.md`), et renseigner `Artifactory.BaseUrl` et `Artifactory.Repository` dans `src/scripts/devops/config/int.psd1` (mêmes valeurs que `dev.psd1`). Fusionner cette MR **avant** d'installer le code de l'US 10 : le premier passage du nouveau runner publiera ce commit de `main`.

### Sur la machine de POC

1. Supprimer l'ancienne tâche :

   ```powershell
   Unregister-ScheduledTask -TaskName 'Styx.CiDev' -Confirm:$false
   ```

2. Dans `D:\ci\runner`, créer `cicd-int-poc.ps1`, `cicd-int-poc.psd1`, et remplacer `install-task.ps1` et `diag-token.ps1` par ceux de `us-10.md`. Reporter `GitLabUrl` et `ProjectId` dans `cicd-int-poc.psd1`. Supprimer `ci-dev.ps1` et `ci-dev.psd1`. `processed.txt` est conservé : les commits de MR déjà traités ne sont pas recompilés.
3. Enregistrer les fichiers en UTF-8 avec BOM (même commande qu'aux US précédentes, sur ces quatre fichiers).
4. Chiffrer le nouveau jeton à la place de l'ancien :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\protect-token.ps1
   ```

5. Lancer le runner une fois à la main :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\cicd-int-poc.ps1
   ```

   Résultat attendu : `Branche main : commit <commit> à publier`, `Pipeline externe GitLab : <n>, version 1.0.<n>`, les étapes Package et Push, puis `Branche main : version 1.0.<n> publiée sur Artifactory`.

6. Installer la tâche :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\install-task.ps1
   ```

   Résultat attendu : la tâche `Styx.CicdIntPoc`. Au passage suivant, le journal du jour (`logs\cicd-int-poc-<date>.log`) indique `Aucune MR à traiter (<n> ouverte(s)), main à jour.`

7. Dans GitLab, révoquer l'ancien jeton `ci-int-poc-api`.

### Vérifications

1. Page du commit de `main` (ou **Build > Pipelines**) : statut `cd-int` « réussi », description `Version 1.0.<n> publiée sur Artifactory en <durée> sur <machine>. Commit de <auteur>.` ; un clic sur le job ouvre les journaux du package et du push.
2. Artifactory : le package `Styx.Publish` en version `1.0.<n>` est présent.
3. `D:\ci\runner\processed-cd.txt` contient le commit, `main 1.0.<n>` et `success`.
4. Aucune trace de la clé : rechercher ses premiers caractères dans `logs\` et `builds\` ne donne rien ; le journal du push affiche `--api-key ***`.
5. Fusionner une MR : au passage suivant, une nouvelle version, plus grande, est publiée.
6. Les MR continuent d'afficher leur statut, désormais nommé `ci-int`.
7. **Clé invalide** : remplacer temporairement la valeur de la variable GitLab par une valeur fausse, fusionner une MR : le statut `cd-int` passe à « échoué » (`Push échoué`), le journal du push indique le refus d'Artifactory. Remettre la bonne clé.

**Fini quand** :

- chaque merge dans `main` produit, sans intervention, une version publiée sur Artifactory ;
- la version se relie à son pipeline et à son commit dans GitLab ;
- la clé Artifactory n'apparaît dans aucun fichier, journal ni console de la machine ;
- l'US 4.2 de l'Epic CI/CD est close avec le format retenu (`1.0.<pipeline>`).

**Limites connues** :

- un package ou un push en échec n'est pas retenté : le commit est noté. Pour republier ce commit (après le renouvellement d'une clé expirée, par exemple), supprimer sa ligne de `processed-cd.txt`, ou fusionner un nouveau commit dans `main` ;
- si un push a réussi mais que la publication du statut a échoué ensuite, le commit est repris et son push échoue avec un conflit (409) : la version est déjà sur Artifactory ;
- comme pour la CI, chaque publication dépose ses journaux dans le projet GitLab.

**Dépendances** : US 8.

## US 9 — Documentation

**Contexte** : le runner doit pouvoir être compris, exploité et reproduit par une autre équipe, sans connaître le code.

**À faire** : rédiger deux documents à partir des US 1 à 8 telles qu'elles se sont réellement déroulées (écarts, messages réellement affichés, décisions).

1. **`devops/ci/README.md` — comment ça fonctionne** :
   - ce que fait le runner et pourquoi il remplace un runner GitLab ;
   - le déroulé d'un passage (détection des MR, sources, build, statut dans GitLab) ;
   - les fichiers et dossiers (`D:\ci\runner`, `D:\ci\build`, journaux, commits traités) ;
   - la tâche planifiée et son mode d'exécution ;
   - le jeton et sa protection ;
   - la configuration (`ci-dev.psd1`, rôle de chaque clé) ;
   - l'exploitation : lire un journal, retraiter un commit, arrêter et relancer, renouveler le jeton ;
   - le dépannage : message affiché → cause → action.

2. **`devops/ci/HOWTO.md` — mettre en place le même runner pour une autre équipe** :
   - prérequis (rôle GitLab, compte Windows, PowerShell 5.1, SDK .NET, accès réseau) ;
   - dans GitLab : identifiant du projet, jeton, réglages de MR ;
   - sur la machine : dossiers, scripts, configuration, jeton chiffré, tâche planifiée ;
   - vérification de bout en bout avec une MR de test ;
   - adaptations à prévoir (script de build, environnement, nom du statut).

   Étapes numérotées, une commande par bloc, résultat attendu après chaque commande.

**Fini quand** : le How-to a été rejoué de bout en bout, en le suivant uniquement, dans un nouveau terminal, idéalement par une autre personne ; les écarts constatés sont corrigés dans les deux documents.

**Dépendances** : US 8.

---

## Hors périmètre

- **Runner-cd** : package et publication sur Artifactory après un merge dans `main` (Epic CI/CD, US 4.1).
- **Nettoyage** des journaux (`logs`, `builds`) : un fichier par jour et un par build, à purger plus tard si besoin.
- **Tests unitaires** dans le build, quand ils existeront.
