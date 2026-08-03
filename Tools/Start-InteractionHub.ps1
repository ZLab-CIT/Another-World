param(
    [int]$Port = 5074,
    [switch]$Background
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'InteractionHub\InteractionHub.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw "InteractionHub project not found at $project"
}

$route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
    Sort-Object RouteMetric, InterfaceMetric |
    Select-Object -First 1
$address = $null
if ($route) {
    $address = Get-NetIPAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '169.254.*' } |
        Select-Object -ExpandProperty IPAddress -First 1
}
if (-not $address) {
    $address = [Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName()) |
        Where-Object { $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and -not [Net.IPAddress]::IsLoopback($_) } |
        Select-Object -ExpandProperty IPAddressToString -First 1
}
if (-not $address) {
    $address = '127.0.0.1'
}

$listenUrl = "http://0.0.0.0:$Port"
$publicUrl = "http://${address}:$Port"
$env:ASPNETCORE_URLS = $listenUrl
$env:INTERACTION_HUB_URL = "http://127.0.0.1:$Port"
$env:INTERACTION_HUB_PUBLIC_URL = $publicUrl

Write-Host "InteractionHub phone URL: $publicUrl" -ForegroundColor Cyan
Write-Host 'Unity discovers this address automatically. Phones must use the same LAN.'
Write-Host 'If Windows asks, allow private-network access only.'

if ($Background) {
    $serviceDirectory = Split-Path -Parent $project
    $process = Start-Process dotnet -ArgumentList @('run', '--no-launch-profile', '--no-build') `
        -WorkingDirectory $serviceDirectory -WindowStyle Hidden -PassThru
    Write-Host "InteractionHub started in background (PID $($process.Id))."
    return
}

Push-Location (Split-Path -Parent $project)
try {
    & dotnet run --no-launch-profile
}
finally {
    Pop-Location
}
