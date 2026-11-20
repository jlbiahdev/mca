# Styx.Processes.psm1 — module commun : démarrage, session et arrêt des composants

Contenu du fichier, à copier dans `src\scripts\devops\modules\Styx.Processes.psm1`.

À enregistrer en UTF-8 avec BOM (Windows PowerShell 5.1).

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Démarrage et arrêt de la session locale STYX (run_all.ps1, stop_all.ps1).

.DESCRIPTION
    Ce module gère :
    - la copie des sorties de build dans un dossier unique
      <RepositoryRoot>\.run (un seul jeu de binaires, toujours celui du
      dernier lancement) ;
    - le démarrage des composants dans l'ordre TaskFlow, Agent, Scheduler, API ;
    - la session .run\session.json, mise à jour après chaque composant ;
    - l'identification des processus par PID et heure de démarrage, afin de
      ne jamais arrêter un processus qui aurait récupéré le même PID ;
    - l'arrêt propre puis, sur demande, forcé.

    Le Runner n'est jamais démarré ni arrêté par ce module : il est lancé
    par l'Agent (chemin lu en base) et s'arrête de lui-même.

    Les primitives de build et la configuration relèvent de StyxBuild.psm1.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'StyxBuild.psm1') -ErrorAction Stop

# Ordre de démarrage. L'arrêt se fait dans l'ordre inverse.
# HPC Lite Runner est copié mais jamais démarré ni arrêté par les scripts.
$script:StartupOrder = @(
    [pscustomobject]@{ Name = 'TaskFlow Runner';    Folder = 'taskflow';          Url = $null }
    [pscustomobject]@{ Name = 'HPC Lite Agent';     Folder = 'hpclite\agent';     Url = 'Agent' }
    [pscustomobject]@{ Name = 'HPC Lite Scheduler'; Folder = 'hpclite\scheduler'; Url = $null }
    [pscustomobject]@{ Name = 'STYX Web API';       Folder = 'api';               Url = 'Api' }
)

# Composants copiés dans .run ; tous sauf le Runner sont démarrés.
$script:ComponentFolders = @(
    'api'
    'hpclite\scheduler'
    'hpclite\agent'
    'hpclite\runner'
    'taskflow'
)

# Durée minimale pendant laquelle un composant doit rester actif après
# son lancement pour être considéré comme démarré.
$script:MinimumAliveSeconds = 3

# ============================================================
# Ports
# ============================================================

function ConvertTo-StyxTcpPort {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [object] $Value
    )

    $port = 0

    if (-not [int]::TryParse([string] $Value, [ref] $port)) {
        throw "Le port '$Name' n'est pas un entier valide : $Value"
    }

    if ($port -lt 1 -or $port -gt 65535) {
        throw "Le port '$Name' doit être compris entre 1 et 65535 : $port"
    }

    return $port
}

function Assert-StyxDistinctPorts {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Ports
    )

    $duplicates = @(
        $Ports.GetEnumerator() |
            Group-Object -Property Value |
            Where-Object { $_.Count -gt 1 }
    )

    if ($duplicates.Count -eq 0) {
        return
    }

    $messages = foreach ($duplicate in $duplicates) {
        $names = @($duplicate.Group | ForEach-Object { $_.Key }) -join ', '
        "port $($duplicate.Name) utilisé par : $names"
    }

    throw ("Des ports sont utilisés plusieurs fois : " + ($messages -join '; '))
}

function Test-StyxPortListening {
    param(
        [Parameter(Mandatory = $true)]
        [int] $Port
    )

    $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()

    foreach ($listener in $listeners) {
        if ($listener.Port -eq $Port) {
            return $true
        }
    }

    return $false
}

function Assert-StyxPortsFree {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Ports
    )

    $busy = @(
        foreach ($entry in $Ports.GetEnumerator()) {
            if (Test-StyxPortListening -Port $entry.Value) {
                "$($entry.Key) ($($entry.Value))"
            }
        }
    )

    if ($busy.Count -gt 0) {
        throw (
            "Ports déjà utilisés sur ce poste : " + ($busy -join ', ') + '. ' +
            "Une session STYX ou un service (TaskFlow en service Windows, par exemple) tourne peut-être déjà."
        )
    }
}

