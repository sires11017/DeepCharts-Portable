# Always use 127.0.0.1 — works on any network, no IP detection needed
$ip = "127.0.0.1"
Write-Host ""
Write-Host "Using: $ip (always localhost)" -ForegroundColor Cyan
Write-Host ""
# Hosts entries needed
$entries = @(
    "$ip demoapi.cqg.com"
    "$ip api.cqg.com"
    "$ip depth-it.historical.deepcharts.com"
    "$ip data-b.historical.deepcharts.com"
)
$hostsPath = Join-Path $env:SYSTEMROOT 'System32\drivers\etc\hosts'
$lines = @(Get-Content $hostsPath -Encoding ASCII)
$added = 0; $removed = 0
foreach ($e in $entries) {
    if ($lines -contains $e) {
        $lines = @($lines | Where-Object { $_ -ne $e })
        Write-Host "  [REMOVED] $e" -ForegroundColor Yellow
        $removed++
    } else {
        $lines += $e
        Write-Host "  [ADDED]   $e" -ForegroundColor Green
        $added++
    }
}
$lines | Out-File $hostsPath -Encoding ascii -Force
Write-Host ""
Write-Host "Done: $added added, $removed removed." -ForegroundColor Cyan
# Show current hosts state
Write-Host ""
Write-Host "Current hosts file entries:" -ForegroundColor DarkGray
Get-Content $hostsPath -Encoding ASCII | Where-Object { $_ -match 'cqg\.com|deepcharts\.com' } | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
