# Artifactory : comptes, jetons et commandes

Ce document explique quel compte Artifactory utiliser pour chaque opération, comment obtenir son jeton, et où le ranger. Il ne contient aucun secret : les valeurs entre `< >` sont à remplacer.

## 1. Deux comptes, deux usages

Le projet dispose de deux comptes techniques Artifactory. Ils n'ont pas les mêmes droits : **ne pas les inverser**.

| Usage | Compte | Forme du nom | Opérations | Utilisé par |
|---|---|---|---|---|
| **Build** | compte projet (`user`) | `usr-<kear_uid>` | restaurer les paquets NuGet de la solution, déposer (push) le package | runner `ci-int` (build des MR, publication de `main`), `push-artifactory.ps1` |
| **Déploiement** | compte de déploiement (`user-cd`) | `usr-cd-<kear_uid>` | télécharger (get) le package à déployer | `download_deploy.ps1` |

Symptômes d'une inversion ou d'une erreur de compte :

| Code renvoyé | Signification | Action |
|---|---|---|
| **401** Unauthorized | le compte et le jeton ne vont pas ensemble, ou le jeton est expiré ou révoqué | vérifier que le nom du compte est exactement celui renvoyé avec le jeton (§ 2) ; sinon, recréer le jeton |
| **403** Forbidden | le compte est reconnu mais n'a pas le droit pour cette opération | vérifier qu'on utilise le bon compte (tableau ci-dessus) : c'est presque toujours une inversion `user` / `user-cd` |
| **409** Conflict (push) | cette version existe déjà sur Artifactory | rien à faire : un nouveau commit sur `main` produira une nouvelle version |

## 2. Obtenir un jeton

Les deux jetons s'obtiennent par la même route de l'API Artifacts du Groupe, section « Project management » :

```text
POST /projects/{kear_uid}/tokens?user_type=<type>
```

| Paramètre | Où | Valeur |
|---|---|---|
| `kear_uid` | dans l'adresse (obligatoire) | identifiant KEAR de l'application, le même que celui qui apparaît dans le nom des comptes et des dépôts |
| `user_type` | dans la requête | `user` pour le jeton de build (restauration et push), `user-cd` pour le jeton de déploiement (get) |

Depuis la page de l'API (interface Swagger) :

1. **S'authentifier** : cliquer sur **Authorize** en haut de la page. La fenêtre « Available authorizations » propose la connexion SG Connect (OAuth2). Laisser le `client_id` prérempli et les trois scopes cochés (`openid`, `profile`, `api.artifacts-api.v1`), puis cliquer sur **Authorize** et se connecter avec son compte personnel. Fermer ensuite la fenêtre avec **Close**.
2. Ouvrir la route `POST /projects/{kear_uid}/tokens`, cliquer sur **Try it out**.
3. Renseigner `kear_uid`, choisir `user_type`, puis **Execute**.

L'opération se fait donc avec un compte personnel, qui doit avoir les droits sur l'application dans l'API Artifacts. Le jeton obtenu appartient en revanche au compte technique (`usr-…` ou `usr-cd-…`), et non à la personne qui l'a créé.

Le jeton créé **n'a pas de date d'expiration** : il reste valide jusqu'à sa révocation. Il n'y a donc pas de renouvellement périodique, mais un jeton exposé (copié dans une note, un mail, une capture) doit être révoqué et remplacé (§ 5).

La réponse a cette forme :

```json
{
  "token": "<jeton>",
  "token_id": "<identifiant du jeton>",
  "user": "<nom du compte>"
}
```

- Le nom du compte à utiliser est **exactement** la valeur de `user` (`usr-…` ou `usr-cd-…`).
- Noter `token_id` : il sert à révoquer ce jeton.
- Copier le jeton **directement à sa destination** (§ 4). Ne jamais le coller dans une note, un mail, un message ou un fichier du dépôt.

## 3. Commandes

