#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds and publishes IonPath .NET packages to NuGet.
.PARAMETER ApiKey
    NuGet API key. Can also be set via NUGET_API_KEY environment variable.
.PARAMETER Source
    NuGet source URL. Defaults to nuget.org.
.PARAMETER DryRun
    Build packages without pushing.
.PARAMETER Configuration
    Build configuration. Defaults to Release.
#>
param(
    [string]$ApiKey = $env:NUGET_API_KEY,
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [switch]$DryRun,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../../..")
$srcDir = Join-Path $repoRoot "src"
$outputDir = Join-Path $repoRoot "artifacts/nuget"

# Projects to publish (order matters for dependencies)
$projects = @(
    "ion.runtime"
    "ion.runtime.client"
    "ion.runtime.network"
    "ion.compiler.runtime"
    "ion.compiler"
    "ion.syntax"
)

Write-Host "Building IonPath packages ($Configuration)..." -ForegroundColor Cyan
Write-Host "Output: $outputDir" -ForegroundColor Gray

# Clean output
if (Test-Path $outputDir) { Remove-Item -Recurse -Force $outputDir }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# Build & pack each project
foreach ($project in $projects) {
    $projPath = Join-Path $srcDir $project
    if (-not (Test-Path $projPath)) {
        Write-Host "  SKIP $project (not found)" -ForegroundColor Yellow
        continue
    }

    Write-Host "  Packing $project..." -ForegroundColor White
    dotnet pack $projPath -c $Configuration -o $outputDir --no-restore 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        dotnet pack $projPath -c $Configuration -o $outputDir
        throw "Failed to pack $project"
    }
}

$packages = Get-ChildItem "$outputDir/*.nupkg"
Write-Host "`nPackages created:" -ForegroundColor Green
$packages | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor Gray }

if ($DryRun) {
    Write-Host "`nDry run - packages not pushed." -ForegroundColor Yellow
    return
}

if (-not $ApiKey) {
    throw "No API key provided. Set NUGET_API_KEY env var or pass -ApiKey parameter."
}

Write-Host "`nPushing to $Source..." -ForegroundColor Cyan
foreach ($pkg in $packages) {
    if ($pkg.Name -like "*.symbols.nupkg") { continue }
    Write-Host "  Pushing $($pkg.Name)..." -ForegroundColor White
    dotnet nuget push $pkg.FullName --api-key $ApiKey --source $Source --skip-duplicate
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Failed to push $($pkg.Name)"
    }
}

Write-Host "`nDone!" -ForegroundColor Green
