# Installer le runner CI sur un projet

## À quoi sert ce document

Ce document explique, pas à pas, comment installer le **runner CI** sur une machine Windows pour un projet GitLab.

Le runner est un petit programme qui tourne tout seul, chaque minute, sur la machine. Une fois installé :

- quand un développeur envoie du code dans une **merge request** (une demande de fusion dans GitLab), le runner vérifie que le code se compile, et affiche le résultat dans GitLab : une coche verte si tout va bien, une croix rouge sinon ;
- quand une merge request est **fusionnée** dans la branche `main`, le runner fabrique le **package** de l'application (un fichier `.nupkg`) et le dépose sur **Artifactory** (l'entrepôt où sont rangés les packages).

Il n'est pas nécessaire de comprendre comment le runner fonctionne pour l'installer : il suffit de suivre les étapes dans l'ordre. Le fonctionnement est expliqué dans `runner.md`.

**Comment lire ce document**

- Faites les étapes dans l'ordre, sans en sauter.
- Chaque commande est dans un cadre gris. Copiez-la telle quelle, collez-la dans la fenêtre indiquée, puis appuyez sur **Entrée**.
- Après chaque commande, le paragraphe « Vous devez voir » dit ce qui doit s'afficher. Si ce n'est pas le cas, arrêtez-vous et allez à la partie 6, « En cas de problème ».
- Les textes entre `< >` sont à remplacer par votre valeur, sans garder les `< >`. Par exemple, `<compte>` devient `dupont`.

**Durée** : environ une heure.

---

## 1. Avant de commencer

### 1.1 Les accès nécessaires

Cette étape sert à vérifier que vous avez tout ce qu'il faut. Sans ces accès, vous serez bloqué en cours de route.

| Il vous faut | Pourquoi | Comment vérifier |
|---|---|---|
| Le rôle **Maintainer** sur le projet GitLab | pour créer le jeton et la variable de la partie 2 | dans le projet GitLab, le menu de gauche affiche **Settings** |
| Une connexion **Bureau à distance** à la machine du runner, avec le **compte de service** prévu pour le runner | le runner tournera avec ce compte | vous arrivez à vous connecter à la machine avec ce compte |
| Ce compte est **administrateur** de la machine | pour créer la tâche planifiée | voir l'étape 3.1 |
| La **clé Artifactory** qui autorise à déposer des packages | pour la publication | voir [Artifactory : comptes, jetons et commandes](../artifactory.md), § 2 (jeton de push, `user_type = user`) |

### 1.2 Ce que la machine doit avoir

Cette étape vérifie que la machine est prête. Faites-la une fois connecté à la machine, dans la fenêtre PowerShell (voir l'étape 3.1 pour l'ouvrir).

| Élément | Commande de vérification | Vous devez voir |
|---|---|---|
| Un lecteur `D:` | `Test-Path D:\` | `True` |
| Windows PowerShell 5.1 | `$PSVersionTable.PSVersion` | `5` dans la colonne `Major`, `1` dans `Minor` |
| Le SDK .NET de l'application | `dotnet --list-sdks` | au moins une ligne, par exemple `8.0.xxx` |
| L'accès à GitLab | `Test-NetConnection <adresse de GitLab, sans https://> -Port 443` | `TcpTestSucceeded : True` |
| L'accès à Artifactory | `Test-NetConnection <adresse d'Artifactory, sans https://> -Port 443` | `TcpTestSucceeded : True` |

Si une ligne ne correspond pas, demandez à l'équipe qui gère la machine de corriger avant de continuer.

### 1.3 Ce que le projet doit contenir (à faire par un développeur)

Cette étape concerne le code du projet, pas la machine. Si vous n'êtes pas développeur, demandez à un développeur de l'équipe de confirmer que tout est en place.

La machine du runner n'a pas accès à Internet ni aux partages réseau ; elle a accès à **Artifactory**. Le projet doit donc trouver tout ce qu'il faut pour se compiler, soit sur Artifactory, soit dans le dépôt :

