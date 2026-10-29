# ─────────────────────────────────────────────────────────────────────────────
# start-postgres.ps1
# Démarre la base PostgreSQL locale si elle n'est pas déjà lancée.
# Prévu pour être lancé à l'ouverture de session Windows (dossier Démarrage),
# sans droits administrateur et indépendamment de FETEAD.
# ─────────────────────────────────────────────────────────────────────────────

# À adapter : dossier qui contient pg_ctl.exe, et dossier des données PostgreSQL
$PgBin  = "C:\pgsql\bin"
$PgData = "C:\pgsql\data"

# Journal du démarrage de PostgreSQL (écrit par pg_ctl)
$Log = Join-Path $PgData "demarrage.log"

$PgCtl = Join-Path $PgBin "pg_ctl.exe"

if (-not (Test-Path $PgCtl)) {
    Write-Error "pg_ctl.exe introuvable : $PgCtl (vérifier `$PgBin)"
    exit 1
}

# pg_ctl status renvoie :
#   0 = PostgreSQL tourne déjà
#   3 = PostgreSQL est arrêté
#   4 = le dossier de données est introuvable ou invalide
& $PgCtl status -D $PgData | Out-Null
$status = $LASTEXITCODE

if ($status -eq 0) {
    Write-Output "PostgreSQL est déjà démarré."
    exit 0
}

if ($status -eq 4) {
    Write-Error "Dossier de données PostgreSQL invalide : $PgData (vérifier `$PgData)"
    exit 1
}

# -w : attend que PostgreSQL soit vraiment prêt avant de rendre la main
& $PgCtl start -D $PgData -l $Log -w

if ($LASTEXITCODE -eq 0) {
    Write-Output "PostgreSQL démarré."
    exit 0
}

Write-Error "Échec du démarrage de PostgreSQL : voir $Log"
exit 1
