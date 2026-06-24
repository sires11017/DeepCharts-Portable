# Always use 127.0.0.1 — works on any network, no IP detection needed
$ip = "127.0.0.1"
Write-Host ""
Write-Host "Using: $ip (always localhost)" -ForegroundColor Cyan
Write-Host ""

$entries = @(
    "demoapi.cqg.com",
    "api.cqg.com",
    "depth-it.historical.deepcharts.com",
    "data-b.historical.deepcharts.com"
)
$hostsPath = Join-Path $env:SYSTEMROOT 'System32\drivers\etc\hosts'
$lines = @(Get-Content $hostsPath -Encoding ASCII)
$added = 0; $removed = 0

foreach ($hostname in $entries) {
    # Find ALL lines mentioning this hostname (any IP)
    $existingLines = @($lines | Where-Object { $_ -match "\s+$hostname\s*$" -or $_ -match "\s+$hostname$" })
    $correctEntry = "$ip $hostname"

    if ($existingLines.Count -gt 0) {
        # Check if any are wrong IP or duplicate
        $wrongIp = $existingLines | Where-Object { $_.Trim() -ne $correctEntry }
        if ($wrongIp) {
            # Remove all entries for this hostname, add correct one
            $lines = @($lines | Where-Object { $_ -notmatch "\s+$hostname\s*$" -and $_ -notmatch "\s+$hostname$" })
            $lines += $correctEntry
            Write-Host "  [FIXED] $hostname -> $correctEntry" -ForegroundColor Green
            $removed++
            $added++
        } else {
            Write-Host "  [EXISTS] $correctEntry" -ForegroundColor DarkGray
        }
    } else {
        $lines += $correctEntry
        Write-Host "  [ADDED] $correctEntry" -ForegroundColor Green
        $added++
    }
}

$lines | Out-File $hostsPath -Encoding ascii -Force
Write-Host ""
Write-Host "Done: $added added, $removed fixed." -ForegroundColor Cyan

# Show current hosts state
Write-Host ""
Write-Host "Current hosts file entries:" -ForegroundColor DarkGray
Get-Content $hostsPath -Encoding ASCII | Where-Object { $_ -match 'cqg\.com|deepcharts\.com' } | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
