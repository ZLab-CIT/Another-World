param(
    [ValidateSet('Windows', 'WebGL')]
    [string]$Target = 'Windows',
    [string]$UnityPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $projectRoot 'Temp\UnityLockfile'

if (Test-Path -LiteralPath $lockPath) {
    try {
        $lock = [IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None')
        $lock.Dispose()
    }
    catch {
        throw 'This project is open in Unity. Close the Editor, or build from Another World > Build inside the Editor.'
    }
}

if (-not $UnityPath) {
    $versionLine = Get-Content -LiteralPath (Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt') |
        Select-Object -First 1
    $version = ($versionLine -replace '^m_EditorVersion:\s*', '').Trim()
    $UnityPath = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
}

if (-not (Test-Path -LiteralPath $UnityPath)) {
    throw "Unity editor not found at $UnityPath"
}

$method = if ($Target -eq 'WebGL') { 'ProjectBuild.BuildWebGL' } else { 'ProjectBuild.BuildWindows' }
$logPath = Join-Path $projectRoot "Logs\Build-$Target.log"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logPath) | Out-Null

Write-Host "Building $Target with $UnityPath" -ForegroundColor Cyan
& $UnityPath -batchmode -quit -projectPath $projectRoot -executeMethod $method -logFile $logPath
if ($LASTEXITCODE -ne 0) {
    throw "Unity build failed with exit code $LASTEXITCODE. See $logPath"
}

Write-Host "Build completed. See Builds\$Target" -ForegroundColor Green
