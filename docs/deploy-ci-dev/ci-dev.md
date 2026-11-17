# ci-dev — runner de dev par scrutation de GitLab

La création de runners GitLab est bloquée sur l'instance de l'entreprise. Le runner de dev est donc un script, `ci-dev.ps1`, qui tourne sur la machine de dev : **si la branche `main` a changé depuis le dernier déploiement**, il récupère le code, construit le package et le déploie avec `download_deploy.ps1`.

Le script est fourni dans ce document (section 7) et non en fichier `.ps1`.

Il n'utilise que le sens sortant (machine de dev vers GitLab, en HTTPS) et ne demande aucune intervention d'une autre équipe.

---

## 1. Fonctionnement

| Étape | Action |
|---|---|
| 1 | **Verrou** : si une exécution est déjà en cours, le script s'arrête sans rien faire |
| 2 | **Jeton GitLab** : déchiffré depuis `D:\ci\styx\gitlab-token.xml` et transmis à git sans apparaître en ligne de commande ni dans les journaux |
| 3 | **`main` a-t-elle changé ?** Requête légère (`git ls-remote`). Si le commit est celui déjà déployé, le script s'arrête (sauf `-Force`) |
| 4 | **Récupération du code** dans `D:\ci\styx\src`, remis exactement à l'état du commit, sans aucun fichier d'un build précédent |
| 5 | **Build et package** avec `package.ps1` du dépôt ; le `.nupkg` est rangé dans `D:\ci\styx\packages` |
| 6 | **Contrôle préalable** : `download_deploy.ps1 -PackageFile <package> -ValidationOnly` |
| 7 | **Déploiement** : `download_deploy.ps1 -PackageFile <package>` ; en cas d'échec, son retour arrière automatique s'applique |
| 8 | **Fin** : le commit est noté dans `last-deployed.txt` (seulement si tout a réussi) ; seuls les 5 derniers packages sont conservés |

Composants déployés : **API (STX) et HpcLite (STJ)**. Taskflow (STP) n'est pas déployé : `download_deploy.ps1` exige un service Windows, alors que Taskflow tourne en tâche planifiée.

---

## 2. Prérequis

1. **Prérequis et exécution de `prepare-dev`** : compte de service, ouverture de session automatique, fichier chiffré du jeton GitLab (`D:\ci\styx\gitlab-token.xml`), et machine préparée (`prepare-dev.ps1` lancé).
2. **Jeton GitLab** : le même que pour `prepare-dev`, avec les scopes **`read_api` et `read_repository`** (lecture du code).
3. **git 2.31 ou plus** et le **SDK .NET** de la solution, accessibles dans le PATH du compte de service. Vérification :

   ```powershell
   git --version
   ```

   Résultat attendu : `git version 2.31` ou plus.

   ```powershell
   dotnet --list-sdks
   ```

   Résultat attendu : au moins un SDK de la version de la solution.
4. **`download_deploy.ps1`** présent sur la machine, et **API et HpcLite déjà installés** une première fois dans `D:\Applications` (la v0 ne fait que des mises à jour).
5. **`package.ps1`** présent dans le dépôt. Relever son chemin dans le dépôt et vérifier le nom du paramètre qui reçoit le dossier de sortie (le script suppose `-OutputDirectory`).

---

## 3. Configuration

Section `>>> DEBUT CONFIGURATION` en tête du script :

| Variable | À renseigner |
|---|---|
| `$GitRepositoryUrl` | Adresse https du dépôt, **sans identifiant** (bouton Code > Clone with HTTPS dans GitLab) |
| `$PackageScriptRelativePath` | Chemin de `package.ps1` dans le dépôt (ex. `scripts\devops\server\package.ps1`) |
| `$PackageOutputParameter` | Nom du paramètre de `package.ps1` pour le dossier de sortie (`-OutputDirectory` par défaut) |
| `$DeployScript` | Chemin complet de `download_deploy.ps1` sur la machine |

Les autres valeurs (dossier de travail `D:\ci\styx`, branche `main`, racine `D:\Applications`, composants `STX` et `STJ`, 5 packages conservés) ne changent pas sans raison.

Le script refuse de démarrer tant que ces valeurs ne sont pas renseignées.

---

## 4. Niveau 1 — lancement à la main

Ouvrir une console PowerShell **en administrateur**, avec le compte de service :

```powershell
cd D:\ci\styx
```

**Premier lancement :**

```powershell
.\ci-dev.ps1
```

Résultat attendu : les étapes défilent (clonage, build, contrôle préalable, déploiement), puis `Déploiement de <commit> terminé.` Le premier lancement déploie toujours, puisqu'aucun commit n'a encore été déployé.

