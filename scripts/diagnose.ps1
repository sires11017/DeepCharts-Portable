# DeepCharts Diagnostic Script
# Run this on any PC to check what's wrong
# Usage: powershell -ExecutionPolicy Bypass -File diagnose.ps1

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $root

Write-Host "============================================"
Write-Host "  DeepCharts Diagnostic"
Write-Host "============================================"
Write-Host ""

# 1. Python
Write-Host "--- Python ---"
try { python --version } catch { Write-Host "python: NOT FOUND" }
try { python3 --version } catch { Write-Host "python3: NOT FOUND" }
$configPath = Join-Path $root ".python_path"
if (Test-Path $configPath) {
    $saved = Get-Content $configPath -Raw
    Write-Host "Saved path: $saved"
    if (Test-Path $saved) { Write-Host "Saved path EXISTS: YES" } else { Write-Host "Saved path EXISTS: NO" }
} else { Write-Host ".python_path config: NOT FOUND" }
Write-Host ""

# 2. Ports
Write-Host "--- Ports ---"
$port443 = Get-NetTCPConnection -LocalPort 443 -ErrorAction SilentlyContinue
$port12010 = Get-NetTCPConnection -LocalPort 12010 -ErrorAction SilentlyContinue
if ($port443) { Write-Host "Port 443: LISTENING (PID $($port443[0].OwningProcess))" } else { Write-Host "Port 443: NOT LISTENING" }
if ($port12010) { Write-Host "Port 12010: LISTENING (PID $($port12010[0].OwningProcess))" } else { Write-Host "Port 12010: NOT LISTENING" }
Write-Host ""

# 3. Hosts file
Write-Host "--- Hosts File ---"
$hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
$hostsEntries = Get-Content $hostsPath -Encoding ASCII -ErrorAction SilentlyContinue | Where-Object { $_ -match 'cqg\.com|deepcharts\.com' }
if ($hostsEntries) { $hostsEntries | ForEach-Object { Write-Host "  $_" } } else { Write-Host "  NO CQG/deepcharts entries found!" }
Write-Host ""

# 4. Processes
Write-Host "--- Running Processes ---"
$pyProcs = Get-Process python -ErrorAction SilentlyContinue
$dcProcs = Get-Process Deepchart* -ErrorAction SilentlyContinue
$bridgeProcs = Get-Process Volumetrica* -ErrorAction SilentlyContinue
if ($pyProcs) { $pyProcs | ForEach-Object { Write-Host "  python PID $($_.Id) started $($_.StartTime)" } } else { Write-Host "  python: NOT RUNNING" }
if ($dcProcs) { $dcProcs | ForEach-Object { Write-Host "  Deepchart PID $($_.Id) started $($_.StartTime)" } } else { Write-Host "  Deepchart: NOT RUNNING" }
if ($bridgeProcs) { $bridgeProcs | ForEach-Object { Write-Host "  VolumetricaBridge PID $($_.Id) started $($_.StartTime)" } } else { Write-Host "  VolumetricaBridge: NOT RUNNING" }
Write-Host ""

# 5. Latest logs
Write-Host "--- Latest Logs ---"
$logDir = Join-Path $root "logs"
if (Test-Path $logDir) {
    $logs = Get-ChildItem $logDir -File | Sort-Object LastWriteTime | Select-Object -Last 3
    foreach ($log in $logs) {
        Write-Host ""
        Write-Host "  $($log.Name):"
        Get-Content $log.FullName -Tail 15 | ForEach-Object { Write-Host "    $_" }
    }
} else { Write-Host "  logs/ directory not found" }
Write-Host ""

# 6. Test upstream CQG
Write-Host "--- Upstream CQG ---"
try {
    $result = Test-NetConnection 208.48.16.22 -Port 443 -WarningAction SilentlyContinue
    if ($result.TcpTestSucceeded) { Write-Host "  208.48.16.22:443 REACHABLE" } else { Write-Host "  208.48.16.22:443 BLOCKED" }
} catch { Write-Host "  Test failed: $_" }
Write-Host ""

# 7. Installed files
Write-Host "--- Installed Files ---"
$checks = @(
    "Deepchart.exe",
    "app\Deepchart.Core.exe",
    "app\bridge\VolumetricaBridge.exe",
    "proxy\mitm\bridge_mitm_proxy.py",
    "proxy\mitm\vol_hist_server.py",
    "proxy\mitm\config.py",
    "scripts\install.ps1",
    "certs\mitm_ca\ca.pem"
)
foreach ($f in $checks) {
    $full = Join-Path $root $f
    if (Test-Path $full) { Write-Host "  [OK] $f" } else { Write-Host "  [MISSING] $f" }
}
Write-Host ""
Write-Host "============================================"
Write-Host "  Copy all output above and send it to me"
Write-Host "============================================"
