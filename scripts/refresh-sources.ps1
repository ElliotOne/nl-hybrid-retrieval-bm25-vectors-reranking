param([string]$Revision = 'c023f59008561b0633204140aaccd803139ac435')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($Revision -notmatch '^[a-f0-9]{40}$') { throw 'Pass a full dotnet/docs Git revision.' }
$definitions = @(
    @('caching', '.NET caching', 'core/extensions/caching.md'),
    @('http-resilience', 'Build resilient HTTP applications', 'core/resilience/http-resilience.md'),
    @('httpclient-factory', 'Use IHttpClientFactory', 'core/extensions/httpclient-factory.md'),
    @('factory-troubleshooting', 'Troubleshoot IHttpClientFactory', 'core/extensions/httpclient-factory-troubleshooting.md'),
    @('http-rate-limiter', 'Rate limit an HTTP handler', 'core/extensions/http-ratelimiter.md'),
    @('http-logging', 'HTTP client logging', 'core/extensions/httpclient-logging.md'),
    @('logging', 'Logging in .NET', 'core/extensions/logging/overview.md'),
    @('options', 'Options pattern in .NET', 'core/extensions/options.md'),
    @('options-validation', 'Compile-time options validation', 'core/extensions/options-validation-generator.md'),
    @('dependency-injection', 'Dependency injection in .NET', 'core/extensions/dependency-injection/overview.md'),
    @('workers', 'Worker services in .NET', 'core/extensions/workers.md'),
    @('channels', 'Channels in .NET', 'core/extensions/channels.md')
)
$sources = foreach ($definition in $definitions) {
    $sourcePath = "docs/$($definition[2])"
    $rawUrl = "https://raw.githubusercontent.com/dotnet/docs/$Revision/$sourcePath"
    $temporaryFile = [IO.Path]::GetTempFileName()
    try {
        Invoke-WebRequest $rawUrl -OutFile $temporaryFile -UseBasicParsing
        $checksum = (Get-FileHash -LiteralPath $temporaryFile -Algorithm SHA256).Hash.ToLowerInvariant()
        [ordered]@{
            id = $definition[0]; title = $definition[1]; path = $sourcePath
            url = "https://github.com/dotnet/docs/blob/$Revision/$sourcePath"
            sha256 = $checksum
        }
        Write-Host "Verified $($definition[0])"
    }
    finally { Remove-Item -LiteralPath $temporaryFile }
}
$manifest = [ordered]@{
    repository = 'dotnet/docs'; revision = $Revision; license = 'CC-BY-4.0 (documentation); MIT (code examples)'
    sources = @($sources)
}
$output = Join-Path $PSScriptRoot '../data/sources.json'
[IO.File]::WriteAllText([IO.Path]::GetFullPath($output), ($manifest | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
Write-Host 'Updated data/sources.json. Review query judgments and choose a new index name before rebuilding.'