- les paquets NuGet viennent du dépôt NuGet d'**Artifactory** ;
- les paquets internes qui ne sont pas (encore) sur Artifactory sont dans le dépôt, dans le dossier `nugets` ; si Artifactory est complet, ce dossier peut être vide ;
- `src/backend/nuget.config` déclare uniquement ces deux sources, Artifactory et `nugets`, précédées de la ligne `<clear />` : nuget.org est interdit et injoignable, et sans cette ligne la compilation échouerait en essayant de le joindre ;
- aucun projet ne va chercher une DLL sur un partage réseau (chemin commençant par `\\`) : les DLL externes sont copiées dans le dépôt ;
- les fichiers `.nupkg` du dossier `nugets` et les `.dll` copiées sont bien enregistrés dans git, même si le `.gitignore` les exclut (`git status --ignored` ne doit afficher aucune ligne `!!` pour eux).

Pour le vérifier, sur un poste de développeur, depuis la racine du dépôt, la commande suivante doit se terminer sans erreur `NU1101`, `NU1102` ni `NU1301` :

```powershell
dotnet restore .\src\backend\<solution>.sln --packages "$env:TEMP\nuget-test"
```

Le dépôt doit aussi contenir les scripts suivants, dans `src/scripts/devops/` :

| Fichier | À quoi il sert |
|---|---|
| `int/build.ps1` | compiler le code |
| `int/package.ps1` | fabriquer le package |
| `int/push-artifactory.ps1` | déposer le package sur Artifactory |
| `config/int.psd1` | les réglages de l'environnement, dont l'adresse d'Artifactory |

Et les fichiers du runner, fournis avec ce document dans `src/scripts/devops/howto/`. Ce sont des **modèles** : ils ne contiennent aucune vraie valeur, vous les compléterez à l'étape 3.3.

| Fichier | À quoi il sert |
|---|---|
| `ci-int.ps1` | le runner lui-même |
| `ci-int.psd1` | ses réglages (adresse de GitLab, numéro du projet…) |
| `install-task.ps1` | installer la tâche qui lance le runner chaque minute |
| `protect-token.ps1` | enregistrer le jeton GitLab de façon sécurisée |
| `diag-token.ps1` | vérifier le jeton en cas de problème |

---

## 2. Dans GitLab

Dans cette partie, vous préparez GitLab pour qu'il accepte de parler au runner. Tout se fait dans le navigateur, sur la page du projet.

### 2.1 Noter l'adresse de GitLab et le numéro du projet

Le runner a besoin de deux informations pour trouver le projet : **l'adresse de GitLab** et le **numéro du projet** (appelé « Project ID »). Vous allez les noter dans un bloc-notes, puis les recopier à l'étape 3.3.

1. Ouvrez la page d'accueil du projet dans GitLab.
2. **L'adresse de GitLab** est le début de l'adresse affichée dans la barre du navigateur, jusqu'au premier `/` après le nom du site. Par exemple, si la barre affiche `https://XXXXX/equipe/projet`, l'adresse de GitLab est `https://XXXXX`. Notez-la, **sans** `/` à la fin.
3. **Le numéro du projet** : en haut à droite de la page d'accueil du projet, cliquez sur le bouton **⋮** (trois points verticaux), puis sur **Copy project ID**. Le numéro est copié : collez-le dans votre bloc-notes.

   Si ce bouton n'existe pas sur votre version de GitLab : dans le menu de gauche, cliquez sur **Settings**, puis **General**. Le numéro est affiché dans le champ **Project ID**, en haut de la page.

Vous devez avoir noté : une adresse comme `https://XXXXX`, et un nombre comme `1234`.

### 2.2 Créer le jeton du runner

Un **jeton** est un mot de passe réservé à un programme. Le runner l'utilisera pour lire le code dans GitLab et y afficher ses résultats. Vous le créez ici, puis vous l'enregistrerez sur la machine à l'étape 3.4.

1. Dans le menu de gauche du projet, cliquez sur **Settings**, puis **Access tokens**.
2. Cliquez sur **Add new token**.
3. Remplissez le formulaire :

   | Champ | À saisir |
   |---|---|
   | Token name | `ci-int-<nom du projet>-api` |
   | Expiration date | la date la plus lointaine autorisée. **Notez cette date dans le calendrier de l'équipe** : le jeton devra être renouvelé avant (voir la partie 7) |
   | Select a role | **Maintainer** |
   | Select scopes | cochez uniquement **api** |

4. Cliquez sur **Create project access token**.
5. Le jeton s'affiche **une seule fois** en haut de la page. Cliquez sur l'icône de copie à côté, et gardez la page ouverte jusqu'à l'étape 3.4. **Ne le collez dans aucun fichier, mail ou message.**

Vous devez voir : le jeton dans la liste **Active project access tokens**, avec le rôle **Maintainer**.