# ============================================================
# Processus
# ============================================================

function Get-StyxProcessStartTime {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $Process
    )

    try {
        return $Process.StartTime.ToUniversalTime().ToString('o')
    }
    catch {
        # Accès refusé (processus d'un autre utilisateur) ou processus terminé.
        return $null
    }
}

<#
.SYNOPSIS
    Retourne le processus d'une entrée de session (composant ou processus
    enfant), ou $null.

.DESCRIPTION
    Le processus n'est retourné que si son heure de démarrage correspond à
    celle enregistrée : un processus ayant récupéré le même PID est ignoré.
    Les sessions écrites avant l'enregistrement de l'heure de démarrage sont
    identifiées par le PID seul.
#>
function Get-StyxSessionProcess {
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Entry
    )

    $processIdProperty = $Entry.PSObject.Properties['ProcessId']

    if ($null -eq $processIdProperty) {
        return $null
    }

    $processId = 0

    if (-not [int]::TryParse([string] $processIdProperty.Value, [ref] $processId) -or $processId -le 0) {
        return $null
    }

    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue

    if ($null -eq $process) {
        return $null
    }

    $startTimeProperty = $Entry.PSObject.Properties['StartTime']

    if ($null -eq $startTimeProperty -or [string]::IsNullOrWhiteSpace([string] $startTimeProperty.Value)) {
        return $process
    }

    # Selon la version de PowerShell, ConvertFrom-Json rend la date sous
    # forme de chaîne ou de DateTime : on compare donc des instants UTC.
    $recordedValue = $startTimeProperty.Value

    if ($recordedValue -is [datetime]) {
        $recordedTicks = $recordedValue.ToUniversalTime().Ticks
    }
    else {
        $recordedTicks = [DateTime]::Parse(
            [string] $recordedValue,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind
        ).ToUniversalTime().Ticks
    }

    $actualStartTime = Get-StyxProcessStartTime -Process $process
    $actualTicks = $null

    if ($null -ne $actualStartTime) {
        $actualTicks = [DateTime]::Parse(
            $actualStartTime,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind
        ).ToUniversalTime().Ticks
    }

    if ($actualTicks -ne $recordedTicks) {
        # Le PID a été réattribué à un autre processus.
        $process.Dispose()
        return $null
    }

    return $process
}

function Invoke-StyxTaskKill {
    param(
        [Parameter(Mandatory = $true)]
        [int] $ProcessId,

        [switch] $Force
    )

    $arguments = @('/PID', [string] $ProcessId, '/T')

    if ($Force) {
        $arguments += '/F'
    }

    return Invoke-StyxNativeQuery -FilePath 'taskkill.exe' -Arguments $arguments
}

<#
.SYNOPSIS
    Arrête un composant et ses processus enfants : demande de fermeture,
    attente, puis arrêt forcé de l'arbre si -Force est indiqué.

.DESCRIPTION
    La demande de fermeture passe par la fenêtre de console du composant
    (CloseMainWindow), à défaut par taskkill /T sans /F. Tous les processus
    attachés à la console la reçoivent ; les applications .NET hébergées
    (Generic Host) la traitent comme un arrêt propre.

    Retourne la liste des processus encore actifs (vide si tout est arrêté).
