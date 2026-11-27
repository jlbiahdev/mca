# `int/` — les scripts lancés par le runner

Le runner ne compile pas l'application lui-même : il lance ces trois scripts, qui sont rangés avec le code de l'application. Personne n'a besoin de les lancer à la main.

| Script | Ce qu'il fait |
|---|---|
| `build.ps1` | vérifie que le code d'une merge request se compile |
| `package.ps1` | compile l'application et fabrique le package `Styx.Publish.<version>.nupkg` |
| `push-artifactory.ps1` | dépose ce package sur Artifactory |

## À savoir

- **Le runner utilise les scripts du code qu'il compile.** Si un développeur ajoute un projet à publier ou déplace un projet à publier dans la solution, il doit mettre à jour `package.ps1` dans la même merge request.
- **Les briques logicielles (paquets NuGet) sont téléchargées à chaque fois** dans le dossier `D:\ci\build\.nuget`, effacé avec le code à chaque nouveau commit. Ainsi, chaque fabrication vérifie qu'Artifactory fournit bien tout ce qu'il faut.
- **Les identifiants Artifactory sont fournis par le runner.** Les scripts ne modifient jamais le code.
- Les réglages (adresse d'Artifactory, configuration de compilation) sont dans `config/int.psd1`.

## Différences avec les scripts du développeur (`dev/`)

| | Poste du développeur (`dev/`) | Machine de build (`int/`) |
|---|---|---|
| Qui lance | le développeur | le runner |
| D'où vient le code | le dépôt git du poste | une copie téléchargée par le runner, sans git |
| Compilation | Debug ou Release, au choix | toujours Release |
| Briques logicielles | réutilisées d'une fois sur l'autre | téléchargées à chaque fois |

## Lancer un script à la main (dépannage)

Sur la machine de build, dans une fenêtre PowerShell, après avoir posé les deux variables `ARTIFACTORY_BUILD_USERNAME` et `ARTIFACTORY_BUILD_TOKEN` dans la fenêtre :

```powershell
D:\ci\build\src\scripts\devops\int\build.ps1 -RepositoryRoot D:\ci\build
```

Vous devez voir, à la fin : `Build Release terminé avec succès.`