Si **Access tokens** n'apparaît pas dans le menu, GitLab interdit peut-être les jetons de projet : arrêtez-vous et signalez-le.

### 2.3 Enregistrer la clé Artifactory dans GitLab

Le runner a besoin de la clé Artifactory pour déposer les packages. Elle est rangée dans GitLab, et non sur la machine : quand la clé change, il suffit de la modifier à un seul endroit.

La clé est formée du **compte de push** et de son **jeton**, séparés par `:` : `usr-<kear_uid>:<jeton>`. Comment obtenir ce jeton, et ne pas le confondre avec celui du compte de déploiement (`usr-cd-…`) : voir [Artifactory : comptes, jetons et commandes](../artifactory.md).

1. Dans le menu de gauche du projet, cliquez sur **Settings**, puis **CI/CD**.
2. À la section **Variables**, cliquez sur **Expand**, puis sur **Add variable**.
3. Remplissez le formulaire :

   | Champ | À saisir |
   |---|---|
   | Type | `Variable (default)` |
   | Environments | `All (default)` |
   | Visibility | **Masked** |
   | Protect variable | ne pas cocher |
   | Expand variable reference | ne pas cocher |
   | Description | `Clé Artifactory du runner ci-int` |
   | Key | `ARTIFACTORY_API_KEY` (exactement ce texte) |
   | Value | `usr-<kear_uid>:<jeton>` (voir ci-dessus) |

4. Cliquez sur **Add variable**.

Vous devez voir : `ARTIFACTORY_API_KEY` dans la liste, avec sa valeur cachée par des étoiles.

Si GitLab refuse la valeur (message sur le format), arrêtez-vous et signalez-le.

### 2.4 Régler les merge requests

Ces réglages font en sorte qu'une merge request ne puisse être fusionnée que si le runner a validé le code.

1. **Supprimer le fichier `.gitlab-ci.yml` s'il existe.** Regardez la liste des fichiers sur la page d'accueil du projet. S'il y a un fichier `.gitlab-ci.yml`, demandez à un développeur de le supprimer par une merge request. Ce fichier sert aux runners GitLab, qui n'existent pas ici : s'il reste, GitLab attend indéfiniment un résultat qui ne viendra jamais, et bloque les fusions.
2. **Exiger un résultat positif avant la fusion.** Dans le menu de gauche, cliquez sur **Settings**, puis **Merge requests**. Descendez jusqu'à **Merge checks**, cochez **Pipelines must succeed**, puis cliquez sur **Save changes** en bas de la section.

   Si vous avez dû supprimer le `.gitlab-ci.yml`, faites ce réglage **après** la fusion de cette suppression : sinon, elle ne pourrait pas être fusionnée.

Vous devez voir : un message de confirmation en haut de la page après **Save changes**.

---

## 3. Sur la machine

Dans cette partie, vous installez le runner sur la machine Windows et vous le faites démarrer.

### 3.1 Ouvrir la fenêtre de commandes

Toutes les commandes de cette partie se tapent dans une fenêtre **Windows PowerShell en administrateur**, ouverte avec le **compte de service** du runner. C'est important : le runner tournera avec le compte qui fait l'installation.

1. Connectez-vous à la machine en **Bureau à distance**, avec le compte de service.
2. Cliquez sur le menu **Démarrer**, tapez `PowerShell`.
3. Faites un clic droit sur **Windows PowerShell** (pas « PowerShell 7 », pas « Windows PowerShell ISE »), puis cliquez sur **Exécuter en tant qu'administrateur**. Acceptez la question de Windows.

Vous devez voir : une fenêtre dont le titre commence par **Administrateur** (ou **Administrator**).

Pour vérifier le compte utilisé, tapez :

```powershell
whoami
```

Vous devez voir : `<domaine>\<compte de service>`.

### 3.2 Créer les dossiers

Le runner range ses fichiers dans trois dossiers du lecteur `D:` :

- `D:\ci\runner` : le runner, ses réglages et ses journaux ;
- `D:\ci\build` : le code téléchargé depuis GitLab ;
- `D:\ci\packages` : les packages fabriqués.

Cette commande crée les trois dossiers d'un coup :

```powershell
New-Item -ItemType Directory -Force D:\ci\runner, D:\ci\build, D:\ci\packages
```

Vous devez voir : un tableau avec trois lignes, `runner`, `build` et `packages`.

