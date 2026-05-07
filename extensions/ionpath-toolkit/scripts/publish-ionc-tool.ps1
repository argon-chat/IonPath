#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes ionc as a .NET global tool to NuGet.
.PARAMETER ApiKey
    NuGet API key. Can also be set via NUGET_API_KEY environment variable.
.PARAMETER Source
    NuGet source URL. Defaults to nuget.org.
.PARAMETER DryRun
    Build package without pushing.
#>
param(
    [string]$ApiKey = $env:NUGET_API_KEY,
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../../..")
$ioncDir = Join-Path $repoRoot "src/ionc"
$outputDir = Join-Path $repoRoot "artifacts/tool"

Write-Host "Building ionc global tool package..." -ForegroundColor Cyan

if (Test-Path $outputDir) { Remove-Item -Recurse -Force $outputDir }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

dotnet pack $ioncDir -c Release -o $outputDir
if ($LASTEXITCODE -ne 0) { throw "Failed to pack ionc" }

$pkg = Get-ChildItem "$outputDir/*.nupkg" | Where-Object { $_.Name -notlike "*.symbols.*" } | Select-Object -First 1
Write-Host "Package: $($pkg.Name)" -ForegroundColor Green

if ($DryRun) {
    Write-Host "Dry run - not pushing." -ForegroundColor Yellow
    return
}

if (-not $ApiKey) {
    throw "No API key provided. Set NUGET_API_KEY env var or pass -ApiKey parameter."
}

Write-Host "Pushing to $Source..." -ForegroundColor Cyan
dotnet nuget push $pkg.FullName --api-key $ApiKey --source $Source --skip-duplicate
if ($LASTEXITCODE -ne 0) { throw "Failed to push ionc tool" }

Write-Host "Published ionc tool successfully!" -ForegroundColor Green
Write-Host "Users can install with: dotnet tool install -g ionc" -ForegroundColor Gray
