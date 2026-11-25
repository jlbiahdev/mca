#requires -Version 5.1

<#
.SYNOPSIS
    Pousse un package .nupkg Styx vers Artifactory, en intégration (machine du
    runner), sans git.

.DESCRIPTION
    Le fichier à pousser est OBLIGATOIRE : aucun « dernier package trouvé »,
    pour ne jamais pousser un ancien package par erreur.

    Une version déjà présente sur Artifactory est une ERREUR (pas de
    --skip-duplicate) : deux builds ne doivent pas produire la même version.

    L'adresse et le dépôt Artifactory sont lus dans
    devops\config\<Environment>.psd1 (section Artifactory).

    La clé d'API est lue dans la variable d'environnement nommée par
    Artifactory.ApiKeyVariable. Le runner la lit dans une variable GitLab et
    la transmet au script par l'environnement du processus : elle n'est
    jamais écrite dans un fichier ni affichée.

.PARAMETER PackageFile
    Chemin du .nupkg, tel que produit par package.ps1.

.PARAMETER RepositoryRoot
    Racine des sources, obligatoire : git n'est pas disponible en intégration.

.PARAMETER Environment
    Nom de l'environnement (devops\config\<Environment>.psd1). Par défaut : int.

.EXAMPLE
    $env:ARTIFACTORY_API_KEY = "..."
    .\push-artifactory.ps1 -PackageFile D:\ci\packages\1.0.913231\Styx.Publish.1.0.913231.nupkg -RepositoryRoot D:\ci\build

.NOTES
    Code de sortie : 0 succès, 1 erreur.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageFile,

    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot,

    [string] $Environment = 'int'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "..\modules\StyxBuild.psm1") -Force
Initialize-StyxOutput

try {
    Write-StyxStep "STYX - Push NuGet vers Artifactory"

    Assert-StyxCommand -Name "dotnet"
    $config = Get-StyxConfiguration `
        -ScriptDirectory $PSScriptRoot `
        -RepositoryRoot $RepositoryRoot `
        -Environment $Environment

    if (-not (Test-Path -LiteralPath $PackageFile -PathType Leaf)) {
        throw "Package introuvable : $PackageFile"
    }

    $package = Get-Item -LiteralPath $PackageFile

    if ($package.Extension -ne ".nupkg") {
        throw "Le fichier n'est pas un package NuGet (.nupkg) : $($package.FullName)"
    }

    # Clé d'API : session, puis utilisateur, puis machine.
    $variable = $config.ArtifactoryApiKeyVariable
    $apiKey = Get-StyxSecretValue -Name $variable

    if ($null -eq $apiKey) {
        throw "Variable d'environnement $variable non définie. Exemple : `$env:$variable = `"<clé>`""
    }

    Write-StyxInfo "Package : $($package.FullName)"
    Write-StyxInfo "Repo    : $($config.ArtifactoryRepo)"
    Write-StyxInfo "Source  : $($config.ArtifactorySource)"
    Write-Host ""

    $pushArguments = @(
        'nuget', 'push', $package.FullName,
        '--source', $config.ArtifactorySource,
        '--api-key', $apiKey
    )

    # La clé est remplacée par *** dans l'affichage de la commande.
    $displayArguments = @(
        'nuget', 'push', $package.FullName,
        '--source', $config.ArtifactorySource,
        '--api-key', '***'
    )

    try {
        Invoke-StyxNative `
            -FilePath 'dotnet' `
            -Arguments $pushArguments `
            -DisplayArguments $displayArguments `
            -Description 'Le push vers Artifactory'
    }
    catch {
        # Piste adaptée au code HTTP renvoyé par Artifactory, s'il est connu.
        $message = $_.Exception.Message

        if ($message -match '\b401\b') {
            $hint = "401 : compte ou jeton refusés. Vérifier que la clé a la forme <compte>:<jeton>, avec le compte de build (usr-…) et son jeton."
        }
        elseif ($message -match '\b403\b') {
            $hint = "403 : le compte n'a pas le droit de déposer dans ce dépôt. Vérifier qu'il s'agit du compte de build (usr-…), et non du compte de déploiement (usr-cd-…)."
        }
        elseif ($message -match '\b409\b') {
            $hint = "409 : cette version existe déjà sur Artifactory. Chaque version correspond à un pipeline GitLab : un nouveau commit sur main produira une nouvelle version."
        }
        else {
            $hint = "Voir la sortie de dotnet nuget push ci-dessus."
        }

        throw ($message + [Environment]::NewLine + $hint)
    }

    Write-StyxSuccess "Package poussé avec succès : $($package.Name)"
    exit 0
}
catch {
    Write-StyxFailure $_.Exception.Message
    exit 1
}
