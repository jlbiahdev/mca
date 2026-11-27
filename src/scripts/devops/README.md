# DevOps Styx

## À quoi sert ce dossier

Ce dossier contient les outils qui fabriquent l'application Styx, la publient et l'installent sur les serveurs.

Ce sont des **scripts PowerShell** : de petits programmes qu'on lance dans une fenêtre PowerShell, sous Windows. Ils remplacent les outils automatiques de GitLab, qui sont bloqués dans l'entreprise.

## Par où commencer

| Vous voulez… | Lisez |
|---|---|
| créer la machine qui fabriquera l'application | [Créer la machine de build](machine-build.md) |
| installer le runner (le programme qui fabrique et publie l'application) sur cette machine | [Installer le runner CI](howto/HOWTO.md) |
| vérifier que le runner fonctionne, ou régler un problème | [Le runner CI](cicd/ci/README.md) |
| créer ou remplacer un jeton Artifactory (la clé d'accès à l'entrepôt des packages) | [Artifactory](artifactory.md) |
| installer l'application sur un serveur | [Le déploiement](cicd/cd/README.md) |
| compiler et lancer l'application sur votre poste (développeur) | [Le poste du développeur](dev/README.md) |

## Ce que contient le dossier

```text
devops/
├── cicd/               le runner CI et le déploiement
├── config/             les réglages de chaque environnement
├── dev/                les scripts du développeur, sur son poste
├── howto/              installer le runner CI : mode d'emploi et fichiers modèles
├── int/                les scripts que le runner lance sur la machine de build
├── modules/            des fonctions partagées par les scripts
├── README.md           ce document
├── artifactory.md      Artifactory : comptes, jetons et commandes
├── machine-build.md    créer la machine de build
├── env.example.ps1     modèle de réglages pour le poste du développeur
├── mytree.ps1          affiche le contenu d'un dossier
└── repair_utf8_bom.ps1 répare l'enregistrement des scripts
```

| Élément | En une phrase |
|---|---|
| [`cicd/`](cicd/README.md) | le runner CI, qui fabrique et publie l'application, et les scripts qui l'installent sur les serveurs |
| `config/` | un fichier de réglages par environnement (`dev.psd1` pour le poste du développeur, `int.psd1` pour l'intégration). Il ne contient aucun mot de passe : seulement le nom de l'endroit où le trouver |
| [`dev/`](dev/README.md) | les scripts du développeur : compiler, lancer et arrêter l'application sur son poste |
| [`howto/`](howto/HOWTO.md) | le mode d'emploi pour installer le runner, et les fichiers à copier |
| [`int/`](int/README.md) | les scripts que le runner lance pour compiler, empaqueter et publier l'application |
| `modules/` | des fonctions utilisées par les scripts ; on ne les lance jamais directement |
| [`artifactory.md`](artifactory.md) | quel compte utiliser sur Artifactory, comment obtenir son jeton, où le ranger |
| [`machine-build.md`](machine-build.md) | les étapes pour créer la machine de build et son compte |
| `env.example.ps1` | pour le développeur : le copier, le compléter, et ne jamais enregistrer la copie dans le dépôt |
| `mytree.ps1` | affiche l'arborescence d'un dossier |
| `repair_utf8_bom.ps1` | à lancer après avoir copié des scripts : `.\repair_utf8_bom.ps1 <dossier>` |

## Règles à respecter

- **Aucun mot de passe ni jeton dans le dépôt** : ni dans un script, ni dans un fichier de réglages, ni dans un journal. Ils sont rangés à part (variables d'environnement, variable GitLab masquée, fichier chiffré sur la machine).
- **Enregistrer les scripts en « UTF-8 avec BOM »** (un format d'enregistrement de fichier). Sinon, Windows PowerShell abîme les accents et certains scripts refusent de démarrer. En cas de doute, lancer `repair_utf8_bom.ps1`.
- **Les briques logicielles (paquets NuGet) viennent d'Artifactory.** Seules celles qu'Artifactory n'a pas restent dans le dépôt, dans `nugets/socgen`. Chaque développeur règle une fois ses identifiants Artifactory : voir [Artifactory, partie 4](artifactory.md#4-poste-du-développeur).
- **La fabrication ne modifie jamais le code source.**
- **Aucun fichier sur un partage réseau** dans le code : le runner n'y a pas accès.
- **Chaque script se termine par un code** : 0 s'il a réussi, 1 s'il a échoué.