### 3.3 Copier et régler les fichiers du runner

Vous allez déposer les cinq fichiers modèles sur la machine, puis y inscrire l'adresse de GitLab et le numéro du projet notés à l'étape 2.1.

**Copier les fichiers**

1. Sur votre poste, ouvrez le dossier `src/scripts/devops/howto/` du projet (ou, dans GitLab, ouvrez chaque fichier et cliquez sur **Download**).
2. Sélectionnez les cinq fichiers `ci-int.ps1`, `ci-int.psd1`, `install-task.ps1`, `protect-token.ps1` et `diag-token.ps1`, puis faites **Ctrl+C**.
3. Dans la fenêtre Bureau à distance, ouvrez l'Explorateur de fichiers, allez dans `D:\ci\runner`, puis faites **Ctrl+V**.
4. Windows bloque les fichiers venus d'un autre ordinateur. Cette commande les débloque :

   ```powershell
   Get-ChildItem D:\ci\runner | Unblock-File
   ```

   Vous devez voir : rien. L'absence de message est normale.

**Renseigner les réglages**

5. Cette commande ouvre le fichier des réglages dans le Bloc-notes :

   ```powershell
   notepad D:\ci\runner\ci-int.psd1
   ```

6. Dans le Bloc-notes, remplacez :
   - `https://XXXXX` par l'adresse de GitLab notée à l'étape 2.1, en gardant les apostrophes autour : `'https://…'` ;
   - le `0` de la ligne `ProjectId = 0` par le numéro du projet.

   Ne touchez pas aux autres lignes. Enregistrez (**Ctrl+S**), puis fermez le Bloc-notes.

**Enregistrer les fichiers dans le bon format**

7. Windows PowerShell a besoin que ces fichiers soient enregistrés dans un format particulier (« UTF-8 avec BOM ») ; sinon, les accents s'affichent mal et les scripts peuvent refuser de démarrer. Cette commande les réenregistre tous dans ce format :

   ```powershell
   Get-ChildItem D:\ci\runner -Include *.ps1, *.psd1 -Recurse | ForEach-Object { Set-Content -LiteralPath $_.FullName -Value (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8) -Encoding UTF8 -NoNewline }
   ```

   Vous devez voir : rien.

**Vérifier les réglages**

8. Cette commande relit le fichier des réglages et affiche son contenu :

   ```powershell
   Import-PowerShellDataFile D:\ci\runner\ci-int.psd1
   ```

   Vous devez voir : un tableau de deux colonnes (`Name`, `Value`), avec votre adresse de GitLab sur la ligne `GitLabUrl` et votre numéro sur la ligne `ProjectId`.

   Un message en rouge signifie le plus souvent qu'une apostrophe a été effacée au point 6 : rouvrez le fichier et corrigez.

### 3.4 Enregistrer le jeton GitLab

Vous allez donner au runner le jeton créé à l'étape 2.2. Le script le chiffre (le rend illisible) et l'enregistre dans `D:\ci\runner\gitlab-token.dat`. Seuls le compte de service, les administrateurs et Windows peuvent le lire, et uniquement sur cette machine.

1. Lancez le script :

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\protect-token.ps1
   ```

2. Le texte `Jeton GitLab:` s'affiche. Collez le jeton copié à l'étape 2.2 (clic droit dans la fenêtre, ou **Ctrl+V**). **Rien ne s'affiche quand vous collez, c'est normal.** Appuyez sur **Entrée**.

   Vous devez voir :

   ```text
   Jeton chiffré dans D:\ci\runner\gitlab-token.dat
     Lecture autorisée : SYSTEM, Administrateurs, <domaine>\<compte de service>
   ```

3. Le jeton est encore dans le presse-papiers : copiez n'importe quel autre texte pour l'effacer. Vous pouvez maintenant fermer la page GitLab de l'étape 2.2.

### 3.5 Lancer le runner une première fois, à la main

Avant de le laisser tourner tout seul, vous lancez le runner une fois vous-même. Vous verrez ainsi dans la fenêtre s'il arrive à joindre GitLab et à lire le projet.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\ci-int.ps1
```

Le runner peut prendre quelques minutes s'il a du travail. Chaque ligne qu'il affiche commence par la date et `[INFO]`. Vous devez voir l'une de ces lignes :

