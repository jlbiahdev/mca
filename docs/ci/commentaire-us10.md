**US 10 — Publication sur Artifactory (CD)**

**Validation**

| Cas | Résultat |
|---|---|
| CI renommée | MR !7, !8, !9 construites, statut `ci-int` |
| Premier passage, `main` sans les scripts de publication | publication impossible (voir ci-dessous) |
| Fusion de la MR des scripts (!7) | version `1.0.913254` publiée sur Artifactory |
| Fusion suivante (!8) | version `1.0.913264`, plus grande : une version par fusion |
| Clé Artifactory fausse (!9) | `Push échoué (code 1)`, statut `cd-int` « échoué », pas de nouvelle tentative |
| Clé dans les journaux | absente : `--api-key ***` |

Le statut `cd-int` apparaît sur le commit de `main` (pipeline `#<n>`, version `1.0.<n>`) ; le job ouvre les journaux du package et du push.

**Écart corrigé pendant l'US**

Au premier passage, le dernier commit de `main` ne contenait pas encore `int/package.ps1`. Le runner traitait cette absence comme une erreur du runner : statut « échoué » sur `main`, commit non noté, et nouveau téléchargement des sources (578 Mo) à chaque passage. Or c'est une propriété du commit, pas une panne. Le runner vérifie désormais par l'API que le commit contient les scripts de publication, avant tout statut et tout téléchargement ; sinon, il note le commit `ignored`. L'enchaînement du ticket a été remis dans l'ordre naturel : installer le runner, puis fusionner les scripts, que le runner construit et publie.

**Décisions prises pendant l'US**

- Les scripts de package et de push restent dans le dépôt : le package dépend de la structure du code et doit évoluer avec lui, par les mêmes MR.
- La clé Artifactory est une variable GitLab (`ARTIFACTORY_API_KEY`, tous les environnements, `Masked`), pour une seule mise à jour à chaque renouvellement ; le jeton du runner a donc le rôle Maintainer.

**À retenir**

- Le jeton `cicd-int-poc-api` expire dans 3 mois : le renouveler et le rechiffrer sur la machine du runner avant cette date.
- Un push en échec n'est pas retenté : après correction (clé renouvelée, par exemple), supprimer la ligne du commit dans `processed-cd.txt`, ou fusionner un nouveau commit.
