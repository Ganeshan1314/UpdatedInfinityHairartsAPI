[CmdletBinding()]
param(
    [Uri]$BaseUrl = 'https://infinityhairarts.com/'
)

$ErrorActionPreference = 'Stop'
if ($BaseUrl.Scheme -ne 'https' -and -not ($BaseUrl.Scheme -eq 'http' -and $BaseUrl.IsLoopback)) {
    throw 'Use HTTPS for a hosted API, or HTTP on loopback for local checks.'
}
Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromSeconds(20)
$origin = $BaseUrl.AbsoluteUri.TrimEnd('/')
$checks = 0
function Confirm-Check([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw "FAIL: $Label" }
    $script:checks++
    Write-Output "PASS: $Label"
}
try {
    $response = $http.GetAsync("$origin/api/Login/health").GetAwaiter().GetResult()
    try {
        Confirm-Check ([int]$response.StatusCode -eq 200) 'The API health route responds'
        $health = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        Confirm-Check ($health.build -eq 'updated-api-20261106-log-fixes') 'The hosted API is running the November 6 log fixes'
    } finally { $response.Dispose() }

    $response = $http.GetAsync("$origin/api/Location/salons").GetAwaiter().GetResult()
    try {
        Confirm-Check ([int]$response.StatusCode -eq 200) 'The public salon directory responds successfully'
        $page = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        Confirm-Check ($null -ne $page -and $page.PSObject.Properties.Name -contains 'items' -and $page.PSObject.Properties.Name -contains 'nextCursor') 'The directory uses the mobile application contract'
    } finally { $response.Dispose() }

    # Invalid input verifies route discovery and validation without creating a client report.
    $body = New-Object System.Net.Http.StringContent('{"reports":[]}', [Text.Encoding]::UTF8, 'application/json')
    try { $response = $http.PostAsync("$origin/api/ClientErrors", $body).GetAwaiter().GetResult() }
    finally { $body.Dispose() }
    try { Confirm-Check ([int]$response.StatusCode -eq 400) 'ClientErrors is installed and validates report batches' }
    finally { $response.Dispose() }

    foreach ($name in @('86712ea3-8bde-4d04-8543-45cd46dff989.jpg', '62593614-45f0-4820-a4ae-812eb453b1bd.jpg')) {
        $response = $http.GetAsync("$origin/Images/HairCutItemListImages/$name").GetAwaiter().GetResult()
        try {
            Confirm-Check ([int]$response.StatusCode -eq 200 -and $response.Content.Headers.ContentType.MediaType -eq 'image/jpeg') "Original service photo is served: $name"
        } finally { $response.Dispose() }
    }
    foreach ($name in @('2e84001c-3dd3-4734-94e5-11085e968f7f.jpg', '1a7548b5-7b4e-4d02-af7b-3b006602cbba.jpg')) {
        $response = $http.GetAsync("$origin/Images/HairCutItemListImages/$name").GetAwaiter().GetResult()
        try {
            $restored = [int]$response.StatusCode -eq 200 -and $response.Content.Headers.ContentType.MediaType -eq 'image/jpeg'
            $fallback = [int]$response.StatusCode -eq 302 -and $response.Headers.Location.OriginalString.EndsWith('/Images/HairCutItemListImages/service-placeholder.svg') -and $response.Headers.CacheControl.NoStore
            Confirm-Check ($restored -or $fallback) "The unavailable service photo has a fallback or a restored original: $name"
        } finally { $response.Dispose() }
    }
    $response = $http.GetAsync("$origin/Images/HairCutItemListImages/service-placeholder.svg").GetAwaiter().GetResult()
    try {
        Confirm-Check ([int]$response.StatusCode -eq 200 -and $response.Content.Headers.ContentType.MediaType -eq 'image/svg+xml') 'The service photo placeholder is available'
    } finally { $response.Dispose() }
    Write-Output "Passed $checks deployment checks. No bookings, accounts or push registrations were changed."
} finally { $http.Dispose() }