| Message | Ce que ça veut dire |
|---|---|
| `Aucune MR à traiter (<n> ouverte(s)), main à jour.` | tout va bien, il n'y avait rien à faire |
| `MR !<n> « <titre> » …`, puis `MR !<n> : Build réussi` | une merge request a été vérifiée |
| `Branche main : version 1.0.<n> publiée sur Artifactory` | un package a été déposé sur Artifactory |
| `Branche main : commit <x> ignoré, scripts absents du commit` | la branche `main` ne contient pas encore les scripts de l'étape 1.3 : c'est normal sur un projet qui démarre, le prochain commit de `main` sera publié |

Une ligne `[ERREUR]` en rouge signifie que quelque chose ne va pas : voir la partie 6.

### 3.6 Installer la tâche planifiée

Jusqu'ici, le runner ne tourne que si vous le lancez. Cette étape crée une **tâche planifiée** Windows qui le lance automatiquement **chaque minute**, même quand personne n'est connecté à la machine, et même après un redémarrage.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\install-task.ps1
```

Vous devez voir :

```text
Tâche Styx.CiInt créée, état : Ready
  Compte    : <domaine>\<compte de service>
  Exécution : ...
  Script    : D:\ci\runner\ci-int.ps1
  Fréquence : toutes les 1 minute(s)
