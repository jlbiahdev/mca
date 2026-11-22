# Paquets NuGet internes — commandes

À lancer sur le poste de dev, depuis la racine du dépôt styx-poc.

## 1. Versions demandées par les projets

```powershell
Get-ChildItem .\src\backend -Recurse -Filter *.csproj | Select-String -Pattern 'SocGen\.ApiBank[^"]*"\s+Version="[^"]*"' | ForEach-Object { $_.Matches[0].Value } | Sort-Object -Unique
```

Résultat attendu : une ligne par paquet interne et par version, par exemple `SocGen.ApiBank.Security.Authentication" Version="x.y.z"`.

## 2. Versions présentes dans le dépôt

```powershell
Get-ChildItem .\nugets\socgen -Recurse -Filter *.nupkg | ForEach-Object { $_.Name } | Sort-Object
```

## 3. Ajouter au dépôt les paquets internes du cache du poste

Le cache NuGet du poste (`%USERPROFILE%\.nuget\packages`) contient les paquets avec lesquels la solution compile. `dotnet nuget push` vers un dossier les range dans la structure attendue par NuGet (`<paquet>\<version>\`) ; les paquets déjà présents sont ignorés.

```powershell
Get-ChildItem "$env:USERPROFILE\.nuget\packages" -Directory -Filter 'socgen.apibank.*' | Get-ChildItem -Recurse -Filter *.nupkg | ForEach-Object { dotnet nuget push $_.FullName --source "$PWD\nugets\socgen" --skip-duplicate }
```

Résultat attendu : une ligne `Your package was pushed.` par paquet ajouté, ou un message indiquant qu'il existe déjà.

## 4. Vérifier la restauration comme sur une machine neuve

Seules les sources du dépôt sont autorisées, et le dossier de paquets est vide : un paquet manquant provoque une erreur `NU1101`.

```powershell
dotnet restore .\src\backend\styxapipoc.sln --source "$PWD\nugets" --source "$PWD\nugets\socgen" --packages "$env:TEMP\nuget-test"
```

Résultat attendu : aucune erreur `NU1101`.

```powershell
Remove-Item "$env:TEMP\nuget-test" -Recurse -Force
```

## 5. nuget.config du dépôt

Dans `src\backend\nuget.config`, remplacer la source `Offline` par les deux sources du dépôt (chemins relatifs au fichier) :

```xml
<add key="Offline" value="..\..\nugets" />
<add key="OfflineSocGen" value="..\..\nugets\socgen" />
```