#>
function Stop-StyxComponentProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $HostProcess,

        [System.Diagnostics.Process[]] $Children = @(),

        [Parameter(Mandatory = $true)]
        [int] $TimeoutSeconds,

        [switch] $Force
    )

    $all = @($Children) + @($HostProcess)

    $alive = {
        @($all | Where-Object { $_.Refresh(); -not $_.HasExited })
    }

    if (@(& $alive).Count -eq 0) {
        return @()
    }

    # 1. Demande de fermeture.
    $closeRequested = $false
    $HostProcess.Refresh()

    if (-not $HostProcess.HasExited) {
        try {
            if ($HostProcess.MainWindowHandle -ne [IntPtr]::Zero) {
                $closeRequested = $HostProcess.CloseMainWindow()
            }
        }
        catch {
            $closeRequested = $false
        }

        if (-not $closeRequested) {
            [void] (Invoke-StyxTaskKill -ProcessId $HostProcess.Id)
        }
    }
    else {
        # L'hôte est arrêté mais un enfant reste actif.
        foreach ($child in @(& $alive)) {
            [void] (Invoke-StyxTaskKill -ProcessId $child.Id)
        }
    }

    # 2. Attente.
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    while (@(& $alive).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    $remaining = @(& $alive)

    if ($remaining.Count -eq 0 -or -not $Force) {
        return $remaining
    }

    # 3. Arrêt forcé de l'arbre.
    foreach ($process in $remaining) {
        Write-StyxInfo "Arrêt forcé de l'arbre de processus (PID $($process.Id))." -ForegroundColor Yellow
        [void] (Invoke-StyxTaskKill -ProcessId $process.Id -Force)
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(5)

    while (@(& $alive).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    return @(& $alive)
}

# ============================================================
# Session
# ============================================================

<#
.SYNOPSIS
    Retourne le dossier d'exécution unique et le fichier de session.
#>
function Get-StyxRunRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    return Join-Path $RepositoryRoot '.run'
}

function Get-StyxSessionFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    return Join-Path (Get-StyxRunRoot -RepositoryRoot $RepositoryRoot) 'session.json'
}

<#
.SYNOPSIS
    Retourne les processus du Runner en cours d'exécution depuis .run.

.DESCRIPTION
    Sert uniquement à ne pas remplacer les binaires d'un Runner qui
    travaille : le Runner n'est jamais arrêté par les scripts.
#>
function Get-StyxRunningRunners {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RunnerDirectory
    )

    $prefix = [IO.Path]::GetFullPath($RunnerDirectory).TrimEnd('\') + '\'

    return @(
        Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $path = $null

            try {
                $path = $_.Path
            }
            catch {
                $path = $null
            }

            -not [string]::IsNullOrEmpty($path) -and
                $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
        }
    )
}

function Read-StyxSession {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SessionFile
    )

    try {
        return Get-Content -LiteralPath $SessionFile -Raw -ErrorAction Stop |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Le fichier de session est illisible : $SessionFile. $($_.Exception.Message)"
    }
}

<#
.SYNOPSIS
    Écrit la session dans un fichier temporaire puis le renomme, afin de ne
    jamais laisser un session.json partiellement écrit.
#>
function Save-StyxSession {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary] $Session,

        [Parameter(Mandatory = $true)]
        [string] $SessionFile
    )

    $temporaryFile = "$SessionFile.tmp"

    $Session |
        ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $temporaryFile -Encoding UTF8 -Force -ErrorAction Stop

    Move-Item -LiteralPath $temporaryFile -Destination $SessionFile -Force -ErrorAction Stop
}

function Assert-NoActiveStyxSession {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SessionFile
    )

    if (-not (Test-Path -LiteralPath $SessionFile -PathType Leaf)) {
        return
    }

    $session = Read-StyxSession -SessionFile $SessionFile
    $activePids = @()

    foreach ($component in @($session.Components)) {
        $entries = @($component)
        $childrenProperty = $component.PSObject.Properties['Children']

        if ($null -ne $childrenProperty -and $null -ne $childrenProperty.Value) {
            $entries += @($childrenProperty.Value)
        }

        foreach ($entry in $entries) {
            $process = Get-StyxSessionProcess -Entry $entry

            if ($null -ne $process) {
                $activePids += $process.Id
                $process.Dispose()
            }
        }
    }

    if ($activePids.Count -gt 0) {
        throw (
            "Une session STYX est déjà active. PID : " + ($activePids -join ', ') +
            ". Session : $SessionFile. Lancer stop_all.ps1 avant de redémarrer."
        )
    }

    # La session précédente ne contient plus aucun processus actif.
    Remove-Item -LiteralPath $SessionFile -Force -ErrorAction Stop
}

