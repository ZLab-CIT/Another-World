param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'InteractionHub\InteractionHub.csproj'
$output = Join-Path $projectRoot 'Builds\InteractionHub'
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

& dotnet publish $project -c Release -r win-x64 --self-contained $selfContained -o $output
if ($LASTEXITCODE -ne 0) {
    throw "InteractionHub publish failed with exit code $LASTEXITCODE."
}

Write-Host "InteractionHub published to $output" -ForegroundColor Green