**Deuxième lancement, sans nouveau commit :**

```powershell
.\ci-dev.ps1
```

Résultat attendu : `Aucun changement sur main (<commit>). Rien à faire.`

**Redéployer le même commit :**

```powershell
.\ci-dev.ps1 -Force
```

Journaux :

- runner : `D:\ci\styx\logs\ci-dev-<date>.log` (un fichier par exécution qui a travaillé) ;
- déploiement : `D:\Applications\deployment-logs\` (journal de `download_deploy.ps1`).

Codes de sortie : `0` réussite, ou rien à faire, ou exécution déjà en cours ; `1` erreur (voir le journal).

---

## 5. Niveau 2 — lancement automatique toutes les 5 minutes

À mettre en place **seulement après avoir validé le niveau 1**.

La tâche tourne dans la session du compte de service, toujours ouverte grâce à l'ouverture de session automatique, avec les droits administrateur (nécessaires au déploiement). Aucun mot de passe n'est stocké dans la tâche.

Console PowerShell en administrateur, en remplaçant `DOMAINE\compte` :

```powershell
$action    = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File D:\ci\styx\ci-dev.ps1" -WorkingDirectory "D:\ci\styx"
$trigger   = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)
$principal = New-ScheduledTaskPrincipal -UserId "DOMAINE\compte" -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 1)
Register-ScheduledTask -TaskName "Styx.CiDev" -Action $action -Trigger $trigger -Principal $principal -Settings $settings
```

Résultat attendu : une ligne `Styx.CiDev` à l'état `Ready`. Sans durée de répétition, la tâche se répète indéfiniment (Windows Server 2016 et plus).

Points à connaître :

- une fenêtre PowerShell peut apparaître brièvement dans la session toutes les 5 minutes ;
- la tâche ne tourne que si la session du compte est ouverte ;
- pour suspendre le runner : `Disable-ScheduledTask Styx.CiDev` ; pour le reprendre : `Enable-ScheduledTask Styx.CiDev`.

---

## 6. Limites connues

- **Seule la branche `main` est traitée.** Les MR ne sont ni compilées ni signalées dans GitLab (niveau 3, non fait).
- **Taskflow n'est pas déployé** (voir § 1).
- **Les secrets applicatifs ne sont pas encore injectés** au déploiement : la configuration du package s'applique, comme avec `download_deploy.ps1`.
- **Le résultat n'est pas visible dans GitLab** : seulement dans les journaux de la machine.
- **Script non exécuté sur Windows** lors de sa rédaction : à valider au niveau 1 avant de passer au niveau 2.

---

## 7. Script

Installation :

1. Créer le fichier `D:\ci\styx\ci-dev.ps1`.
2. Y coller **tout le contenu** du bloc de code ci-dessous.
3. L'enregistrer en **UTF-8 avec BOM** (Visual Studio Code : en bas à droite, cliquer sur l'encodage > Save with Encoding > UTF-8 with BOM).
4. Renseigner la configuration (§ 3).

```powershell
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Runner de dev : si la branche main a changé depuis le dernier déploiement,
    récupère le code, construit le package et le déploie sur cette machine.

.PARAMETER Force
    Reconstruit et redéploie même si main n'a pas changé.

.EXAMPLE
    .\ci-dev.ps1
    .\ci-dev.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force
)

# >>> DEBUT CONFIGURATION ─────────────────────────────────────────────────────

# Dépôt GitLab (adresse https du dépôt, sans identifiant)
$GitRepositoryUrl = ""        # à renseigner : ex. https://gitlab.exemple.fr/groupe/projet.git
$Branch           = "main"

# Dossier de travail du runner
$WorkRoot        = "D:\ci\styx"
$GitLabTokenFile = "D:\ci\styx\gitlab-token.xml"
$KeepPackages    = 5          # nombre de packages conservés

# Script de package, chemin relatif dans le dépôt
$PackageScriptRelativePath = ""   # à renseigner : ex. scripts\devops\server\package.ps1
$PackageOutputParameter    = "-OutputDirectory"   # paramètre de package.ps1 qui reçoit le dossier de sortie

# Déploiement
$DeployScript    = ""         # à renseigner : chemin complet de download_deploy.ps1 sur cette machine
$DestinationRoot = "D:\Applications"
$Components      = @("STX", "STJ")   # STP (Taskflow) exclu : download_deploy.ps1 exige un service Windows

# <<< FIN CONFIGURATION ───────────────────────────────────────────────────────

$ErrorActionPreference = "Stop"
$script:LogFile = $null

