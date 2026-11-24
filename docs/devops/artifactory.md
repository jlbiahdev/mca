# Artifactory : comptes, jetons et commandes

Ce document explique quel compte Artifactory utiliser pour chaque opération, comment obtenir son jeton, et où le ranger. Il ne contient aucun secret : les valeurs entre `< >` sont à remplacer.

## 1. Deux comptes, deux usages

Le projet dispose de deux comptes techniques Artifactory. Ils n'ont pas les mêmes droits : **ne pas les inverser**.

| Opération | Compte | Forme du nom | Utilisé par |
|---|---|---|---|
| **Push** : déposer un package | compte projet (`user`) | `usr-<kear_uid>` | runner `ci-int` (publication de `main`), `push-artifactory.ps1` |
| **Get** : télécharger un package | compte de déploiement (`user-cd`) | `usr-cd-<kear_uid>` | `download_deploy.ps1` |

Symptômes d'une inversion ou d'une erreur de compte :

| Code renvoyé | Signification | Action |
|---|---|---|
| **401** Unauthorized | le compte et le jeton ne vont pas ensemble, ou le jeton est expiré ou révoqué | vérifier que le nom du compte est exactement celui renvoyé avec le jeton (§ 2) ; sinon, recréer le jeton |
| **403** Forbidden | le compte est reconnu mais n'a pas le droit pour cette opération | vérifier qu'on utilise le bon compte (tableau ci-dessus) |
| **409** Conflict (push) | cette version existe déjà sur Artifactory | rien à faire : un nouveau commit sur `main` produira une nouvelle version |

## 2. Obtenir un jeton

Les deux jetons s'obtiennent par la même route de l'API Artifacts du Groupe, section « Project management » :

```text
POST /projects/{kear_uid}/tokens?user_type=<type>
```

| Paramètre | Où | Valeur |
|---|---|---|
| `kear_uid` | dans l'adresse (obligatoire) | identifiant KEAR de l'application, le même que celui qui apparaît dans le nom des comptes et des dépôts |
| `user_type` | dans la requête | `user` pour le jeton de push, `user-cd` pour le jeton de get |

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

**Push**, la clé est la forme `<compte>:<jeton>` :

```powershell
dotnet nuget push <package>.nupkg --api-key "usr-<kear_uid>:<jeton>" --source https://<artifactory>/artifactory/api/nuget/v3/<dépôt>
```

**Get** :

```powershell
curl.exe -u "usr-cd-<kear_uid>:<jeton>" -L -o <package>.nupkg https://<artifactory>/artifactory/<dépôt>/<package>.nupkg
```

`<dépôt>` est le dépôt NuGet du projet (`nuget-releases-private-<kear_uid>` pour les versions publiées par le runner).

En pratique, ces commandes ne se tapent pas à la main : les scripts lisent les identifiants comme indiqué ci-dessous, et le jeton n'apparaît jamais dans les journaux (`--api-key ***`).

## 4. Où sont rangés les jetons

| Jeton | Emplacement | Lu par |
|---|---|---|
| Push (`usr-…`) | variable GitLab `ARTIFACTORY_API_KEY` (masquée), valeur `usr-<kear_uid>:<jeton>` | runner `ci-int`, qui la transmet à `push-artifactory.ps1` |
| Get (`usr-cd-…`) | variables d'environnement `ARTIFACTORY_USERNAME` (`usr-cd-<kear_uid>`) et `ARTIFACTORY_TOKEN` (`<jeton>`) | `download_deploy.ps1` |

Pour la variable GitLab : **Settings > CI/CD > Variables**, voir le HOWTO du runner.

## 5. Renouveler ou révoquer un jeton

1. Créer le nouveau jeton (§ 2).
2. Le mettre à sa place (§ 4).
3. Vérifier que l'opération fonctionne (une publication ou un téléchargement).
4. Seulement ensuite, révoquer l'ancien jeton avec son `token_id`.

Révoquer d'abord coupe le push ou le téléchargement jusqu'à la mise en place du nouveau jeton.

Après la correction d'un push en échec, le commit n'est pas republié automatiquement : supprimer sa ligne dans `D:\ci\runner\processed-publish.txt`, ou fusionner un nouveau commit dans `main`.

## 6. Restauration des paquets NuGet depuis Artifactory (en attente)

La compilation utilise aujourd'hui les paquets du dossier `nugets` du dépôt. Les tests de restauration depuis Artifactory avec un compte technique ont donné :

- des refus (403) au téléchargement de paquets publics qui ne sont pas encore en cache dans Artifactory ;
- en API v2, au plus 100 versions par paquet, ce qui masque les versions récentes.

La question des droits est posée à l'équipe Artifactory. Tant qu'elle n'est pas réglée, `nuget.config` ne déclare que les sources locales (`nugets` et `nugets\socgen`).
