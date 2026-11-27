# Artifactory : comptes, jetons et commandes

**Artifactory** est l'entrepôt de l'entreprise où sont rangés les packages et les briques logicielles (paquets NuGet). Pour y accéder, il faut un **compte** et un **jeton** (une sorte de mot de passe).

Ce document explique quel compte utiliser, comment obtenir son jeton et où le ranger. Il ne contient aucun secret : les textes entre `< >` sont à remplacer par la vraie valeur, sans garder les `< >`.

## 1. Deux comptes, deux usages

Le projet a deux comptes Artifactory. Ils n'ont pas les mêmes droits : **ne jamais les inverser**.

| Compte | Nom | Sert à | Utilisé par |
|---|---|---|---|
| **compte de build** | `usr-<kear_uid>` | télécharger les briques logicielles pendant la compilation, et déposer le package | le runner CI |
| **compte de déploiement** | `usr-cd-<kear_uid>` | télécharger le package pour l'installer sur un serveur | `download_deploy.ps1` |

`<kear_uid>` est l'identifiant de l'application ; il apparaît dans le nom des deux comptes.

Chaque développeur, lui, utilise **son propre compte** sur son poste : voir la partie 4.

Si Artifactory refuse l'accès :

| Code | Ce que ça veut dire | Que faire |
|---|---|---|
| **401** | le compte et le jeton ne vont pas ensemble, ou le jeton a été supprimé | vérifier que le nom du compte est exactement celui donné avec le jeton (partie 2) ; sinon, créer un nouveau jeton |
| **403** | le compte n'a pas le droit de faire cette opération | c'est presque toujours une inversion entre le compte de build et le compte de déploiement |
| **409** | la version du package existe déjà sur Artifactory | normal si on republie une version : rien à faire |

## 2. Obtenir un jeton

Les jetons se créent sur la page de l'**API Artifacts du Groupe** : `<adresse de l'API Artifacts>`, à remplacer par l'adresse interne.

Il faut un compte personnel qui a les droits sur l'application. Le jeton créé appartient au compte de build ou de déploiement, pas à vous.

1. Ouvrir `<adresse de l'API Artifacts>` dans le navigateur.
2. Cliquer sur **Authorize**, en haut de la page. Dans la fenêtre qui s'ouvre, laisser les valeurs proposées (les trois cases `openid`, `profile` et `api.artifacts-api.v1` doivent être cochées), cliquer sur **Authorize**, puis se connecter avec son compte personnel. Fermer la fenêtre avec **Close**.
3. Dans la partie « Project management », ouvrir la ligne `POST /projects/{kear_uid}/tokens`, puis cliquer sur **Try it out**.
4. Remplir :
   - `kear_uid` : l'identifiant de l'application ;
   - `user_type` : `user` pour le compte de build, `user-cd` pour le compte de déploiement.
5. Cliquer sur **Execute**.

Vous devez voir, dans la réponse :

```json
{
  "token": "<jeton>",
  "token_id": "<identifiant du jeton>",
  "user": "<nom du compte>"
}
```

- `user` est le nom exact du compte à utiliser avec ce jeton.
- `token_id` sert à supprimer ce jeton plus tard : le noter.
- `token` est le jeton. Le copier **directement à sa place** (partie 3). Ne jamais le coller dans une note, un mail, un message, une capture d'écran ou un fichier du dépôt.

Le jeton n'expire pas. Mais s'il a été vu ou copié ailleurs, il faut le remplacer (partie 4).

## 3. Où ranger les jetons

| Jeton | Où le ranger | Comment |
|---|---|---|
| compte de build, pour le runner | dans GitLab, variable `ARTIFACTORY_API_KEY` (masquée), valeur `usr-<kear_uid>:<jeton>` | **Settings > CI/CD > Variables** ; voir le [mode d'emploi d'installation](howto/HOWTO.md), étape 2.3 |
| compte personnel, sur le poste d'un développeur | variables d'environnement `ARTIFACTORY_BUILD_USERNAME` et `ARTIFACTORY_BUILD_TOKEN` | voir la partie 4 |
| compte de déploiement, sur un serveur | variables d'environnement `ARTIFACTORY_USERNAME` (`usr-cd-<kear_uid>`) et `ARTIFACTORY_TOKEN` | voir le README de `download_deploy` |

## 4. Poste du développeur

Pour compiler l'application, le poste télécharge les briques logicielles sur Artifactory avec **le compte personnel** du développeur. Chacun fait ces étapes **une seule fois**.

### 4.1 Obtenir son jeton

1. Se connecter à sgithub : `<adresse de sgithub>`.
2. Se connecter à JFrog : `<adresse de JFrog>`.
3. Dans le menu **All Projects**, choisir le projet de l'application.
4. Cliquer sur **Artifacts**, puis sélectionner le dépôt `nuget-virtual-<kear_uid>`.
5. Cliquer sur **Set Me Up**, puis sur **Generate token**.
6. Copier le jeton affiché. Il sert à l'étape suivante ; ne le coller nulle part ailleurs.