```

Aucun mot de passe n'est demandé : c'est normal.

Attendez deux minutes. Cette commande affiche les trois dernières lignes du journal du jour, le fichier où le runner note ce qu'il fait :

```powershell
Get-Content D:\ci\runner\logs\ci-int-$(Get-Date -Format yyyyMMdd).log -Tail 3
```

Vous devez voir : des lignes `Aucune MR à traiter (<n> ouverte(s)), main à jour.`, une par minute, avec des heures récentes.

Aucune fenêtre ne doit s'ouvrir sur l'écran chaque minute : la tâche tourne en arrière-plan.

---

## 4. Vérifier que tout fonctionne

Cette partie teste le runner en conditions réelles, de bout en bout. Les points 2 à 5 demandent l'aide d'un développeur, qui fait des modifications de test dans le code.

1. **Le runner continue quand personne n'est connecté.** Déconnectez-vous de la machine : menu **Démarrer**, icône du compte, **Se déconnecter** (fermer la fenêtre Bureau à distance ne suffit pas). Attendez deux minutes, reconnectez-vous, rouvrez PowerShell (étape 3.1), puis relancez la commande de lecture du journal (fin de l'étape 3.6).

   Vous devez voir : une ligne pour chaque minute où vous étiez déconnecté.

2. **Une merge request correcte.** Le développeur crée une branche de test avec une petite modification qui compile, et ouvre une merge request.

   Vous devez voir, dans les deux minutes, sur la page de la merge request : un pipeline « running » (en cours), puis « passed » avec une coche verte. Un clic sur la coche, puis sur `ci-int`, ouvre le journal de compilation.

3. **Une merge request avec une erreur.** Le développeur ajoute, sur la même branche, une ligne volontairement fausse dans un fichier `.cs`.

   Vous devez voir : le pipeline « failed » avec une croix rouge, un commentaire du runner dans la merge request qui donne l'erreur, et le bouton **Merge** bloqué avec le message « Pipeline must succeed ».

4. **La correction.** Le développeur retire la ligne fausse.

   Vous devez voir : le pipeline « passed », et le bouton **Merge** de nouveau disponible.

5. **La publication.** Cliquez sur **Merge**.

   Vous devez voir, dans les deux minutes : sur la page du commit de fusion (dans `main`), un pipeline « passed » ; et sur Artifactory, le package `Styx.Publish` dans une version `1.0.<numéro du pipeline>`.

6. **Aucun secret dans les journaux.** Cette commande cherche les quatre premiers caractères de la clé Artifactory dans tous les journaux du runner. Remplacez `<début de la clé>` par ces caractères :

   ```powershell
   Select-String -Path D:\ci\runner\logs\*, D:\ci\runner\builds\* -Pattern '<début de la clé>' -SimpleMatch
   ```

   Vous devez voir : rien. Dans le journal du dépôt sur Artifactory, la clé apparaît sous la forme `--api-key ***`.

L'installation est terminée.

---

## 5. Réglages possibles

Ces réglages sont facultatifs. Ils se font dans `D:\ci\runner\ci-int.psd1`, que vous ouvrez avec `notepad D:\ci\runner\ci-int.psd1`. Le runner relit ce fichier à chaque passage : une modification est prise en compte à la minute suivante, sans rien relancer.

| Vous voulez | Ligne à modifier |
|---|---|
| utiliser d'autres scripts de compilation, de package ou de dépôt | `BuildScript`, `PackageScript`, `PushScript` |
| changer d'environnement | `BuildEnvironment` (il faut alors un fichier `config\<environnement>.psd1` dans le projet) |
| publier une autre branche que `main` | `PublishBranch` |
| changer le nom affiché dans GitLab | `StatusName` (merge requests), `PublishStatusName` (`main`) |
| changer la forme des numéros de version | `VersionFormat` (`{0}` est remplacé par le numéro du pipeline) |
| laisser plus de temps à une compilation longue | `BuildTimeoutMinutes` |
| ranger les fichiers ailleurs | `RunnerRoot`, `BuildRoot`, `PackageRoot`, `TokenFile` |

Pour donner un autre nom à la tâche planifiée, refaites l'étape 3.6 en ajoutant ` -TaskName <nom>` à la fin de la commande.

---

## 6. En cas de problème

| Ce que vous voyez | Ce qu'il faut faire |
|---|---|
| Des accents remplacés par des signes bizarres, ou `Attribut inattendu` | refaire l'étape 3.3, point 7 |
| Un message rouge à l'étape 3.3, point 8 | rouvrir `ci-int.psd1` et vérifier les apostrophes autour de l'adresse |
| `GitLab refuse le jeton (401)` | le jeton a été mal collé ou a expiré : refaire l'étape 2.2, puis l'étape 3.4 |
| `GitLab refuse l'accès (403)` | le jeton n'a pas le rôle Maintainer ou le scope api : refaire l'étape 2.2, puis l'étape 3.4 |
| `Ressource GitLab introuvable (404)` | le numéro du projet est faux : refaire l'étape 2.1 et corriger `ProjectId` |
| `Push échoué` avec `401` ou `403` dans le journal du push | mauvais compte ou mauvais jeton dans `ARTIFACTORY_API_KEY` : voir [Artifactory : comptes, jetons et commandes](../artifactory.md), § 1 |
| `Variable GitLab 'ARTIFACTORY_API_KEY' illisible` | la variable n'existe pas ou porte un autre nom : refaire l'étape 2.3 |
| `Build échoué` avec `NU1101` ou `NU1102` dans le journal | un paquet NuGet n'est ni sur Artifactory ni dans le dossier `nugets` : un développeur doit l'ajouter à l'un des deux (étape 1.3) |
| `Build échoué` avec `NU1301` dans le journal | une source NuGet est injoignable : vérifier l'accès à Artifactory (étape 1.2) et la ligne `<clear />` de `nuget.config` (étape 1.3) |
| `Script introuvable dans les sources` | il manque un script dans le projet : voir l'étape 1.3 avec un développeur |
| `Build échoué` dans une merge request | ce n'est pas un problème du runner : le code ne se compile pas ; le commentaire dans la merge request donne l'erreur |
| Plus aucune nouvelle ligne dans le journal | vérifier que la tâche existe avec `Get-ScheduledTask -TaskName 'Styx.CiInt'` ; sinon, refaire l'étape 3.6 |
| Une fenêtre s'ouvre chaque minute | refaire l'étape 3.6 |

Pour tout autre message, consultez la partie « Dépannage » de `runner.md`, ou transmettez le journal du jour (`D:\ci\runner\logs\ci-int-<date>.log`) à l'équipe du projet.

---

## 7. Après l'installation

- **Date d'expiration du jeton GitLab** : vous l'avez notée à l'étape 2.2. Avant cette date, il faut créer un nouveau jeton (étape 2.2) et l'enregistrer sur la machine (étape 3.4) ; sinon, le runner s'arrêtera de fonctionner ce jour-là. Le détail est dans `runner.md`.
- **Clé Artifactory** : le jeton n'expire pas, mais il doit être remplacé s'il a été exposé. Il suffit alors de modifier la variable `ARTIFACTORY_API_KEY` dans GitLab (étape 2.3), rien à faire sur la machine. La procédure de remplacement est dans [Artifactory : comptes, jetons et commandes](../artifactory.md), § 5.
- **Place sur le disque** : les dossiers `D:\ci\runner\logs`, `D:\ci\runner\builds` et `D:\ci\packages` grossissent avec le temps et ne sont pas vidés automatiquement. Pensez à les surveiller.
