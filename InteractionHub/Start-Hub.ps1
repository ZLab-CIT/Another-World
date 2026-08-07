param(
    [int]$Port = 5074
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'InteractionHub.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "InteractionHub.exe was not found beside this script."
}

$address = [Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName()) |
    Where-Object {
        $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and
        -not [Net.IPAddress]::IsLoopback($_) -and
        $_.IPAddressToString -notlike '169.254.*'
    } |
    Select-Object -ExpandProperty IPAddressToString -First 1
if (-not $address) {
    $address = '127.0.0.1'
}

$env:ASPNETCORE_URLS = "http://0.0.0.0:$Port"
$env:INTERACTION_HUB_URL = "http://127.0.0.1:$Port"
$env:INTERACTION_HUB_PUBLIC_URL = "http://${address}:$Port"

Write-Host "InteractionHub phone URL: $env:INTERACTION_HUB_PUBLIC_URL" -ForegroundColor Cyan
Write-Host 'Keep this window open while the Unity player is running.'
& $exe