### 4.2 Enregistrer ses identifiants

Le fichier `src/backend/nuget.config` du dépôt lit le compte et le jeton dans deux variables d'environnement.

1. Ouvrir une fenêtre PowerShell.
2. Enregistrer le compte : votre identifiant JFrog, le même que sur sgithub (par exemple `prenom-nom-ext`).
   ```powershell
   [Environment]::SetEnvironmentVariable('ARTIFACTORY_BUILD_USERNAME', '<votre identifiant>', 'User')
   ```
3. Enregistrer le jeton. Collez-le quand il est demandé : il ne s'affiche pas.
   ```powershell
   [Environment]::SetEnvironmentVariable('ARTIFACTORY_BUILD_TOKEN', (Read-Host 'Jeton Artifactory'), 'User')
   ```
4. Si vous aviez ajouté la source Artifactory à la main (ancienne méthode, `dotnet nuget add source …`), la retirer :
   ```powershell
   dotnet nuget remove source ArtifactoryV2
   ```
   Vous devez voir : `Package source with Name: ArtifactoryV2 removed successfully.` Si le message dit qu'aucune source ne porte ce nom, il n'y a rien à retirer.
5. Fermer Visual Studio et toutes les fenêtres PowerShell : les variables ne sont lues qu'à leur ouverture.

### 4.3 Mettre à jour le dépôt

Récupérer la dernière version de `main`. Son fichier `src/backend/nuget.config` contient la section ci-dessous. Elle ne doit **jamais** contenir de vrai compte ni de vrai jeton.

```xml
<packageSourceCredentials>
  <ArtifactoryV2>
    <add key="Username" value="%ARTIFACTORY_BUILD_USERNAME%" />
    <add key="ClearTextPassword" value="%ARTIFACTORY_BUILD_TOKEN%" />
  </ArtifactoryV2>
</packageSourceCredentials>
```

### 4.4 Vérifier

Dans une **nouvelle** fenêtre PowerShell, depuis le dossier `src\backend` du dépôt :

```powershell
dotnet restore --force --no-cache --packages "$env:TEMP\nuget-test"
```

Vous devez voir les paquets se télécharger (environ une minute), puis la restauration se terminer **sans erreur `NU1301`**. Supprimer ensuite le dossier `$env:TEMP\nuget-test`.

Un simple « Nettoyer » dans Visual Studio ne suffit pas à vérifier : les paquets déjà présents sur le poste sont réutilisés, sans contacter Artifactory.

## 5. Remplacer un jeton

Toujours dans cet ordre, sinon la publication ou le téléchargement s'arrête :

1. Créer le nouveau jeton (partie 2).
2. Le ranger à sa place (partie 3).
3. Vérifier que ça marche : une publication du runner, ou un téléchargement.
4. Seulement ensuite, supprimer l'ancien jeton avec son `token_id`.

Si une publication a échoué à cause du jeton, elle n'est pas refaite toute seule : voir [Le runner CI](cicd/ci/README.md), « Refaire un commit ».

## 6. Pour les développeurs : commandes et réglages

**Téléchargement des briques logicielles** : `src/backend/nuget.config` déclare deux sources. Le compte et le jeton n'y sont jamais écrits : ils sont lus dans les variables d'environnement.

```xml
<packageSources>
  <clear />
  <add key="ArtifactoryV2" value="https://<artifactory>/artifactory/api/nuget/nuget-virtual-<kear_uid>" />
  <add key="OfflineSocGen" value="..\..\nugets\socgen" />
</packageSources>
<packageSourceCredentials>
  <ArtifactoryV2>
    <add key="Username" value="%ARTIFACTORY_BUILD_USERNAME%" />
    <add key="ClearTextPassword" value="%ARTIFACTORY_BUILD_TOKEN%" />
  </ArtifactoryV2>
</packageSourceCredentials>
```

`nugets\socgen` contient les briques internes qu'Artifactory n'a pas encore (`SocGen.ApiBank.*`). Une demande est en cours auprès de l'équipe Artifactory pour les y publier ; le dossier `nugets` pourra alors disparaître.

**Dépôt d'un package** (compte de build) :

```powershell
dotnet nuget push <package>.nupkg --api-key "usr-<kear_uid>:<jeton>" --source https://<artifactory>/artifactory/api/nuget/v3/<dépôt>
```

**Téléchargement d'un package** (compte de déploiement) :

```powershell
curl.exe -u "usr-cd-<kear_uid>:<jeton>" -L -o <package>.nupkg https://<artifactory>/artifactory/<dépôt>/<package>.nupkg
```

`<dépôt>` est le dépôt NuGet du projet (`nuget-releases-private-<kear_uid>` pour les versions publiées par le runner). En pratique, ces commandes sont lancées par les scripts, et le jeton n'apparaît jamais dans les journaux.

## 7. En cas de problème

Ouvrir une demande auprès du support Artifactory (service JFrog de la plateforme), puis prévenir le référent Artifactory de l'équipe.
