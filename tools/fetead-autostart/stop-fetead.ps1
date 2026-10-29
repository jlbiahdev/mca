# ─────────────────────────────────────────────────────────────────────────────
# stop-fetead.ps1
# Arrête le FETEAD lancé en arrière-plan, pour libérer le port
# (par exemple avant de lancer FETEAD depuis Visual Studio).
# ─────────────────────────────────────────────────────────────────────────────

# Même adresse que dans start-fetead.ps1
$Url = "https://localhost:7061"

$port = ([uri]$Url).Port
$listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue

if ($null -eq $listener) {
    Write-Output "Aucun programme n'écoute sur le port $port : rien à arrêter."
    exit 0
}

# Le processus propriétaire du port (un seul, même s'il écoute en IPv4 et IPv6)
$processId = ($listener | Select-Object -First 1).OwningProcess
$process   = Get-Process -Id $processId -ErrorAction SilentlyContinue

# Sécurité : on n'arrête que FETEAD, jamais un autre programme qui occuperait le port.
if ($null -eq $process -or $process.ProcessName -ne "FeteadApi.Presentation") {
    Write-Error "Le port $port est occupé par un autre programme ($($process.ProcessName)) : rien n'est arrêté."
    exit 1
}

Stop-Process -Id $processId
Write-Output "FETEAD arrêté."
