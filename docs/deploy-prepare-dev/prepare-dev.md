# prepare-dev — préparation de la machine de dev avant déploiement

`prepare-dev.ps1` crée sur la machine de dev ce qui manque pour que `download_deploy.ps1` puisse déployer l'API, HpcLite et Taskflow. Il se lance **avant** le déploiement.

Le script est fourni dans ce document (section 7) et non en fichier `.ps1`. Pour l'installer, voir la section 3.

---

## 1. Ce que fait le script

| Élément | Si absent | Si présent |
|---|---|---|
| Dossiers `D:\Applications\api`, `taskflow`, `HpcLite\agent`, `HpcLite\runner`, `HpcLite\scheduler` | Créés | Laissés tels quels |
| Pool IIS `styx-api` | Créé : sans code managé, avec le compte de service en identité | Laissé tel quel ; avertissement si son identité n'est pas le compte de service |
| Site IIS | Créé : chemin `D:\Applications\api`, pool `styx-api`, HTTPS sur le port 443, nom d'hôte et certificat de la configuration | Laissé tel quel ; avertissement si son chemin ou son pool diffère |
| Services `HpcLite.Scheduler` et `HpcLite.Agent` | Créés : démarrage automatique, compte de service en ouverture de session, **non démarrés** | Laissés tels quels ; avertissement si leur exécutable diffère |
| Tâche planifiée Taskflow | Créée : à l'ouverture de session du compte de service, une seule instance, sans limite de durée | Laissée telle quelle ; avertissement si son exécutable diffère |

Règles :

- **Le script ne modifie jamais un élément existant.** Il ne fait que créer ce qui manque, et signale ce qui ne correspond pas.
- **Le mot de passe du compte de service est lu dans GitLab**, et seulement si quelque chose doit être créé avec lui (pool ou services). Il n'est jamais affiché ni journalisé.
- **Relancer le script ne crée rien de plus** si tout existe déjà. Après un échec en cours de route, il suffit de le relancer : il crée ce qui manque encore.
- **`-ValidationOnly`** fait toutes les vérifications, lit le mot de passe dans GitLab si nécessaire (pour vérifier que la lecture fonctionne), affiche ce qui serait créé, puis s'arrête **sans rien créer**.

---

## 2. Prérequis (une seule fois, à la main)

### 2.1 Compte de service

- Un compte `DOMAINE\compte` administrateur de la machine. **Le script se lance avec ce compte.**
- Le droit **« Ouvrir une session en tant que service »** pour ce compte (nécessaire aux services HpcLite) : `secpol.msc` > Stratégies locales > Attribution des droits utilisateur > Ouvrir une session en tant que service > Ajouter `DOMAINE\compte`.

### 2.2 Ouverture de session automatique

Taskflow est lancé à l'**ouverture de session** du compte de service (les tâches UFX ont besoin d'une session interactive). Pour qu'il soit disponible après chaque redémarrage, la machine doit ouvrir automatiquement la session de ce compte.

Méthode recommandée : l'outil **Autologon** de Microsoft Sysinternals, qui enregistre le mot de passe chiffré (et non en clair dans le registre).

1. Lancer `Autologon.exe` en administrateur.
2. Saisir le nom du compte, le domaine et le mot de passe, puis **Enable**.

Résultat attendu : le message « Autologon successfully configured ». Au redémarrage suivant, la session du compte s'ouvre seule.

À valider avec la sécurité si la machine est partagée.

### 2.3 IIS

- IIS installé, avec les **outils de gestion par script** (module PowerShell `WebAdministration`).
- **ASP.NET Core Hosting Bundle** dans la version .NET de l'API.

Vérification :

```powershell
Get-Module -ListAvailable WebAdministration
```

Résultat attendu : une ligne `WebAdministration`.

### 2.4 Certificat HTTPS

Un certificat pour le nom d'hôte du site doit être installé dans le magasin de l'ordinateur. Relever son empreinte :