$SourceFolder  = Join-Path $WorkRoot "src"
$PackageFolder = Join-Path $WorkRoot "packages"
$LogFolder     = Join-Path $WorkRoot "logs"
$StateFile     = Join-Path $WorkRoot "last-deployed.txt"
$LockFile      = Join-Path $WorkRoot "ci-dev.lock"

# ── Journal ──────────────────────────────────────────────────────────────────

function Write-Log {
    param(
        [string]$Message,
        [ValidateSet("INFO", "ATTENTION", "ERREUR")]
        [string]$Level = "INFO"
    )
    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    $color = switch ($Level) { "ATTENTION" { "Yellow" } "ERREUR" { "Red" } default { "Gray" } }
    Write-Host $line -ForegroundColor $color
    if ($script:LogFile) {
        Add-Content -Path $script:LogFile -Value $line -Encoding UTF8
    }
}

# ── Outils ───────────────────────────────────────────────────────────────────

function Assert-Configuration {
    $missing = @()
    if ([string]::IsNullOrWhiteSpace($GitRepositoryUrl))          { $missing += '$GitRepositoryUrl' }
    if ([string]::IsNullOrWhiteSpace($PackageScriptRelativePath)) { $missing += '$PackageScriptRelativePath' }
    if ([string]::IsNullOrWhiteSpace($DeployScript))              { $missing += '$DeployScript' }
    if ($missing.Count -gt 0) {
        throw "Configuration incomplète, à renseigner en tête du script : $($missing -join ', ')"
    }
    if ($GitRepositoryUrl -match '@') {
        throw "L'adresse du dépôt ne doit contenir aucun identifiant : le jeton est lu dans $GitLabTokenFile."
    }
    if (-not (Test-Path $DeployScript)) {
        throw "Script de déploiement introuvable : $DeployScript"
    }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "git introuvable dans le PATH."
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet introuvable dans le PATH : installer le SDK .NET."
    }
}

# Le jeton GitLab est passé à git par variables d'environnement (et non en ligne de commande),
# pour qu'il n'apparaisse ni dans la liste des processus ni dans les journaux. Nécessite git 2.31 ou plus.
function Set-GitAuthentication {
    if (-not (Test-Path $GitLabTokenFile)) {
        throw "Fichier du jeton GitLab introuvable : $GitLabTokenFile (voir les prérequis)."
    }
    try {
        $secureToken = Import-Clixml -Path $GitLabTokenFile
    }
    catch {
        throw "Impossible de déchiffrer le jeton GitLab. Le fichier doit avoir été créé par le compte qui lance ce script, sur cette machine."
    }
    $token = [System.Net.NetworkCredential]::new("", $secureToken).Password
    $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("oauth2:$token"))
    $token = $null

    $env:GIT_CONFIG_COUNT   = "1"
    $env:GIT_CONFIG_KEY_0   = "http.extraHeader"
    $env:GIT_CONFIG_VALUE_0 = "Authorization: Basic $basic"
    $env:GIT_TERMINAL_PROMPT = "0"   # pas de demande d'identifiant interactive
}

function Clear-GitAuthentication {
    Remove-Item Env:GIT_CONFIG_COUNT, Env:GIT_CONFIG_KEY_0, Env:GIT_CONFIG_VALUE_0 -ErrorAction SilentlyContinue
}

function Assert-GitVersion {
    $text = (git --version) -replace '[^\d\.]', ' '
    $version = [version](($text.Trim() -split '\s+')[0] -replace '^(\d+\.\d+).*$', '$1')
    if ($version -lt [version]"2.31") {
        throw "git 2.31 ou plus requis (version détectée : $version)."
    }
}

# Lance une commande externe, recopie sa sortie dans le journal, échoue si son code de sortie n'est pas 0.
function Invoke-Logged {
    param(
        [string]$Description,
        [string]$FilePath,
        [string[]]$Arguments
    )
    Write-Log $Description
    # Windows PowerShell 5.1 transforme en erreur tout message écrit par la commande sur sa sortie d'erreur
    # (git y écrit sa progression) : on ne juge la commande que sur son code de sortie.
    $ErrorActionPreference = "Continue"
    & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Log "    $_" }
    if ($LASTEXITCODE -ne 0) {
        throw "Échec ($Description), code de sortie $LASTEXITCODE."
    }
}

function Invoke-PowerShellScript {
    param(
        [string]$Description,
        [string]$ScriptPath,
        [string[]]$Arguments
    )
    # Processus séparé : un « exit » du script appelé ne termine pas ce runner.
    $allArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $ScriptPath) + $Arguments
    Invoke-Logged -Description $Description -FilePath "powershell.exe" -Arguments $allArguments
}

# ── Programme principal ──────────────────────────────────────────────────────

