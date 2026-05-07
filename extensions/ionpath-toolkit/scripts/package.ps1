#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Packages the IonPath extension into a .vsix file for local installation or CI.
.PARAMETER Output
    Output directory for the .vsix file. Defaults to current directory.
#>
param(
    [string]$Output = "."
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$extensionDir = Split-Path -Parent $scriptDir

Push-Location $extensionDir
try {
    if (-not (Get-Command "vsce" -ErrorAction SilentlyContinue)) {
        Write-Host "Installing @vscode/vsce globally..." -ForegroundColor Yellow
        npm install -g @vscode/vsce
    }

    # Clean
    if (Test-Path "out") { Remove-Item -Recurse -Force "out" }
    if (Test-Path "*.vsix") { Remove-Item -Force "*.vsix" }

    # Install & compile
    npm ci
    npm run compile
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed" }

    # Package
    vsce package --out $Output
    $vsix = Get-ChildItem "$Output/*.vsix" | Select-Object -First 1
    Write-Host "Packaged: $($vsix.FullName)" -ForegroundColor Green
} finally {
    Pop-Location
}
