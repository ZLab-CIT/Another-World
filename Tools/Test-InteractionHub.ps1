param([string]$BaseUrl = 'http://127.0.0.1:5074')

$ErrorActionPreference = 'Stop'
$health = Invoke-RestMethod "$BaseUrl/health"
if ($health.status -ne 'healthy') { throw 'Health endpoint failed.' }
$visitor = Invoke-RestMethod "$BaseUrl/api/visitors/register" -Method Post `
    -ContentType 'application/json' -Body '{"alias":"Test Visitor","publicAliasConsent":true}'
$state = Invoke-RestMethod "$BaseUrl/api/state" -Headers @{ 'X-Visitor-Token' = $visitor.token }
if (-not $state.visitor.visitorId) { throw 'Visitor registration did not persist.' }
$qr = Invoke-WebRequest "$BaseUrl/api/qr?url=$([uri]::EscapeDataString($BaseUrl))" -UseBasicParsing
if ($qr.Headers.'Content-Type' -notlike 'image/png*') { throw 'QR endpoint did not return PNG.' }
Write-Host "InteractionHub smoke test passed for visitor $($state.visitor.displayName)." -ForegroundColor Green
