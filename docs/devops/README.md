# Scripts devops STYX

Copie des scripts de `src\scripts\devops` (état de la PR « devops : optimisation » du dépôt de référence), au format Markdown pour le transfert.

Pour chaque fichier : copier le contenu du bloc de code dans le chemin indiqué, encodé en **UTF-8 avec BOM**. Le README se copie tel quel.

Les identifiants internes (partage réseau, Artifactory) sont masqués par `XXXXX` : à remplacer par les vraies valeurs dans `config\dev.psd1`.

| Fichier | Destination | Rôle |
|---|---|---|
| [config/dev.psd1.md](config/dev.psd1.md) | `src\scripts\devops\config\dev.psd1` | configuration de l'environnement de dev (sans secret) |
| [dev/README.md](dev/README.md) | `src\scripts\devops\dev\README.md` | documentation des scripts de dev |
| [dev/build.ps1.md](dev/build.ps1.md) | `src\scripts\devops\dev\build.ps1` | build complet de la solution (restore, clean, build) |
| [dev/build_debug.ps1.md](dev/build_debug.ps1.md) | `src\scripts\devops\dev\build_debug.ps1` | build de la solution en Debug |
| [dev/build_release.ps1.md](dev/build_release.ps1.md) | `src\scripts\devops\dev\build_release.ps1` | build de la solution en Release |
| [dev/debug_all.ps1.md](dev/debug_all.ps1.md) | `src\scripts\devops\dev\debug_all.ps1` | lance les composants depuis leurs projets en Debug (débogable, user-secrets) |
| [dev/run_all.ps1.md](dev/run_all.ps1.md) | `src\scripts\devops\dev\run_all.ps1` | construit et lance les composants en Release (validation locale) |
| [dev/stop_all.ps1.md](dev/stop_all.ps1.md) | `src\scripts\devops\dev\stop_all.ps1` | arrête les sessions Debug et Release |
| [legacy/generate_package.ps1.md](legacy/generate_package.ps1.md) | `src\scripts\devops\legacy\generate_package.ps1` | génération du package NuGet (ancienne version, non modifiée) |
| [modules/Styx.Processes.psm1.md](modules/Styx.Processes.psm1.md) | `src\scripts\devops\modules\Styx.Processes.psm1` | module commun : démarrage, sessions et arrêt des composants |
| [modules/StyxBuild.psm1.md](modules/StyxBuild.psm1.md) | `src\scripts\devops\modules\StyxBuild.psm1` | module commun : configuration, build, commandes natives |
| [server/00_build/push-artifactory.ps1.md](server/00_build/push-artifactory.ps1.md) | `src\scripts\devops\server\00_build\push-artifactory.ps1` | push du package vers Artifactory |

Non inclus (non transmis) : les autres scripts `legacy\` et `server\` (futur `int\`), `env.example.ps1`.
