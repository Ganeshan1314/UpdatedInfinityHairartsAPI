param(
    [uri]$PublicBaseUrl = 'https://infinityhairarts.com/',
    [uri]$OriginBaseUrl,
    [ValidateRange(1, 10)][int]$Samples = 3
)

# Read-only health and anonymous-session probes. Never submits a customer login.
# Run on the API server with -OriginBaseUrl to compare IIS/origin with the public proxy.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$targets = @(@{ Name = 'Public'; BaseUrl = $PublicBaseUrl })
if ($OriginBaseUrl) { $targets += @{ Name = 'Origin'; BaseUrl = $OriginBaseUrl } }

foreach ($target in $targets) {
    $baseUrl = $target.BaseUrl
    if (!$baseUrl.IsAbsoluteUri -or ($baseUrl.Scheme -ne 'https' -and !($baseUrl.Scheme -eq 'http' -and $baseUrl.IsLoopback))) {
        throw 'Use HTTPS for public servers. HTTP is accepted only for a loopback origin.'
    }
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseCookies = $false
    $handler.AllowAutoRedirect = $false
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(10)
    try {
        foreach ($path in @('api/Login/health', 'api/Login/session')) {
            $uri = New-Object System.Uri($baseUrl, $path)
            for ($sample = 1; $sample -le $Samples; $sample++) {
                $clock = [System.Diagnostics.Stopwatch]::StartNew()
                $response = $null
                try {
                    # Buffer the small response so HttpClient's timeout covers the body too.
                    $response = $client.GetAsync($uri).GetAwaiter().GetResult()
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    $build = ''
                    if ($path -eq 'api/Login/health' -and $response.IsSuccessStatusCode) {
                        $build = ($body | ConvertFrom-Json).build
                    }
                    [pscustomobject]@{
                        Target = $target.Name
                        Path = $path
                        Sample = $sample
                        Status = [int]$response.StatusCode
                        TotalMs = [Math]::Round($clock.Elapsed.TotalMilliseconds, 1)
                        Build = $build
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
