param(
    [switch]$Background
)

$ErrorActionPreference = "SilentlyContinue"
$REPO = Split-Path -Parent $PSScriptRoot

function Write-Log($msg) { if (-not $Background) { Write-Host $msg } }

# ── 1. Ensure hosts file has CQG entries ──
$hostsFile = "$env:SystemRoot\System32\drivers\etc\hosts"
$hostsEntries = @(
    "127.0.0.1 demoapi.cqg.com",
    "127.0.0.1 api.cqg.com",
    "127.0.0.1 depth-it.historical.deepcharts.com",
    "127.0.0.1 data-b.historical.deepcharts.com"
)
$hostsContent = Get-Content $hostsFile -Raw -ErrorAction SilentlyContinue
$needsUpdate = $false
foreach ($entry in $hostsEntries) {
    $domain = ($entry -split "\s+")[1]
    if ($hostsContent -notmatch "127\.0\.0\.1\s+$([regex]::Escape($domain))") {
        $needsUpdate = $true
        break
    }
    if ($hostsContent -match "\d+\.\d+\.\d+\.\d+\s+$([regex]::Escape($domain))" -and $hostsContent -notmatch "127\.0\.0\.1\s+$([regex]::Escape($domain))") {
        $needsUpdate = $true
        break
    }
}

if ($needsUpdate) {
    $lines = Get-Content $hostsFile | Where-Object {
        $line = $_
        $keep = $true
        foreach ($entry in $hostsEntries) {
            $domain = ($entry -split "\s+")[1]
            if ($line -match "\s+$([regex]::Escape($domain))(\s|$)") {
                $keep = $false
                break
            }
        }
        $keep
    }
    $lines += ""
    $lines += "# DeepCharts CQG proxy entries"
    $lines += $hostsEntries
    Set-Content -Path $hostsFile -Value ($lines -join "`r`n") -Force
}

# ── 2. Kill old processes ──
Get-Process python -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process Deepchart* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process VolumetricaBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process BridgeWrapper -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# ── 3. Find Python ──
$python = $null
$pyPathFile = Join-Path $REPO ".python_path"
if (Test-Path $pyPathFile) {
    $saved = Get-Content $pyPathFile -Raw | ForEach-Object { $_.Trim() }
    if ($saved -and (Test-Path $saved)) { $python = $saved }
}
if (-not $python) {
    foreach ($cmd in @("python", "python3", "py")) {
        $found = Get-Command $cmd -ErrorAction SilentlyContinue
        if ($found) { $python = $found.Source; break }
    }
}
if (-not $python) {
    $locations = @(
        "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe",
        "C:\Python3*\python.exe"
    )
    foreach ($loc in $locations) {
        $match = Get-Item $loc -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($match) { $python = $match.FullName; break }
    }
}
if (-not $python) {
    Write-Log "ERROR: Python not found"
    exit 1
}

# ── 4. Start proxies ──
$proxyMitmDir = Join-Path $REPO "proxy\mitm"
$histScript = Join-Path $proxyMitmDir "vol_hist_server.py"
$bridgeProxy = Join-Path $proxyMitmDir "bridge_mitm_proxy.py"

$histProc = Start-Process -FilePath $python -ArgumentList "`"$histScript`"" -WorkingDirectory $proxyMitmDir -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 2
$proxyProc = Start-Process -FilePath $python -ArgumentList "`"$bridgeProxy`"" -WorkingDirectory $proxyMitmDir -WindowStyle Hidden -PassThru

# ── 5. Verify proxies started and wait for ports ──
$proxyReady = $false
$maxWait = 30
for ($i = 0; $i -lt $maxWait; $i++) {
    Start-Sleep -Seconds 1
    $p443 = netstat -ano 2>$null | findstr ":443 " | findstr "LISTENING"
    $p12010 = netstat -ano 2>$null | findstr ":12010 " | findstr "LISTENING"
    if ($p443 -and $p12010) {
        $proxyReady = $true
        break
    }
    # Check if processes died
    if ($histProc -and $histProc.HasExited) {
        Write-Log "ERROR: vol_hist_server exited (code $($histProc.ExitCode))"
        break
    }
    if ($proxyProc -and $proxyProc.HasExited) {
        Write-Log "ERROR: bridge_mitm_proxy exited (code $($proxyProc.ExitCode))"
        break
    }
}

if (-not $proxyReady) {
    Write-Log "WARNING: Proxy ports not ready after ${maxWait}s."
}

# ── 6. Start bridge via wrapper ──
$bridgeExe = Join-Path $REPO "app\bridge\VolumetricaBridge.exe"
$wrapperExe = Join-Path $REPO "app\BridgeWrapper.exe"
$bridgeDir = Join-Path $REPO "app\bridge"
if (Test-Path $bridgeExe) {
    if (Test-Path $wrapperExe) {
        Start-Process -FilePath $wrapperExe -ArgumentList "--wait" -WorkingDirectory $bridgeDir -WindowStyle Hidden
    } else {
        Start-Process -FilePath $bridgeExe -WorkingDirectory $bridgeDir -WindowStyle Hidden
    }
    Start-Sleep -Seconds 2
}

# ── 7. Start Deepchart ──
$coreExe = Join-Path $REPO "app\Deepchart.Core.exe"
if (Test-Path $coreExe) {
    Start-Process -FilePath $coreExe -WorkingDirectory $REPO
} else {
    Write-Log "ERROR: Deepchart.Core.exe not found at $coreExe"
}

if (-not $Background) {
    Write-Host "DeepCharts started successfully"
}
