# Paquets NuGet du dépôt — commandes

Objectif : que la solution se restaure uniquement avec les paquets du dépôt (`nugets\` et `nugets\socgen\`), sans nuget.org ni Artifactory.

Sauf mention contraire, à lancer dans PowerShell sur le poste de dev, depuis la racine du dépôt styx-poc.

## 1. nuget.config du dépôt

Dans `src\backend\nuget.config`, `<clear />` écarte les sources de la machine (dont nuget.org) ; seules les sources du dépôt restent. Les chemins sont relatifs au fichier.

```xml
<packageSources>
  <clear />
  <add key="Offline" value="..\..\nugets" />
  <add key="OfflineSocGen" value="..\..\nugets\socgen" />
</packageSources>
```

## 2. Lister les paquets manquants

La dernière restauration complète du poste (dossier `$env:TEMP\nuget-w-socgen`) contient tous les paquets de la solution, dépendances comprises. Cette commande liste ceux qui ne sont pas encore dans `nugets\`, sans rien copier :

```powershell
$have = (Get-ChildItem nugets -Recurse -Filter *.nupkg).Name
Get-ChildItem "$env:TEMP\nuget-w-socgen" -Recurse -Filter *.nupkg | Where-Object { $have -notcontains $_.Name -and $_.Name -notlike 'socgen.*' } | Select-Object Name
```

Résultat attendu : une ligne par paquet manquant.

## 3. Copier les paquets manquants

Uniquement si la liste de l'étape 2 est correcte. Dans la même fenêtre (la variable `$have` est réutilisée) :

```powershell
Get-ChildItem "$env:TEMP\nuget-w-socgen" -Recurse -Filter *.nupkg | Where-Object { $have -notcontains $_.Name -and $_.Name -notlike 'socgen.*' } | Copy-Item -Destination nugets -PassThru
```

Résultat attendu : la liste des fichiers copiés dans `nugets\`.

## 4. Vérifier la restauration comme sur une machine neuve

Dossier de paquets vide, sources du `nuget.config` uniquement :

```powershell
Remove-Item -Recurse -Force "$env:TEMP\nuget-test" -ErrorAction SilentlyContinue
```

```powershell
dotnet restore .\src\backend\styxapipoc.sln --packages "$env:TEMP\nuget-test"
```

Résultat attendu : ni `NU1101` (paquet absent), ni `NU1102` (version absente), ni `NU1603` (version approchante). Les avertissements `NU1900` (audit de vulnérabilités, qui interroge nuget.org) ne bloquent pas.

## 5. Vérifier que tout est versionné

Le `.gitignore` ignore les `*.nupkg` : un paquet présent sur le poste mais non versionné manquera sur le POC. Cette commande liste les paquets ignorés :

```powershell
git status --ignored --short nugets
```

Résultat attendu : des lignes `!!` pour les paquets à ajouter. Ils sont ajoutés à l'étape 6.

## 6. Commiter et pousser

Ajouter les paquets, y compris ceux ignorés par le `.gitignore` :

```powershell
git add -f "nugets/*.nupkg"
```

Les guillemets sont nécessaires : c'est git qui interprète le `*`, sous-dossiers compris. Ne jamais lancer `git add -f .`, qui ajouterait tous les fichiers ignorés du dépôt.

Ajouter le `nuget.config` :

```powershell
git add src/backend/nuget.config
```

Vérifier :

```powershell
git status --ignored --short nugets
```

Résultat attendu : aucune ligne `!!`.

```powershell
git status --short
```

Résultat attendu : des `A` pour les `.nupkg`, un `M` pour `nuget.config`, rien d'autre.

```powershell
git commit -m "US6 : paquets NuGet complets et sources limitées au dépôt"
```

```powershell
git push
```

Le runner reprend le nouveau commit de la MR au passage suivant.

## Tester une correction directement sur le POC

Les sources du dernier commit traité restent dans `D:\ci\build` tant qu'aucun nouveau commit n'arrive sur la MR. Sur le POC :

```powershell
notepad D:\ci\build\src\backend\nuget.config
```

```powershell
dotnet restore D:\ci\build\src\backend\styxapipoc.sln
```

```powershell
D:\ci\build\src\scripts\devops\int\build.ps1 -RepositoryRoot D:\ci\build
```