```powershell
Get-ChildItem Cert:\LocalMachine\My | Select-Object Thumbprint, Subject, NotAfter
```

Résultat attendu : la liste des certificats. Noter l'empreinte (`Thumbprint`) de celui du site, pour la configuration (section 4).

### 2.5 Variable GitLab du mot de passe

Dans le projet GitLab : Settings > CI/CD > Variables > Add variable.

| Champ | Valeur |
|---|---|
| Key | `STYX_SERVICE_ACCOUNT_PASSWORD` |
| Value | Mot de passe du compte de service |
| Environments | `dev` |
| Visibility | **Masked** (et non « Masked and hidden » : une variable cachée ne peut pas être relue par le script) |

Si GitLab refuse de masquer la valeur, c'est que le mot de passe ne respecte pas ses règles (au moins 8 caractères, certains caractères spéciaux refusés).

### 2.6 Jeton d'accès GitLab

Dans le projet GitLab : Settings > Access tokens > Add new token.

| Champ | Valeur |
|---|---|
| Nom | Par exemple `prepare-dev-poc` |
| Rôle | **Maintainer** (obligatoire pour lire les variables) |
| Scopes | `read_api` et `read_repository` (lecture du code, pour le runner `ci-dev`) |
| Expiration | Selon la règle de l'entreprise ; noter la date pour le renouveler |

Copier le jeton affiché : il ne sera plus montré.

> Un jeton Maintainer peut lire **toutes** les variables du projet. Ne pas mettre dans ce projet les secrets de rec ou prd tant que ce jeton existe sur la machine de dev.

### 2.7 Fichier chiffré du jeton

**En étant connecté avec le compte de service**, sur la machine de dev :

```powershell
New-Item -ItemType Directory -Path D:\ci\styx -Force
```

Résultat attendu : le dossier `D:\ci\styx` est listé.

```powershell
Read-Host "Jeton GitLab" -AsSecureString | Export-Clixml -Path D:\ci\styx\gitlab-token.xml
```

Coller le jeton à l'invite (il ne s'affiche pas), puis Entrée. Résultat attendu : aucun message, et le fichier `D:\ci\styx\gitlab-token.xml` existe.

Le fichier est chiffré par Windows (DPAPI) : **seul ce compte, sur cette machine**, peut le relire. Copié ailleurs ou lu par un autre compte, il est inutilisable.

---

## 3. Installation du script

1. Créer le fichier `D:\ci\styx\prepare-dev.ps1`.
2. Y coller **tout le contenu** du bloc de code de la section 7.
3. L'enregistrer en **UTF-8 avec BOM** (Visual Studio Code : en bas à droite, cliquer sur l'encodage > Save with Encoding > UTF-8 with BOM). Sans BOM, les accents sont corrompus et Windows PowerShell 5.1 peut refuser le script.
4. Renseigner la section `>>> DEBUT CONFIGURATION` (section 4).

---

## 4. Configuration