**Restauration des paquets NuGet** (build) : la source Artifactory est le dépôt virtuel `nuget-virtual-<kear_uid>`, déclaré dans `src/backend/nuget.config`. Les identifiants n'y sont jamais écrits en clair : ils sont lus dans des variables d'environnement.

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

`nugets\socgen` contient les paquets internes qui ne sont pas sur Artifactory (§ 6).

**Push** (build), la clé est la forme `<compte>:<jeton>` :

```powershell
dotnet nuget push <package>.nupkg --api-key "usr-<kear_uid>:<jeton>" --source https://<artifactory>/artifactory/api/nuget/v3/<dépôt>
```

**Get** (déploiement) :

```powershell
curl.exe -u "usr-cd-<kear_uid>:<jeton>" -L -o <package>.nupkg https://<artifactory>/artifactory/<dépôt>/<package>.nupkg
```

`<dépôt>` est le dépôt NuGet du projet (`nuget-releases-private-<kear_uid>` pour les versions publiées par le runner).

En pratique, ces commandes ne se tapent pas à la main : les scripts lisent les identifiants comme indiqué ci-dessous, et le jeton n'apparaît jamais dans les journaux (`--api-key ***`).

## 4. Où sont rangés les jetons

| Jeton | Emplacement | Lu par |
|---|---|---|
| Build : push (`usr-…`) | variable GitLab `ARTIFACTORY_API_KEY` (masquée), valeur `usr-<kear_uid>:<jeton>` | runner `ci-int`, qui la transmet à `push-artifactory.ps1` |
| Build : restauration (`usr-…`) | variables d'environnement `ARTIFACTORY_BUILD_USERNAME` et `ARTIFACTORY_BUILD_TOKEN`, lues par `nuget.config` | runner `ci-int` (à mettre en place, § 6) ; sur un poste de développeur, à définir dans la session |
| Déploiement : get (`usr-cd-…`) | variables d'environnement `ARTIFACTORY_USERNAME` (`usr-cd-<kear_uid>`) et `ARTIFACTORY_TOKEN` (`<jeton>`) | `download_deploy.ps1` |

Pour la variable GitLab : **Settings > CI/CD > Variables**, voir le HOWTO du runner.

## 5. Renouveler ou révoquer un jeton

1. Créer le nouveau jeton (§ 2).
2. Le mettre à sa place (§ 4).
3. Vérifier que l'opération fonctionne (une publication ou un téléchargement).
4. Seulement ensuite, révoquer l'ancien jeton avec son `token_id`.

Révoquer d'abord coupe le push ou le téléchargement jusqu'à la mise en place du nouveau jeton.

Après la correction d'un push en échec, le commit n'est pas republié automatiquement : supprimer sa ligne dans `D:\ci\runner\processed-publish.txt`, ou fusionner un nouveau commit dans `main`.

## 6. Restauration des paquets NuGet depuis Artifactory

Vérifié sur un poste de développeur, restauration forcée dans un dossier vide (`dotnet restore --packages <dossier vide> --no-cache --force`) avec le compte `user` :

| Sources déclarées | Résultat |
|---|---|
| Artifactory seul | tous les paquets publics restaurés ; 4 erreurs `NU1101` sur les paquets internes `SocGen.ApiBank.*` |
| Artifactory et `nugets\socgen` | restauration complète |

Les échecs des essais précédents (403 au téléchargement, `NU1102`) venaient d'une inversion entre les comptes `user` et `user-cd`.

Conséquences :

- le dossier `nugets` peut être réduit à `nugets\socgen` : les paquets publics viennent d'Artifactory ;
- `nuget.config` déclare `ArtifactoryV2` et `OfflineSocGen` (§ 3) ;
- le runner `ci-int` doit fournir `ARTIFACTORY_BUILD_USERNAME` et `ARTIFACTORY_BUILD_TOKEN` au build et au package (à mettre en place).

Reste à voir avec l'équipe Artifactory : la publication des paquets internes `SocGen.ApiBank.*` sur Artifactory, ce qui permettrait de supprimer complètement le dossier `nugets`.

Ne jamais commiter un `nuget.config` contenant des identifiants en clair.
