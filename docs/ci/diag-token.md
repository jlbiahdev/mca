# diag-token.ps1

Compare les jetons GitLab chiffrés du runner (`gitlab-token.dat` et chaque `*.xml` de `D:\ci\runner`) sans jamais les afficher : longueur, empreinte, et description renvoyée par GitLab (nom, scopes, actif). L'adresse de GitLab est lue dans `ci-dev.psd1`.

À copier dans `D:\ci\runner` en UTF-8 avec BOM, puis à lancer dans une console administrateur, avec le compte qui a chiffré les fichiers XML :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\ci\runner\diag-token.ps1
```

```powershell
#requires -Version 5.1

<#
.SYNOPSIS
    Compare les jetons GitLab chiffrés du runner, sans jamais les afficher.

.DESCRIPTION
    Pour chaque fichier de jeton du dossier du runner :
    - gitlab-token.dat (chiffré par protect-token.ps1, niveau machine) ;
    - chaque *.xml (chiffré par Export-Clixml, niveau du compte) ;
    le script affiche la longueur du jeton, une empreinte (4 premiers octets
    du SHA-256, pour voir si deux fichiers contiennent le même jeton) et la
    description renvoyée par GitLab (nom, scopes, actif).

    L'adresse de GitLab est lue dans ci-dev.psd1.

    À lancer avec le compte qui a chiffré les fichiers XML.

.PARAMETER RunnerRoot
    Dossier du runner. Par défaut : le dossier de ce script.

.EXAMPLE
    .\diag-token.ps1
#>
[CmdletBinding()]
param(
    [string] $RunnerRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RunnerRoot)) {
    $RunnerRoot = $PSScriptRoot
}

$config = Import-PowerShellDataFile -LiteralPath (Join-Path $RunnerRoot 'ci-dev.psd1')
$url    = ([string] $config.GitLabUrl).TrimEnd('/')

Add-Type -AssemblyName System.Security
[Net.ServicePointManager]::SecurityProtocol = `
    [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Get-Fingerprint {
    param([string] $Text)

    $sha  = [Security.Cryptography.SHA256]::Create()
    $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))
    return -join ($hash[0..3] | ForEach-Object { $_.ToString('x2') })
}

function Get-TokenDescription {
    param([string] $Token)

    try {
        $info = Invoke-RestMethod -Uri "$url/api/v4/personal_access_tokens/self" -Headers @{ 'PRIVATE-TOKEN' = $Token }
        return '{0} / {1} / actif : {2}' -f $info.name, ($info.scopes -join ','), $info.active
    }
    catch {
        return "erreur GitLab : $($_.Exception.Message)"
    }
}

function Read-DatToken {
    param([string] $Path)

    $encrypted = [Convert]::FromBase64String((Get-Content -LiteralPath $Path -Raw -Encoding ASCII).Trim())
    $plain = [Security.Cryptography.ProtectedData]::Unprotect(
        $encrypted,
        [Text.Encoding]::UTF8.GetBytes('Styx.CiDev'),
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Text.Encoding]::UTF8.GetString($plain)
}

function Read-XmlToken {
    param([string] $Path)

    return [Net.NetworkCredential]::new('', (Import-Clixml -LiteralPath $Path)).Password
}

$files = @(Get-ChildItem -LiteralPath $RunnerRoot -File | Where-Object { $_.Extension -in '.dat', '.xml' } | Sort-Object Name)

Write-Host ''
Write-Host "GitLab : $url"
Write-Host ''

foreach ($file in $files) {
    try {
        if ($file.Extension -eq '.dat') {
            $token = Read-DatToken -Path $file.FullName
        }
        else {
            $token = Read-XmlToken -Path $file.FullName
        }

        $line = '{0,-22} longueur {1,3}  empreinte {2}  {3}' -f `
            $file.Name, $token.Length, (Get-Fingerprint -Text $token), (Get-TokenDescription -Token $token)
    }
    catch {
        $line = '{0,-22} illisible : {1}' -f $file.Name, $_.Exception.Message
    }
    finally {
        $token = $null
    }

    Write-Host $line
}

Write-Host ''
```