| Variable | À renseigner |
|---|---|
| `$ServiceAccount` | `DOMAINE\compte` réel |
| `$SiteHostName` | Nom d'hôte du site |
| `$CertificateThumbprint` | Empreinte relevée au § 2.4 |
| `$GitLabUrl` | Adresse de GitLab, sans `/` final (ex. `https://gitlab.exemple.fr`) |
| `$GitLabProjectId` | Identifiant numérique du projet (affiché sur la page d'accueil du projet) |

Les autres valeurs (noms, dossiers, port 443) correspondent à ce qu'attend `download_deploy.ps1` et ne changent pas sans raison.

Le script refuse de démarrer tant que ces cinq valeurs ne sont pas renseignées.

---

## 5. Utilisation

Ouvrir une console PowerShell **en administrateur**, avec le compte de service, puis :

```powershell
cd D:\ci\styx
```

**Étape 1 — Vérification, sans rien créer :**

```powershell
.\prepare-dev.ps1 -ValidationOnly
```

Résultat attendu : la liste de ce qui serait créé, puis `MODE VALIDATION : aucune création effectuée.` et le code de sortie `0`.

**Étape 2 — Création :**

```powershell
.\prepare-dev.ps1
```

Résultat attendu : une ligne `Créé : ...` par élément créé, puis `Préparation terminée.`.

**Étape 3 — Contrôle :** relancer `.\prepare-dev.ps1 -ValidationOnly`. Résultat attendu : `Rien à créer.`

Le journal de chaque exécution est dans `D:\Applications\deployment-logs\prepare-dev-<date>.log`.

Codes de sortie : `0` réussite, `1` erreur (voir le journal).

---

## 6. Limites connues

- **Pas d'installation initiale des binaires.** Le script crée les dossiers, mais `download_deploy.ps1` (v0) exige que les exécutables y soient déjà. Sur une machine neuve, la première copie des binaires reste à faire à la main.
- **Les services HpcLite créés restent arrêtés.** `download_deploy.ps1` ne redémarre que les applications qui tournaient avant le déploiement. Après la première installation des binaires, les démarrer à la main (Scheduler d'abord).
- **Taskflow n'est pas déployé par `download_deploy.ps1`** : il exige un service Windows et refuse `-STP`. Le script crée la tâche planifiée, mais la mise à jour de Taskflow reste à traiter.
- **Taskflow tourne sans élévation** (droits standards de la session, même pour un compte administrateur). À changer si le runner a besoin des droits administrateur.
- **Script non exécuté sur Windows** lors de sa rédaction : à valider avec `-ValidationOnly` avant toute création.

---

## 7. Script

```powershell
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Prépare la machine de dev avant un déploiement : crée ce qui manque
    (dossiers, pool IIS, site IIS, services HpcLite, tâche planifiée Taskflow).
    Ne modifie jamais un élément existant.

.PARAMETER ValidationOnly
    Fait toutes les vérifications et affiche ce qui serait créé, sans rien créer.

.EXAMPLE
    .\prepare-dev.ps1 -ValidationOnly
    .\prepare-dev.ps1
#>
[CmdletBinding()]
param(
    [switch]$ValidationOnly
)

# >>> DEBUT CONFIGURATION ─────────────────────────────────────────────────────

$DestinationRoot = "D:\Applications"
$ServiceAccount  = "DOMAINE\compte"

# API (IIS)
$ApiFolder             = "api"
$ApplicationPoolName   = "styx-api"
$SiteName              = "styx-api"
$SiteHostName          = ""      # à renseigner : nom d'hôte du site
$SitePort              = 443
$CertificateThumbprint = ""      # à renseigner : empreinte du certificat (Cert:\LocalMachine\My)

# Taskflow (tâche planifiée)
$TaskflowFolder         = "taskflow"
$TaskflowExecutableName = "Socgen.TaskFlow.Runner.exe"
$TaskflowTaskName       = "TaskFlow.Runner"

# HpcLite (services Windows ; le Runner est lancé par l'Agent)
$HpcLiteFolder       = "HpcLite"
$HpcLiteRunnerFolder = "runner"
$HpcLiteServices     = @(
    @{ Name = "HpcLite.Scheduler"; Folder = "scheduler"; Executable = "Styx.HpcLite.Scheduler.exe" },
    @{ Name = "HpcLite.Agent";     Folder = "agent";     Executable = "Styx.HpcLite.Agent.exe" }
)

# GitLab : lecture du mot de passe du compte de service
$GitLabUrl             = ""      # à renseigner : ex. https://gitlab.exemple.fr (sans / final)
$GitLabProjectId       = 0       # à renseigner : identifiant numérique du projet
$PasswordVariableKey   = "STYX_SERVICE_ACCOUNT_PASSWORD"
$PasswordVariableScope = "dev"
$GitLabTokenFile       = "D:\ci\styx\gitlab-token.xml"

# Journal
$LogFolder = "D:\Applications\deployment-logs"

# <<< FIN CONFIGURATION ───────────────────────────────────────────────────────

$ErrorActionPreference = "Stop"
$script:LogFile = $null

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

# ── Vérifications ────────────────────────────────────────────────────────────

function Assert-Configuration {
    $missing = @()
    if ($ServiceAccount -eq "DOMAINE\compte" -or $ServiceAccount -notmatch '^[^\\]+\\[^\\]+$') { $missing += '$ServiceAccount' }
    if ([string]::IsNullOrWhiteSpace($SiteHostName))          { $missing += '$SiteHostName' }
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { $missing += '$CertificateThumbprint' }
    if ([string]::IsNullOrWhiteSpace($GitLabUrl))             { $missing += '$GitLabUrl' }
    if ($GitLabProjectId -le 0)                               { $missing += '$GitLabProjectId' }
    if ($missing.Count -gt 0) {
        throw "Configuration incomplète, à renseigner en tête du script : $($missing -join ', ')"
    }

    # Les services sont créés avec un chemin sans guillemets (forme attendue par download_deploy.ps1) :
    # un chemin avec espace serait mal interprété par Windows, donc on le refuse.
    if ($DestinationRoot -match '\s') {
        throw "Le dossier racine ne doit pas contenir d'espace : $DestinationRoot"
    }
}

function Assert-Machine {
    if ($PSVersionTable.PSVersion.Major -ne 5) {
        throw "Windows PowerShell 5.1 requis (version détectée : $($PSVersionTable.PSVersion))."
    }
    if (-not (Test-Path "D:\")) {
        throw "Lecteur D: introuvable."
    }
    if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
        throw "Module WebAdministration introuvable : installer IIS avec les outils de gestion par script."
    }
    Import-Module WebAdministration
    if (-not (Test-Path "Cert:\LocalMachine\My\$CertificateThumbprint")) {
        throw "Certificat introuvable dans Cert:\LocalMachine\My : $CertificateThumbprint"
    }
}

# ── Lecture du mot de passe dans GitLab ──────────────────────────────────────

function Get-ServiceAccountCredential {
    if (-not (Test-Path $GitLabTokenFile)) {
        throw "Fichier du jeton GitLab introuvable : $GitLabTokenFile (voir les prérequis)."
    }

    try {
        $secureToken = Import-Clixml -Path $GitLabTokenFile
    }
    catch {
        throw "Impossible de déchiffrer le jeton GitLab. Le fichier doit avoir été créé par le compte qui lance ce script, sur cette machine."
    }

    # TLS 1.2 n'est pas actif par défaut dans Windows PowerShell 5.1.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $scope = [uri]::EscapeDataString($PasswordVariableScope)
    $uri   = "$GitLabUrl/api/v4/projects/$GitLabProjectId/variables/$PasswordVariableKey" + "?filter%5Benvironment_scope%5D=$scope"
    $token = [System.Net.NetworkCredential]::new("", $secureToken).Password

    try {
        $variable = Invoke-RestMethod -Uri $uri -Method Get -Headers @{ "PRIVATE-TOKEN" = $token }
    }
    catch {
        $status = $null
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        switch ($status) {
            401     { throw "GitLab refuse le jeton (401) : jeton invalide ou expiré." }
            403     { throw "GitLab refuse l'accès aux variables (403) : le jeton doit avoir le rôle Maintainer." }
            404     { throw "Variable $PasswordVariableKey (environnement $PasswordVariableScope) ou projet $GitLabProjectId introuvable (404)." }
            default { throw "Lecture de la variable GitLab impossible : $($_.Exception.Message)" }
        }
    }
    finally {
        $token = $null
    }

    if ([string]::IsNullOrEmpty($variable.value)) {
        throw "La variable $PasswordVariableKey est vide ou illisible (une variable « Masked and hidden » ne peut pas être relue)."
    }

    $securePassword = ConvertTo-SecureString -String $variable.value -AsPlainText -Force
    $variable = $null
    return New-Object System.Management.Automation.PSCredential($ServiceAccount, $securePassword)
}

# ── Programme principal ──────────────────────────────────────────────────────

try {
    Assert-Configuration

    New-Item -ItemType Directory -Path $LogFolder -Force | Out-Null
    $script:LogFile = Join-Path $LogFolder ("prepare-dev-{0}.log" -f (Get-Date -Format "yyyyMMdd-HHmmss"))

    Write-Log "Préparation de la machine de dev ($env:COMPUTERNAME), lancée par $env:USERDOMAIN\$env:USERNAME"
    if ($ValidationOnly) { Write-Log "MODE VALIDATION : rien ne sera créé." }

    Assert-Machine

    $apiPath      = Join-Path $DestinationRoot $ApiFolder
    $taskflowPath = Join-Path $DestinationRoot $TaskflowFolder
    $taskflowExe  = Join-Path $taskflowPath $TaskflowExecutableName
    $hpcLitePath  = Join-Path $DestinationRoot $HpcLiteFolder

    # 1. Inventaire : ce qui manque, et ce qui existe mais ne correspond pas

    $folders = @($DestinationRoot, $apiPath, $taskflowPath, (Join-Path $hpcLitePath $HpcLiteRunnerFolder))
    foreach ($service in $HpcLiteServices) { $folders += Join-Path $hpcLitePath $service.Folder }
    $missingFolders = @($folders | Where-Object { -not (Test-Path $_) })

    $poolPath    = "IIS:\AppPools\$ApplicationPoolName"
    $poolMissing = -not (Test-Path $poolPath)
    if (-not $poolMissing) {
        $poolUser = (Get-Item $poolPath).processModel.userName
        if ($poolUser -ne $ServiceAccount) {
            Write-Log "Le pool $ApplicationPoolName existe avec l'identité « $poolUser » au lieu de $ServiceAccount. Non modifié." "ATTENTION"
        }
    }

    $sitePath    = "IIS:\Sites\$SiteName"
    $siteMissing = -not (Test-Path $sitePath)
    if (-not $siteMissing) {
        $site = Get-Item $sitePath
        if ($site.physicalPath -ne $apiPath) {
            Write-Log "Le site $SiteName pointe sur « $($site.physicalPath) » au lieu de $apiPath. Non modifié." "ATTENTION"
        }
        if ($site.applicationPool -ne $ApplicationPoolName) {
            Write-Log "Le site $SiteName utilise le pool « $($site.applicationPool) » au lieu de $ApplicationPoolName. Non modifié." "ATTENTION"
        }
    }

    $missingServices = @()
    foreach ($service in $HpcLiteServices) {
        $expectedExe = Join-Path (Join-Path $hpcLitePath $service.Folder) $service.Executable
        $existing    = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($service.Name)'"
        if (-not $existing) {
            $missingServices += $service
        }
        elseif ($existing.PathName.Trim('"') -ne $expectedExe) {
            Write-Log "Le service $($service.Name) exécute « $($existing.PathName) » au lieu de $expectedExe. Non modifié." "ATTENTION"
        }
    }

    $existingTask = Get-ScheduledTask -TaskName $TaskflowTaskName -ErrorAction SilentlyContinue
    $taskMissing  = -not $existingTask
    if (-not $taskMissing) {
        $taskExe = $existingTask.Actions[0].Execute
        if ($taskExe -ne $taskflowExe) {
            Write-Log "La tâche $TaskflowTaskName lance « $taskExe » au lieu de $taskflowExe. Non modifiée." "ATTENTION"
        }
    }

    # 2. Plan

    $plan = @()
    foreach ($folder in $missingFolders) { $plan += "dossier $folder" }
    if ($poolMissing) { $plan += "pool IIS $ApplicationPoolName (identité $ServiceAccount)" }
    if ($siteMissing) { $plan += "site IIS $SiteName (https://${SiteHostName}:$SitePort -> $apiPath)" }
    foreach ($service in $missingServices) { $plan += "service $($service.Name) (compte $ServiceAccount)" }
    if ($taskMissing) { $plan += "tâche planifiée $TaskflowTaskName (ouverture de session de $ServiceAccount)" }

    if ($plan.Count -eq 0) {
        Write-Log "Rien à créer."
        exit 0
    }
    Write-Log "À créer :"
    foreach ($item in $plan) { Write-Log "  - $item" }

    # 3. Mot de passe : seulement si le pool ou un service doit être créé

    $credential = $null
    if ($poolMissing -or $missingServices.Count -gt 0) {
        Write-Log "Lecture du mot de passe de $ServiceAccount dans GitLab ($PasswordVariableKey, environnement $PasswordVariableScope)."
        $credential = Get-ServiceAccountCredential
        Write-Log "Mot de passe lu."
    }

    if ($ValidationOnly) {
        Write-Log "MODE VALIDATION : aucune création effectuée."
        exit 0
    }

    # 4. Créations

    foreach ($folder in $missingFolders) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Write-Log "Créé : dossier $folder"
    }

    if ($poolMissing) {
        New-WebAppPool -Name $ApplicationPoolName | Out-Null
        # Chaîne vide = « Aucun code managé », obligatoire pour une API ASP.NET Core.
        Set-ItemProperty $poolPath -Name managedRuntimeVersion -Value ""
        # identityType 3 = compte spécifique (SpecificUser).
        Set-ItemProperty $poolPath -Name processModel -Value @{
            userName     = $credential.UserName
            password     = $credential.GetNetworkCredential().Password
            identityType = 3
        }
        Write-Log "Créé : pool IIS $ApplicationPoolName"
    }

    if ($siteMissing) {
        # SslFlags 1 = SNI : le certificat est associé au nom d'hôte, sans gêner les autres sites sur le port 443.
        New-Website -Name $SiteName -PhysicalPath $apiPath -ApplicationPool $ApplicationPoolName `
            -Port $SitePort -HostHeader $SiteHostName -Ssl -SslFlags 1 | Out-Null
        $binding = Get-WebBinding -Name $SiteName -Protocol "https"
        $binding.AddSslCertificate($CertificateThumbprint, "My")
        Write-Log "Créé : site IIS $SiteName"
    }

    foreach ($service in $missingServices) {
        $exePath = Join-Path (Join-Path $hpcLitePath $service.Folder) $service.Executable
        # Créé arrêté : l'exécutable n'est peut-être pas encore installé.
        New-Service -Name $service.Name -DisplayName $service.Name -BinaryPathName $exePath `
            -StartupType Automatic -Credential $credential | Out-Null
        Write-Log "Créé : service $($service.Name) (arrêté)"
    }

    if ($taskMissing) {
        $action    = New-ScheduledTaskAction -Execute $taskflowExe -WorkingDirectory $taskflowPath
        $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $ServiceAccount
        # Session interactive : nécessaire aux tâches UFX (Excel). Aucun mot de passe à stocker.
        $principal = New-ScheduledTaskPrincipal -UserId $ServiceAccount -LogonType Interactive
        # Sans limite de durée (72 h par défaut) et une seule instance à la fois.
        $settings  = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew `
            -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        Register-ScheduledTask -TaskName $TaskflowTaskName -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings | Out-Null
        Write-Log "Créé : tâche planifiée $TaskflowTaskName"
    }

    $credential = $null
    Write-Log "Préparation terminée."
    exit 0
}
catch {
    Write-Log $_.Exception.Message "ERREUR"
    exit 1
}
```
