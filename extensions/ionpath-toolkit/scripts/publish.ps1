#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes the IonPath VS Code extension to the marketplace.
.PARAMETER Token
    Personal Access Token for VS Code Marketplace (publisher: ArgonChat).
    Can also be set via VSCE_PAT environment variable.
.PARAMETER PreRelease
    Publish as a pre-release version.
.PARAMETER DryRun
    Package the extension (.vsix) without publishing.
#>
param(
    [string]$Token = $env:VSCE_PAT,
    [switch]$PreRelease,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$extensionDir = Split-Path -Parent $scriptDir

Push-Location $extensionDir
try {
    # Ensure vsce is available
    if (-not (Get-Command "vsce" -ErrorAction SilentlyContinue)) {
        Write-Host "Installing @vscode/vsce globally..." -ForegroundColor Yellow
        npm install -g @vscode/vsce
    }

    # Clean previous builds
    if (Test-Path "out") { Remove-Item -Recurse -Force "out" }
    if (Test-Path "*.vsix") { Remove-Item -Force "*.vsix" }

    # Install dependencies
    Write-Host "Installing dependencies..." -ForegroundColor Cyan
    npm ci

    # Compile
    Write-Host "Compiling extension..." -ForegroundColor Cyan
    npm run compile
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed" }

    # Read version from package.json
    $pkg = Get-Content "package.json" | ConvertFrom-Json
    $version = $pkg.version
    Write-Host "Extension version: $version" -ForegroundColor Green

    if ($DryRun) {
        Write-Host "Packaging .vsix (dry run)..." -ForegroundColor Yellow
        if ($PreRelease) {
            vsce package --pre-release
        } else {
            vsce package
        }
        $vsix = Get-ChildItem "*.vsix" | Select-Object -First 1
        Write-Host "Created: $($vsix.Name)" -ForegroundColor Green
    } else {
        if (-not $Token) {
            throw "No token provided. Set VSCE_PAT env var or pass -Token parameter."
        }

        Write-Host "Publishing to marketplace..." -ForegroundColor Cyan
        if ($PreRelease) {
            vsce publish --pat $Token --pre-release
        } else {
            vsce publish --pat $Token
        }
        Write-Host "Published v$version successfully!" -ForegroundColor Green
    }
} finally {
    Pop-Location
}