$lock = $null
try {
    Assert-Configuration

    foreach ($folder in @($WorkRoot, $PackageFolder, $LogFolder)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }

    # Une seule exécution à la fois : le verrou est libéré automatiquement à la fin du processus.
    try {
        $lock = [System.IO.File]::Open($LockFile, "OpenOrCreate", "ReadWrite", "None")
    }
    catch {
        Write-Host "Une exécution est déjà en cours. Rien à faire."
        exit 0
    }

    Assert-GitVersion
    Set-GitAuthentication

    # 1. main a-t-elle changé ? (requête légère, sans rien télécharger)
    $remoteLine = git ls-remote $GitRepositoryUrl "refs/heads/$Branch"
    if ($LASTEXITCODE -ne 0 -or -not $remoteLine) {
        throw "Impossible de lire la branche $Branch du dépôt (jeton, réseau ou adresse)."
    }
    $remoteCommit = ($remoteLine -split '\s+')[0]
    $lastCommit   = if (Test-Path $StateFile) { (Get-Content $StateFile -Raw).Trim() } else { "" }

    if ($remoteCommit -eq $lastCommit -and -not $Force) {
        Write-Host "Aucun changement sur $Branch ($remoteCommit). Rien à faire."
        exit 0
    }

    $script:LogFile = Join-Path $LogFolder ("ci-dev-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
    Write-Log "Runner de dev sur $env:COMPUTERNAME, lancé par $env:USERDOMAIN\$env:USERNAME"
    Write-Log "Commit à déployer : $remoteCommit (dernier déployé : $(if ($lastCommit) { $lastCommit } else { 'aucun' }))"

    # 2. Récupération du code, à l'état exact du commit (aucun fichier résiduel d'un build précédent)
    if (-not (Test-Path (Join-Path $SourceFolder ".git"))) {
        Invoke-Logged "Clonage du dépôt" "git" @("clone", "--branch", $Branch, "--single-branch", $GitRepositoryUrl, $SourceFolder)
    }
    else {
        Invoke-Logged "Récupération de $Branch" "git" @("-C", $SourceFolder, "fetch", "origin", $Branch)
    }
    Invoke-Logged "Positionnement sur $remoteCommit" "git" @("-C", $SourceFolder, "reset", "--hard", $remoteCommit)
    Invoke-Logged "Nettoyage du dossier de travail" "git" @("-C", $SourceFolder, "clean", "-fdx")
    Clear-GitAuthentication

    # 3. Build et package
    $packageScript = Join-Path $SourceFolder $PackageScriptRelativePath
    if (-not (Test-Path $packageScript)) {
        throw "Script de package introuvable dans le dépôt : $packageScript"
    }
    $startTime = Get-Date
    Invoke-PowerShellScript "Build et package" $packageScript @($PackageOutputParameter, $PackageFolder)

    $package = Get-ChildItem -Path $PackageFolder -Filter "*.nupkg" |
        Where-Object { $_.LastWriteTime -ge $startTime } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $package) {
        throw "Aucun nouveau package trouvé dans $PackageFolder après le build."
    }
    Write-Log "Package produit : $($package.Name)"

    # 4. Contrôle préalable puis déploiement
    $deployArguments = @("-d", $DestinationRoot, "-PackageFile", $package.FullName)
    foreach ($component in $Components) { $deployArguments += "-$component" }

    # Les anciennes versions de download_deploy.ps1 demandent une confirmation, que -Force supprime.
    # Les versions de production n'ont plus ni confirmation ni -Force.
    $deployCommand = Get-Command $DeployScript
    $confirmationArguments = @()
    if ($deployCommand.Parameters.ContainsKey("Force")) { $confirmationArguments = @("-Force") }

    Invoke-PowerShellScript "Contrôle préalable du déploiement" $DeployScript ($deployArguments + @("-ValidationOnly"))
    Invoke-PowerShellScript "Déploiement" $DeployScript ($deployArguments + $confirmationArguments)

    # 5. Fin : on ne note le commit qu'après un déploiement réussi
    Set-Content -Path $StateFile -Value $remoteCommit -Encoding ASCII
    Get-ChildItem -Path $PackageFolder -Filter "*.nupkg" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $KeepPackages |
        ForEach-Object {
            Remove-Item $_.FullName
            Write-Log "Package supprimé (conservation des $KeepPackages derniers) : $($_.Name)"
        }

    Write-Log "Déploiement de $remoteCommit terminé."
    exit 0
}
catch {
    Write-Log $_.Exception.Message "ERREUR"
    exit 1
}
finally {
    Clear-GitAuthentication
    if ($lock) { $lock.Dispose() }
}
```