<#
.SYNOPSIS
    Arrête les composants d'une session, dans l'ordre inverse du démarrage.

.DESCRIPTION
    Le fichier de session n'est supprimé que si tous les composants sont
    arrêtés ; sinon il est conservé pour une nouvelle tentative et une
    exception est levée.
#>
function Stop-StyxSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $SessionFile,

        [ValidateRange(1, 300)]
        [int] $TimeoutSeconds = 10,

        [switch] $Force
    )

    $session = Read-StyxSession -SessionFile $SessionFile
    $componentsProperty = $session.PSObject.Properties['Components']

    if ($null -eq $componentsProperty) {
        throw "Le fichier '$SessionFile' ne contient pas de propriété 'Components'."
    }

    $components = @($componentsProperty.Value)
    [array]::Reverse($components)

    $errors = New-Object 'Collections.Generic.List[string]'

    foreach ($component in $components) {
        $name = 'Composant inconnu'
        $nameProperty = $component.PSObject.Properties['Name']

        if ($null -ne $nameProperty -and -not [string]::IsNullOrWhiteSpace([string] $nameProperty.Value)) {
            $name = [string] $nameProperty.Value
        }

        $hostProcess = Get-StyxSessionProcess -Entry $component
        $children = @()
        $childrenProperty = $component.PSObject.Properties['Children']

        if ($null -ne $childrenProperty -and $null -ne $childrenProperty.Value) {
            $children = @(
                foreach ($childEntry in @($childrenProperty.Value)) {
                    $child = Get-StyxSessionProcess -Entry $childEntry

                    if ($null -ne $child) {
                        $child
                    }
                }
            )
        }

        if ($null -eq $hostProcess -and $children.Count -eq 0) {
            Write-StyxInfo "[--] $name est déjà arrêté."
            continue
        }

        # Si l'hôte (dotnet run) est déjà arrêté, le premier enfant actif
        # sert de point d'entrée pour la demande de fermeture.
        if ($null -eq $hostProcess) {
            $hostProcess = $children[0]
            $children = @($children | Select-Object -Skip 1)
        }

        $pids = (@($hostProcess) + $children | ForEach-Object { $_.Id }) -join ', '

        try {
            Write-StyxInfo "[..] Arrêt de $name (PID $pids)..."

            $remaining = @(
                Stop-StyxComponentProcesses `
                    -HostProcess $hostProcess `
                    -Children $children `
                    -TimeoutSeconds $TimeoutSeconds `
                    -Force:$Force
            )

            if ($remaining.Count -eq 0) {
                Write-StyxInfo "[OK] $name arrêté." -ForegroundColor Green
            }
            else {
                $remainingPids = ($remaining | ForEach-Object { $_.Id }) -join ', '

                if ($Force) {
                    $errors.Add("$name (PID $remainingPids) : toujours actif après l'arrêt forcé.")
                }
                else {
                    $errors.Add("$name (PID $remainingPids) : toujours actif après $TimeoutSeconds s. Relancer avec -Force pour forcer l'arrêt.")
                }
            }
        }
        catch {
            $errors.Add("$name (PID $pids) : $($_.Exception.Message)")
        }
        finally {
            foreach ($process in @($hostProcess) + $children) {
                $process.Dispose()
            }
        }
    }

    if ($errors.Count -gt 0) {
        throw ($errors -join [Environment]::NewLine)
    }

    Remove-Item -LiteralPath $SessionFile -Force -ErrorAction Stop
}

# ============================================================
# Démarrage
# ============================================================

