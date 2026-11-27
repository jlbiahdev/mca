# `cicd/` — fabriquer, publier et installer l'application

Ce dossier contient deux choses, qui se suivent :

| Dossier | Ce qu'il fait | Mode d'emploi |
|---|---|---|
| `ci/` | **Fabriquer et publier.** Le runner tourne tout seul, chaque minute, sur la machine de build. Il vérifie que le code de chaque merge request se compile, et, à chaque mise à jour de la branche `main`, il fabrique le package de l'application et le dépose sur Artifactory. | [Le runner CI](ci/README.md) |
| `cd/` | **Installer.** Des scripts téléchargent le package depuis Artifactory et installent l'application sur les serveurs. | [Le déploiement](cd/README.md) |

Le runner ne fait que fabriquer et publier : il n'installe rien sur les serveurs. L'installation se lance encore à la main.

Les deux étapes n'utilisent pas le même compte Artifactory : voir [Artifactory](../artifactory.md).
