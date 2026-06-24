<#
.SYNOPSIS
    Background proxy service for DeepCharts.
    Runs bridge_mitm_proxy.py and vol_hist_server.py as hidden processes.
    Monitors health, auto-restarts on crash.
    Designed to run as a Windows scheduled task (hidden, no console).
.DESCRIPTION
    This script is invoked by a Windows Scheduled Task at user logon.
    It starts both Python proxy scripts with hidden windows, polls until
    their ports are up, then loops every 5 seconds monitoring them.
    If a process dies, it is restarted automatically.
.NOTES
    Run via: powershell.exe -WindowStyle Hidden -File proxy_service.ps1
#>

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptRoot
$proxyScript = Join-Path (Join-Path $root "proxy") "mitm\bridge_mitm_proxy.py"
$histScript   = Join-Path (Join-Path $root "proxy") "mitm\vol_hist_server.py"

# Find Python - try every possible method
function Find-Python {
    # 1. Environment variable
    if ($env:PYTHON_EXE -and (Test-Path $env:PYTHON_EXE -ErrorAction SilentlyContinue)) {
        return $env:PYTHON_EXE
    }

    # 2. Saved config from installer
    $configFile = Join-Path $root ".python_path"
    if (Test-Path $configFile) {
        $saved = (Get-Content $configFile -Raw).Trim()
        if ($saved -and (Test-Path $saved -ErrorAction SilentlyContinue)) {
            return $saved
        }
    }

    # 3. Try commands in PATH
    foreach ($cmd in @("python", "python3")) {
        try {
            $info = Get-Command $cmd -ErrorAction SilentlyContinue
            if ($info -and $info.Source -and (Test-Path $info.Source)) {
                return $info.Source
            }
        } catch {}
    }

    # 4. Try py launcher
    try {
        $pyPath = (Get-Command "py" -ErrorAction SilentlyContinue).Source
        if ($pyPath) {
            $test = & $pyPath -3 --version 2>&1
            if ($test -match "Python 3") {
                return $pyPath
            }
        }
    } catch {}

    # 5. Search common install locations (wildcards)
    $searchPatterns = @(
        "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe",
        "C:\Python3*\python.exe",
        "$env:ProgramFiles\Python3*\python.exe",
        "$env:ProgramFiles(x86)\Python3*\python.exe"
    )
    foreach ($pattern in $searchPatterns) {
        $found = Resolve-Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) {
            return $found.Path
        }
    }

    # 6. Last resort - just return "python" and hope PATH works
    return "python"
}

$pythonExe = Find-Python
$proxyPort = 443
$histPort  = 12010

Write-Host "[proxy] Python: $pythonExe"

# Prevent multiple instances
$mtxName = "Global\DeepChartsProxyService"
try {
    $mtx = New-Object System.Threading.Mutex($true, $mtxName, [ref]$null)
    if (-not $mtx.WaitOne(0)) { exit 0 }
} catch { }


function Get-ProcessByPort($port) {
    $conn = Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue
    if ($conn) {
        return Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
    }
    return $null
}

function Start-HiddenPython($scriptPath, $port) {
    $existing = Get-ProcessByPort -port $port
    if ($existing) {
        Write-Host "[proxy] Port $port already bound by PID $($existing.Id), assuming running"
        return $existing
    }
    try {
        $proc = Start-Process -FilePath $pythonExe -ArgumentList "`"$scriptPath`"" -PassThru
        Write-Host "[proxy] Started $scriptPath (PID $($proc.Id))"
        return $proc
    } catch {
        Write-Host "[proxy] Failed to start $scriptPath: $_"
        return $null
    }
}

function Wait-ForPort($port, $maxSec) {
    for ($i = 0; $i -lt $maxSec; $i++) {
        if (Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Seconds 1
    }
    return (Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue) -ne $null
}

function Test-ProcessAlive($proc) {
    if (-not $proc) { return $false }
    try { return -not $proc.HasExited } catch { return $false }
}

$global:proxyProc = $null
$global:histProc = $null

Write-Host "[proxy] DeepCharts Proxy Service starting..."
Write-Host "[proxy] Root: $root"

# Main health-check loop
while ($true) {
    # Ensure historical server is running
    if (-not (Test-ProcessAlive $global:histProc)) {
        if (Get-ProcessByPort -port $histPort) {
            Write-Host "[proxy] vol_hist_server port $histPort is bound by another process"
        } else {
            $global:histProc = Start-HiddenPython -scriptPath $histScript -port $histPort
            if (-not (Wait-ForPort $histPort 10)) {
                Write-Host "[proxy] WARNING: vol_hist_server did not bind within 10s"
            }
        }
    }

    # Ensure proxy is running
    if (-not (Test-ProcessAlive $global:proxyProc)) {
        if (Get-ProcessByPort -port $proxyPort) {
            Write-Host "[proxy] bridge_mitm_proxy port $proxyPort is bound by another process"
        } else {
            $global:proxyProc = Start-HiddenPython -scriptPath $proxyScript -port $proxyPort
            if (-not (Wait-ForPort $proxyPort 15)) {
                Write-Host "[proxy] WARNING: bridge_mitm_proxy did not bind within 15s"
            }
        }
    }

    Start-Sleep -Seconds 5
}