function Get-StyxProjectOutputDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Project,

        [Parameter(Mandatory = $true)]
        [string] $ExecutableName,

        [Parameter(Mandatory = $true)]
        [string] $Configuration
    )

    $buildDirectory = Join-Path (Split-Path -Parent $Project) (Join-Path 'bin' $Configuration)

    if (-not (Test-Path -LiteralPath $buildDirectory -PathType Container)) {
        throw "Répertoire de build $Configuration introuvable : $buildDirectory"
    }

    # Le build a déjà été validé : cette recherche sert uniquement à
    # retrouver le répertoire à copier sans coder en dur le framework cible.
    $executable = Get-ChildItem `
        -LiteralPath $buildDirectory `
        -Filter $ExecutableName `
        -File `
        -Recurse `
        -ErrorAction Stop |
        Where-Object { $_.DirectoryName -notmatch '[\\/](ref|refint)[\\/]' } |
        Sort-Object -Property LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $executable) {
        throw "La sortie de build de '$ExecutableName' est introuvable sous '$buildDirectory'."
    }

    return $executable.DirectoryName
}

function Copy-StyxProjectOutput {
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $Project,

        [Parameter(Mandatory = $true)]
        [string] $Configuration,

        [Parameter(Mandatory = $true)]
        [string] $Destination
    )

    $sourceDirectory = Get-StyxProjectOutputDirectory `
        -Project $Project.Project `
        -ExecutableName $Project.Executable `
        -Configuration $Configuration

    Write-StyxInfo ("Copie de {0} : {1} -> {2}" -f $Project.Name, $sourceDirectory, $Destination)

    # On remplace entièrement la sortie du composant afin de ne pas garder
    # de fichiers obsolètes.
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction Stop
    }

    New-Item -Path $Destination -ItemType Directory -Force -ErrorAction Stop | Out-Null

    foreach ($item in Get-ChildItem -LiteralPath $sourceDirectory -Force -ErrorAction Stop) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force -ErrorAction Stop
    }

    $destinationExecutable = Join-Path $Destination $Project.Executable

    if (-not (Test-Path -LiteralPath $destinationExecutable -PathType Leaf)) {
        throw "L'exécutable '$($Project.Executable)' n'a pas été copié dans '$Destination'."
    }

    return [pscustomobject]@{
        Name       = $Project.Name
        Folder     = $Project.Folder
        Directory  = $Destination
        Executable = $destinationExecutable
    }
}

<#
.SYNOPSIS
    Lance un composant et attend qu'il soit prêt.

.DESCRIPTION
    Windows PowerShell 5.1 ne propose pas le paramètre -Environment de
    Start-Process : les variables sont définies temporairement dans
    l'environnement du script, le processus enfant en reçoit une copie,
    puis les valeurs précédentes sont restaurées.

    Le composant est considéré comme démarré lorsqu'il est resté actif
    quelques secondes et, si un port est indiqué, lorsque ce port est en
    écoute avant la fin du délai.
#>
function Start-StyxComponent {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Executable,

        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [int] $TimeoutSeconds,

        [string[]] $Arguments = @(),

        [hashtable] $Environment = @{},

        [int] $Port = 0
    )

    Write-StyxStep "Démarrage de $Name"
    Write-StyxInfo "Commande   : $Executable $($Arguments -join ' ')"
    Write-StyxInfo "Répertoire : $WorkingDirectory"

    $startParameters = @{
        FilePath         = $Executable
        WorkingDirectory = $WorkingDirectory
        PassThru         = $true
        ErrorAction      = 'Stop'
    }

    if ($Arguments.Count -gt 0) {
        $startParameters.ArgumentList = $Arguments
    }

    $previousEnvironment = @{}

    try {
        foreach ($environmentName in @($Environment.Keys)) {
            $previousEnvironment[$environmentName] = [Environment]::GetEnvironmentVariable(
                [string] $environmentName,
                [EnvironmentVariableTarget]::Process
            )

            [Environment]::SetEnvironmentVariable(
                [string] $environmentName,
                [string] $Environment[$environmentName],
                [EnvironmentVariableTarget]::Process
            )
        }

        $process = Start-Process @startParameters
    }
    finally {
        foreach ($environmentName in @($Environment.Keys)) {
            [Environment]::SetEnvironmentVariable(
                [string] $environmentName,
                $previousEnvironment[$environmentName],
                [EnvironmentVariableTarget]::Process
            )
        }
    }

    # Le processus est connu de l'appelant dès son lancement, pour pouvoir
    # l'arrêter si l'attente échoue.
    $script:LastStartedProcess = $process

    $startedAt = [DateTime]::UtcNow
    $minimumAliveUntil = $startedAt.AddSeconds([Math]::Min($script:MinimumAliveSeconds, $TimeoutSeconds))
    $deadline = $startedAt.AddSeconds($TimeoutSeconds)

    while ($true) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()

        if ($process.HasExited) {
            throw "$Name s'est arrêté pendant son démarrage. Code de sortie : $($process.ExitCode)."
        }

        $now = [DateTime]::UtcNow
        $aliveLongEnough = $now -ge $minimumAliveUntil
        $portReady = ($Port -eq 0) -or (Test-StyxPortListening -Port $Port)

        if ($aliveLongEnough -and $portReady) {
            break
        }

        if ($now -ge $deadline) {
            throw "$Name n'écoute pas sur le port $Port après $TimeoutSeconds s."
        }
    }

    if ($Port -gt 0) {
        Write-StyxInfo "$Name est actif et écoute sur le port $Port." -ForegroundColor Green
    }
    else {
        Write-StyxInfo "$Name est actif." -ForegroundColor Green
    }

    return $process
}

