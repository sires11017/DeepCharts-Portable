param(
    [switch]$Background
)

$ErrorActionPreference = "SilentlyContinue"
$REPO = Split-Path -Parent $PSScriptRoot

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
    # Also fix stale LAN IPs
    if ($hostsContent -match "\d+\.\d+\.\d+\.\d+\s+$([regex]::Escape($domain))" -and $hostsContent -notmatch "127\.0\.0\.1\s+$([regex]::Escape($domain))") {
        $needsUpdate = $true
        break
    }
}

if ($needsUpdate) {
    # Remove old broken entries for these domains
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
    # Add correct entries
    $lines += ""
    $lines += "# DeepCharts CQG proxy entries"
    $lines += $hostsEntries
    Set-Content -Path $hostsFile -Value ($lines -join "`r`n") -Force
}

# ── 2. Kill old processes ──
Get-Process python -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process Deepchart* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process VolumetricaBridge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
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
    Write-Host "ERROR: Python not found"
    exit 1
}

# ── 4. Start proxies ──
$proxyMitmDir = Join-Path $REPO "proxy\mitm"
$histScript = Join-Path $proxyMitmDir "vol_hist_server.py"
$bridgeProxy = Join-Path $proxyMitmDir "bridge_mitm_proxy.py"

function Start-HiddenProcess($exe, $args, $workDir) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $args
    $psi.WorkingDirectory = $workDir
    $psi.UseShellExecute = $true
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    [System.Diagnostics.Process]::Start($psi) | Out-Null
}

Start-HiddenProcess $python "`"$histScript`"" $proxyMitmDir
Start-Sleep -Seconds 2
Start-HiddenProcess $python "`"$bridgeProxy`"" $proxyMitmDir

# Wait for ports
$maxWait = 25
for ($i = 0; $i -lt $maxWait; $i++) {
    Start-Sleep -Seconds 1
    $p443 = netstat -ano | findstr ":443 " | findstr "LISTENING"
    $p12010 = netstat -ano | findstr ":12010 " | findstr "LISTENING"
    if ($p443 -and $p12010) { break }
}

# ── 5. Start bridge ──
$bridgeExe = Join-Path $REPO "app\bridge\VolumetricaBridge.exe"
$wrapperExe = Join-Path $REPO "app\BridgeWrapper.exe"
$bridgeDir = Join-Path $REPO "app\bridge"
if (Test-Path $bridgeExe) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    if (Test-Path $wrapperExe) {
        $psi.FileName = $wrapperExe
        $psi.Arguments = "--wait"
    } else {
        $psi.FileName = $bridgeExe
    }
    $psi.WorkingDirectory = $bridgeDir
    $psi.UseShellExecute = $true
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    [System.Diagnostics.Process]::Start($psi) | Out-Null
    Start-Sleep -Seconds 2
}

# ── 6. Start Deepchart ──
$coreExe = Join-Path $REPO "app\Deepchart.Core.exe"
if (Test-Path $coreExe) {
    Start-Process -FilePath $coreExe -WorkingDirectory $REPO
} else {
    Write-Host "ERROR: Deepchart.Core.exe not found at $coreExe"
}

if (-not $Background) {
    Write-Host "DeepCharts started successfully"
}
