#Requires -Version 5.1
<#
.SYNOPSIS
    Compila il plugin (se richiesto) e copia la DLL nella cartella plugin di NINA.

.PARAMETER Configuration
    Configurazione di build: Debug o Release (default: Release).

.PARAMETER NinaVersion
    Sottocartella dei plugin NINA. Default: 3.0.0
    NINA 3.x usa sempre la cartella "3.0.0" indipendentemente dalla versione
    dell'applicazione (e' la versione dell'API plugin, non di NINA).

.PARAMETER SkipBuild
    Se specificato, salta il passo di build e usa il binario esistente.

.EXAMPLE
    # Build + install in Release
    powershell -ExecutionPolicy Bypass -File scripts\install-plugin.ps1

.EXAMPLE
    # Solo copia (build gia' eseguita)
    powershell -ExecutionPolicy Bypass -File scripts\install-plugin.ps1 -SkipBuild
#>
param(
    [string] $Configuration = "Release",
    [string] $NinaVersion   = "3.0.0",
    [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir
$projDir   = Join-Path $repoRoot "src\AdaptiveAgentForPHD2.NinaPlugin"
$dllName   = "AdaptiveAgentForPHD2.NinaPlugin.dll"
$srcDll    = Join-Path $projDir "bin\x64\$Configuration\$dllName"
$pluginSubfolder = "AdaptiveAgentForPHD2.NinaPlugin"
$targetDir = Join-Path $env:LOCALAPPDATA "NINA\Plugins\$NinaVersion\$pluginSubfolder"

if (-not $SkipBuild) {
    Write-Host "Building $Configuration..."
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" +
                [System.Environment]::GetEnvironmentVariable("Path","User")
    Push-Location $projDir
    try {
        dotnet build -c $Configuration -p:Platform=x64
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Build fallita (exit code $LASTEXITCODE)."
        }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path $srcDll)) {
    Write-Error "DLL non trovata: $srcDll`nEsegui prima: dotnet build -c $Configuration"
}

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item $srcDll -Destination $targetDir -Force

Write-Host ""
Write-Host "Plugin installato in: $targetDir\$dllName"
Write-Host "Chiudi e riavvia NINA per caricarlo."