function New-StyxSessionEntry {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $Process
    )

    $processName = $null

    try {
        $processName = $Process.ProcessName
    }
    catch {
        $processName = $null
    }

    return [ordered]@{
        Name      = $processName
        ProcessId = $Process.Id
        StartTime = Get-StyxProcessStartTime -Process $Process
    }
}

<#
.SYNOPSIS
    Construit, prépare et démarre la session locale STYX.

.DESCRIPTION
    - vérifie qu'aucune session n'est active, qu'aucun Runner ne travaille
      depuis .run et que les ports sont libres ;
    - lance le script de build Release ;
    - copie les sorties dans <RepositoryRoot>\.run (dossier unique) ;
    - démarre TaskFlow, Agent, Scheduler puis API ;
    - met à jour .run\session.json après chaque composant démarré ;
    - en cas d'échec, arrête les composants déjà démarrés et supprime la session.

    Avec -Development, les composants tournent en environnement Development :
    .NET y active la validation de l'injection de dépendances (portées et
    construction des services) et charge les user-secrets. Le Runner, lancé
    par l'Agent, hérite de cet environnement.
#>
function Start-StyxSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $BuildScript,

        [Parameter(Mandatory = $true)]
        [string] $ScriptDirectory,

        [string] $RepositoryRoot,

        [string] $Environment = 'dev',

        [string] $ConfigFile,

        [switch] $Development,

        [ValidateRange(1, 300)]
        [int] $StartupTimeoutSeconds = 30
    )

    if (-not (Test-Path -LiteralPath $BuildScript -PathType Leaf)) {
        throw "Script de build introuvable : $BuildScript"
    }

    $config = Get-StyxConfiguration `
        -ScriptDirectory $ScriptDirectory `
        -RepositoryRoot $RepositoryRoot `
        -Environment $Environment `
        -ConfigFile $ConfigFile

    $settings = $config.Environment
    $runRoot = Get-StyxRunRoot -RepositoryRoot $config.RepositoryRoot
    $sessionFile = Get-StyxSessionFile -RepositoryRoot $config.RepositoryRoot

    Assert-NoActiveStyxSession -SessionFile $sessionFile

    $projectsByFolder = @{}

    foreach ($project in $config.Projects) {
        $projectsByFolder[$project.Folder.Replace('/', '\').Trim('\').ToLowerInvariant()] = $project
    }

    foreach ($folder in $script:ComponentFolders) {
        if (-not $projectsByFolder.ContainsKey($folder)) {
            throw "Projet absent de la configuration STYX : $folder"
        }
    }

    # Le Runner n'est jamais arrêté par les scripts : s'il travaille encore,
    # on attend sa fin plutôt que de remplacer ses binaires.
    $runnerDirectory = Join-Path $runRoot $projectsByFolder['hpclite\runner'].Folder
    $runningRunners = @(Get-StyxRunningRunners -RunnerDirectory $runnerDirectory)

    if ($runningRunners.Count -gt 0) {
        throw (
            "Un Runner est en cours d'exécution depuis $runnerDirectory (PID " +
            (($runningRunners | ForEach-Object { $_.Id }) -join ', ') + '). ' +
            "Attendre la fin de son job avant de relancer."
        )
    }

    $applicationEnvironmentName = $null

    if ($Development) {
        $applicationEnvironmentName = $settings.DevelopmentEnvironment
    }

    Write-StyxStep 'STYX - Préparation de la session'
    Write-StyxInfo "Dépôt         : $($config.RepositoryRoot)"
    Write-StyxInfo "Solution      : $($config.SolutionPath)"
    Write-StyxInfo "Destination   : $runRoot"
    Write-StyxInfo "Configuration : $($settings.Name) ($($settings.ConfigFile))"

    if ($Development) {
        Write-StyxInfo "Applications  : environnement $applicationEnvironmentName (validation DI, user-secrets)"
    }

    # --------------------------------------------------------
    # Secrets et ports : vérifiés avant le build, qui est long.
    # En Development, les secrets peuvent venir des user-secrets.
    # --------------------------------------------------------

    if (-not $Development) {
        $missingSecrets = @(
            foreach ($name in $settings.SecretVariables) {
                if ($null -eq (Get-StyxSecretValue -Name $name)) {
                    $name
                }
            }
        )

        if ($missingSecrets.Count -gt 0) {
            throw (
                "Variables d'environnement obligatoires non définies : " + ($missingSecrets -join ', ') + '. ' +
                "Les définir sur le poste (utilisateur ou session), ou lancer avec -Development pour utiliser les user-secrets."
            )
        }
    }

    $ports = @{
        ApiPort      = ConvertTo-StyxTcpPort -Name 'ApiPort' -Value $settings.Network.ApiPort
        AgentPort    = ConvertTo-StyxTcpPort -Name 'AgentPort' -Value $settings.Network.AgentPort
        TaskFlowPort = ConvertTo-StyxTcpPort -Name 'TaskFlowPort' -Value $settings.Network.TaskFlowPort
    }

    Assert-StyxDistinctPorts -Ports $ports
    Assert-StyxPortsFree -Ports $ports

    $bindHost = [string] $settings.Network.BindHost
    $urls = @{
        Api   = "http://${bindHost}:$($ports.ApiPort)"
        Agent = "http://${bindHost}:$($ports.AgentPort)"
    }
    $urlPorts = @{
        Api   = $ports.ApiPort
        Agent = $ports.AgentPort
    }

    # --------------------------------------------------------
    # Build
    # --------------------------------------------------------

    Write-StyxStep 'Compilation de la solution STYX en Release'

    $global:LASTEXITCODE = 0
    & $BuildScript -RepositoryRoot $config.RepositoryRoot -Environment $Environment
    $buildInvocationSucceeded = $?
    $buildExitCode = $LASTEXITCODE

    if (-not $buildInvocationSucceeded -or ($null -ne $buildExitCode -and $buildExitCode -ne 0)) {
        throw "La compilation Release a échoué (code $buildExitCode)."
    }

    # --------------------------------------------------------
    # Copie des sorties dans le dossier unique
    # --------------------------------------------------------

    Write-StyxStep 'Copie des sorties Release'

    New-Item -Path $runRoot -ItemType Directory -Force -ErrorAction Stop | Out-Null

    $outputs = @{}

    foreach ($folder in $script:ComponentFolders) {
        $project = $projectsByFolder[$folder]

        $outputs[$folder] = Copy-StyxProjectOutput `
            -Project $project `
            -Configuration 'Release' `
            -Destination (Join-Path $runRoot $project.Folder)
    }

    $runner = $outputs['hpclite\runner']
    Write-StyxInfo "HPC Lite Runner : $($runner.Executable) ; lancé par l'Agent, jamais par les scripts."

    # --------------------------------------------------------
    # Démarrage, avec mise à jour de la session après chaque composant
    # --------------------------------------------------------

    $session = [ordered]@{
        Version         = 4
        SessionId       = [guid]::NewGuid().ToString()
        RepositoryRoot  = $config.RepositoryRoot
        RunRoot         = $runRoot
        Environment     = $settings.Name
        ConfigFile      = $settings.ConfigFile
        Development     = [bool] $Development
        StartedAt       = [DateTime]::UtcNow.ToString('o')
        Endpoints       = [ordered]@{ Api = $urls.Api; Agent = $urls.Agent }
        Runner          = [ordered]@{
            Executable = $runner.Executable
            Started    = $false
        }
        Components      = @()
    }

    $startedProcesses = @()

    try {
        foreach ($component in $script:StartupOrder) {
            $output = $outputs[$component.Folder]
            $componentEnvironment = @{}

            foreach ($name in $settings.ApplicationEnvironment.Keys) {
                $componentEnvironment[$name] = $settings.ApplicationEnvironment[$name]
            }

            if ($Development) {
                $componentEnvironment['DOTNET_ENVIRONMENT'] = $applicationEnvironmentName
                $componentEnvironment['ASPNETCORE_ENVIRONMENT'] = $applicationEnvironmentName
            }

            $arguments = @()
            $port = 0
            $url = $null

            if ($null -ne $component.Url) {
                $url = $urls[$component.Url]
                $port = $urlPorts[$component.Url]
                $arguments = @('--urls', $url)
                $componentEnvironment['ASPNETCORE_URLS'] = $url

                # Pour les applications dont Kestrel est configuré avec une
                # section Endpoints explicite.
                $componentEnvironment['Kestrel__Endpoints__Http__Url'] = $url
            }

            $script:LastStartedProcess = $null

            try {
                $process = Start-StyxComponent `
                    -Name $component.Name `
                    -Executable $output.Executable `
                    -WorkingDirectory $output.Directory `
                    -TimeoutSeconds $StartupTimeoutSeconds `
                    -Arguments $arguments `
                    -Environment $componentEnvironment `
                    -Port $port
            }
            catch {
                if ($null -ne $script:LastStartedProcess) {
                    $startedProcesses += $script:LastStartedProcess
                }

                throw
            }

            $startedProcesses += $process

            $entry = New-StyxSessionEntry -Process $process
            $entry.Name = $component.Name
            $entry.Executable = $output.Executable
            $entry.Directory = $output.Directory

            if ($null -ne $url) {
                $entry.Url = $url
            }

            $session.Components = @($session.Components) + @($entry)
            Save-StyxSession -Session $session -SessionFile $sessionFile
        }
    }
    catch {
        $startupError = $_

        for ($index = $startedProcesses.Count - 1; $index -ge 0; $index--) {
            try {
                [void] (
                    Stop-StyxComponentProcesses `
                        -HostProcess $startedProcesses[$index] `
                        -TimeoutSeconds 5 `
                        -Force
                )
            }
            catch {
                # Le nettoyage ne doit pas masquer l'erreur initiale.
            }
        }

        if (Test-Path -LiteralPath $sessionFile -PathType Leaf) {
            Remove-Item -LiteralPath $sessionFile -Force -ErrorAction SilentlyContinue
        }

        throw $startupError
    }

    Write-StyxSuccess 'Session STYX démarrée avec succès'
    Write-StyxInfo "Session : $sessionFile"
    Write-StyxInfo "API     : $($urls.Api)"
    Write-StyxInfo "Agent   : $($urls.Agent)"
    Write-StyxInfo "Runner  : $($runner.Executable) (valeur attendue dans exe_path en base)"

    foreach ($component in $session.Components) {
        Write-StyxInfo ('{0,-20}: PID {1}' -f $component.Name, $component.ProcessId)
    }
}

Export-ModuleMember -Function `
    Start-StyxSession, `
    Stop-StyxSession, `
    Get-StyxRunRoot, `
    Get-StyxSessionFile, `
    Get-StyxSessionProcess, `
    Test-StyxPortListening
```
