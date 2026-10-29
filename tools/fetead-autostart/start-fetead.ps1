# ─────────────────────────────────────────────────────────────────────────────
# start-fetead.ps1
# Démarre FETEAD (API + pages) s'il n'est pas déjà lancé.
# Prévu pour être lancé à l'ouverture de session Windows (dossier Démarrage),
# sans droits administrateur.
# ─────────────────────────────────────────────────────────────────────────────

# À adapter : l'exécutable de FETEAD. Utiliser une COPIE dédiée (voir README),
# pas le dossier bin de Visual Studio : sinon les compilations échouent (fichiers verrouillés).
$FeteadExe = "C:\Tools\Fetead\FeteadApi.Presentation.exe"

# Adresse d'écoute : la même que dans Visual Studio, pour garder les mêmes favoris.
$Url = "https://localhost:7061"

# Environnement ASP.NET Core : "Development" charge aussi appsettings.Development.json,
# comme quand FETEAD est lancé depuis Visual Studio.
$AspNetEnvironment = "Development"

# Journaux de FETEAD (remplacés à chaque démarrage)
$LogFolder = Join-Path $env:LOCALAPPDATA "Fetead"
$LogFile   = Join-Path $LogFolder "fetead.log"
$ErrorFile = Join-Path $LogFolder "fetead-erreurs.log"

# Temps maximal d'attente du démarrage, en secondes
$StartupTimeout = 60

$port = ([uri]$Url).Port

function Test-PortListening {
    param([int]$Port)
    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    return $null -ne $listener
}

# 1. Déjà démarré ? On regarde si quelque chose écoute déjà sur le port.
#    (Cela couvre aussi un FETEAD lancé depuis Visual Studio.)
if (Test-PortListening -Port $port) {
    Write-Output "FETEAD est déjà démarré (port $port occupé)."
    exit 0
}

# 2. L'exécutable existe ?
if (-not (Test-Path $FeteadExe)) {
    Write-Error "Exécutable FETEAD introuvable : $FeteadExe (vérifier `$FeteadExe)"
    exit 1
}

# 3. Démarrage en arrière-plan, sans fenêtre.
#    Le dossier de travail doit être celui de l'exécutable : FETEAD y cherche
#    appsettings.json et le dossier du frontend.
New-Item -ItemType Directory -Path $LogFolder -Force | Out-Null
$env:ASPNETCORE_ENVIRONMENT = $AspNetEnvironment

Start-Process -FilePath $FeteadExe `
    -ArgumentList "--urls", $Url `
    -WorkingDirectory (Split-Path $FeteadExe) `
    -WindowStyle Hidden `
    -RedirectStandardOutput $LogFile `
    -RedirectStandardError $ErrorFile

# 4. On attend que FETEAD écoute réellement sur son port.
for ($i = 0; $i -lt $StartupTimeout; $i++) {
    if (Test-PortListening -Port $port) {
        Write-Output "FETEAD démarré : $Url"
        exit 0
    }
    Start-Sleep -Seconds 1
}

Write-Error "FETEAD n'a pas démarré en $StartupTimeout s : voir $LogFile et $ErrorFile"
exit 1
