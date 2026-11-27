# `cd/` — installer l'application sur les serveurs

Ces scripts installent une version de l'application sur un serveur. Ils se lancent à la main, sur le serveur, dans une fenêtre PowerShell ouverte en administrateur.

| Dossier | Ce qu'il fait | Mode d'emploi |
|---|---|---|
| `02_download_deploy/` | télécharge le package depuis Artifactory, puis met à jour les applications demandées sur le serveur : l'API, HpcLite ou Taskflow | [README](02_download_deploy/README.md) |
| `03_deploy/` | met à jour les applications à partir d'un package déjà présent sur le serveur | [README](03_deploy/README.md) |
| `tests_download_deploy/` | les tests de `download_deploy.ps1`, rangés par étape (« jalons » 0 à 19) | [README](tests_download_deploy/README.md) |

Pour télécharger le package, le serveur a besoin du **compte de déploiement** Artifactory et de son jeton, rangés dans deux variables d'environnement, `ARTIFACTORY_USERNAME` et `ARTIFACTORY_TOKEN` : voir [Artifactory](../../artifactory.md).
