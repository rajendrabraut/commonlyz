<#
.SYNOPSIS
    End-to-end smoke test of a running Commonlyz instance (IIS or Kestrel).

.DESCRIPTION
    Checks public pages and health, confirms admin pages are protected, signs in through the real login form
    (antiforgery token included), opens every module's admin page as the signed-in admin, and opens a real
    Blazor WebSocket (the transport Blazor Server needs; under IIS it requires the WebSocket Protocol feature).
    Uses HttpClient directly so it behaves the same on Windows PowerShell 5.1 and PowerShell 7.
    Exits 1 if any check fails.

.EXAMPLE
    .\eng\iis\smoke-test.ps1 -BaseUrl http://localhost:8090 -AdminPassword 'Change-me-1!'
#>
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    [Parameter(Mandatory)] [string] $AdminPassword,
    [string] $AdminEmail = 'admin@commonlyz.local'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$BaseUrl = $BaseUrl.TrimEnd('/')
$failures = [System.Collections.Generic.List[string]]::new()

function New-Client([System.Net.CookieContainer] $Cookies) {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = $Cookies
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    $client.BaseAddress = [uri] $BaseUrl
    return $client
}

# Returns "<status>" or "<status> -> <redirect target>" so redirects are checked exactly.
function Get-Result([System.Net.Http.HttpClient] $Client, [string] $Path) {
    try {
        $response = $Client.GetAsync($Path).GetAwaiter().GetResult()
        $status = [int] $response.StatusCode
        if ($response.Headers.Location) { return "$status -> $($response.Headers.Location.OriginalString -replace '\?.*$', '')" }
        return "$status"
    } catch { return "error: $($_.Exception.GetBaseException().Message)" }
}

function Check([string] $Name, $Actual, $Expected) {
    $pass = "$Actual" -eq "$Expected"
    Write-Host ("{0,-4} {1,-38} {2}" -f $(if ($pass) { 'ok' } else { 'FAIL' }), $Name, $Actual)
    if (-not $pass) { $failures.Add("$Name -> $Actual (expected $Expected)") }
}

$modules = '/shortlink', '/linkhub', '/pixelsqueeze', '/journal', '/splitshare', '/budget', '/timetrack', '/formflow',
           '/slotbook', '/stockroom', '/clientcollect', '/taskboard', '/wiki', '/chat', '/statusboard'

Write-Host '== Anonymous'
$anonymous = New-Client ([System.Net.CookieContainer]::new())
foreach ($path in '/login', '/health', '/status', '/status.json', '/_framework/blazor.web.js') {
    Check "GET $path" (Get-Result $anonymous $path) 200
}
foreach ($path in @('/') + $modules + '/email') {
    Check "GET $path (protected)" (Get-Result $anonymous $path) "302 -> $BaseUrl/login"
}

Write-Host '== Sign in'
$cookies = [System.Net.CookieContainer]::new()
$admin = New-Client $cookies
$form = $admin.GetStringAsync('/login').GetAwaiter().GetResult()
$token = [regex]::Match($form, 'name="__RequestVerificationToken" value="([^"]+)"').Groups[1].Value
Check 'antiforgery token present' ([bool] $token) $true
$fields = [System.Collections.Generic.Dictionary[string, string]]::new()
$fields['_handler'] = 'login'
$fields['__RequestVerificationToken'] = $token
$fields['Input.Email'] = $AdminEmail
$fields['Input.Password'] = $AdminPassword
$fields['Input.RememberMe'] = 'true'
$login = $admin.PostAsync('/login', [System.Net.Http.FormUrlEncodedContent]::new($fields)).GetAwaiter().GetResult()
$loginBody = $login.Content.ReadAsStringAsync().GetAwaiter().GetResult()
Check 'POST /login' ([int] $login.StatusCode) 302
if ([int] $login.StatusCode -ne 302) {
    $text = ($loginBody -replace '<[^>]+>', ' ' -replace '\s+', ' ').Trim()
    Write-Host "  page text: $($text.Substring(0, [Math]::Min(300, $text.Length)))"
}

Write-Host '== Signed-in pages'
foreach ($path in @('/') + $modules + '/email') {
    Check "GET $path" (Get-Result $admin $path) 200
}

Write-Host '== Blazor circuit'
$negotiate = $admin.PostAsync('/_blazor/negotiate?negotiateVersion=1', [System.Net.Http.StringContent]::new('')).GetAwaiter().GetResult()
$negotiation = $negotiate.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
Check 'negotiate offers WebSockets' (@($negotiation.availableTransports | ForEach-Object transport) -contains 'WebSockets') $true

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
try {
    $ws.Options.Cookies = $cookies
    $wsUrl = ($BaseUrl -replace '^http', 'ws') + "/_blazor?id=$([uri]::EscapeDataString($negotiation.connectionToken))"
    [void] $ws.ConnectAsync([uri] $wsUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $wsState = "$($ws.State)"
} catch { $wsState = "error: $($_.Exception.GetBaseException().Message)" } finally { $ws.Dispose() }
Check 'WebSocket upgrade' $wsState 'Open'

if ($failures.Count) {
    Write-Host "`n$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host "`nAll checks passed" -ForegroundColor Green
