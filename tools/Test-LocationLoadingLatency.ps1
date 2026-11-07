param(
    [uri]$PublicBaseUrl = 'https://infinityhairarts.com/',
    [uri]$OriginBaseUrl,
    [ValidateRange(1, 10)][int]$Samples = 3
)

# Anonymous read-only probes. Run on the API host with its loopback origin to
# compare public round trips with controller durations and the database logs.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$targets = @(@{ Name = 'Public'; BaseUrl = $PublicBaseUrl })
if ($OriginBaseUrl) { $targets += @{ Name = 'Origin'; BaseUrl = $OriginBaseUrl } }
foreach ($target in $targets) {
    $baseUrl = $target.BaseUrl
    if (!$baseUrl.IsAbsoluteUri -or $baseUrl.UserInfo -or
        ($baseUrl.Scheme -ne 'https' -and !($baseUrl.Scheme -eq 'http' -and $baseUrl.IsLoopback))) {
        throw 'Use HTTPS for public servers. HTTP is accepted only for a loopback origin, without embedded credentials.'
    }
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseCookies = $false
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    try {
        for ($sample = 1; $sample -le $Samples; $sample++) {
            foreach ($path in @('api/Location/getLocations', 'api/Login/health')) {
                $uri = New-Object System.Uri($baseUrl, $path)
                $timer = [System.Diagnostics.Stopwatch]::StartNew()
                $response = $null
                try {
                    $response = $client.GetAsync($uri).GetAwaiter().GetResult()
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    $serverTiming = $null
                    $timingValues = $null
                    if ($response.Headers.TryGetValues('Server-Timing', [ref]$timingValues)) {
                        $serverTiming = $timingValues -join ', '
                    }
                    $locationCount = $null
                    if ($path -eq 'api/Location/getLocations' -and $response.IsSuccessStatusCode) {
                        $locationCount = @($body | ConvertFrom-Json).Count
                    }
                    [pscustomobject]@{
                        Target = $target.Name
                        Path = $path
                        Sample = $sample
                        Status = [int]$response.StatusCode
                        TotalMs = [Math]::Round($timer.Elapsed.TotalMilliseconds, 1)
                        ServerTiming = $serverTiming
                        LocationCount = $locationCount
                    }
                } finally {
                    if ($response) { $response.Dispose() }
                }
            }
        }
    } finally {
        $client.Dispose()
    }
}
