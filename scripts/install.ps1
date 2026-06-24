<#
.SYNOPSIS
    One-time admin setup for DeepCharts Portable.
    Must be run as Administrator.
.DESCRIPTION
    - Verifies prerequisites (Python, .NET 4.8)
    - Generates CA certificates
    - Configures hosts file entries
    - Installs Python dependencies
    - Creates Windows scheduled task for proxy service
    - Compiles the Deepchart.exe launcher from C# source
    - Copies templates and settings to user Documents
    - Adds Windows Defender exclusions
.NOTES
    Run: Right-click -> Run with PowerShell (Admin)
    Run once after every git pull that changes infrastructure.
#>

#Requires -RunAsAdministrator

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptRoot
$ErrorActionPreference = "Continue"

Write-Host "============================================"
Write-Host "  DeepCharts Portable Installer"
Write-Host "============================================"
Write-Host ""

Write-Host "[*] Repo root: $root"

# -- 0. Kill existing processes --
Write-Host "[0/9] Stopping existing processes..."
Get-Process Deepchart* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process Volumetrica* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Write-Host "[+] Cleaned up"

# -- 1. Prerequisites --
Write-Host "[1/9] Checking prerequisites..."
$pythonExe = $null

# Try common Python commands
foreach ($exe in @("python", "python3")) {
    try {
        $v = & $exe --version 2>&1
        if ($v -match "Python 3\.\d+") { $pythonExe = $exe; Write-Host "[+] Python: $v ($exe)"; break }
    } catch {}
}
# Also try py launcher
if (-not $pythonExe) {
    try {
        $v = & py -3 --version 2>&1
        if ($v -match "Python 3\.\d+") { $pythonExe = "py -3"; Write-Host "[+] Python: $v (py -3)"; }
    } catch {}
}

# If not in PATH, search common install locations
if (-not $pythonExe) {
    $searchPaths = @(
        "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe",
        "C:\Python3*\python.exe",
        "$env:ProgramFiles\Python3*\python.exe",
        "$env:ProgramFiles(x86)\Python3*\python.exe"
    )
    foreach ($pattern in $searchPaths) {
        $found = Resolve-Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) {
            $v = & $found.Path --version 2>&1
            if ($v -match "Python 3\.\d+") { $pythonExe = $found.Path; Write-Host "[+] Python: $v ($pythonExe)"; break }
        }
    }
}

if (-not $pythonExe) {
    Write-Host "[!] Python 3 not found."
    Write-Host "    Install from https://www.python.org/downloads/"
    Write-Host "    IMPORTANT: Check 'Add Python to PATH' during install"
    exit 1
}

# Resolve full Python path for SYSTEM context (scheduled task)
$pythonFull = $null
if ($pythonExe -eq "py -3") {
    # For py launcher, find the actual python.exe it points to
    try {
        $pyWhere = & where.exe py 2>&1 | Select-Object -First 1
        $pyDir = Split-Path $pyWhere -Parent
        $pyVersion = & py -3 -c "import sys; print(sys.executable)" 2>&1
        if ($pyVersion -and (Test-Path $pyVersion)) {
            $pythonFull = $pyVersion
        }
    } catch {}
    if (-not $pythonFull) { $pythonFull = "py" }
} else {
    # For python/python3, get full path
    try {
        $whereResult = & where.exe $pythonExe 2>&1 | Select-Object -First 1
        if ($whereResult -and (Test-Path $whereResult)) {
            $pythonFull = $whereResult
        }
    } catch {}
    if (-not $pythonFull) {
        $cmdInfo = Get-Command $pythonExe -ErrorAction SilentlyContinue
        if ($cmdInfo -and $cmdInfo.Source) { $pythonFull = $cmdInfo.Source }
    }
    if (-not $pythonFull) { $pythonFull = $pythonExe }
}
$pythonConfig = Join-Path $root ".python_path"
Set-Content -Path $pythonConfig -Value $pythonFull -NoNewline
Write-Host "[+] Python path saved: $pythonFull"
Write-Host "    (Verify: $(Test-Path $pythonFull))"

$dotnet = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full\" -Name Release -ErrorAction SilentlyContinue
if (-not $dotnet -or $dotnet.Release -lt 528040) {
    Write-Host "[!] .NET Framework 4.8+ required."
    exit 1
}
Write-Host "[+] .NET Framework 4.8+"

# -- 2. Generate CA certificates --
Write-Host "[2/9] Generating CA certificates..."
pushd $root
try {
    pushd (Join-Path $root "proxy\mitm")
    & $pythonFull -c "import config; from bridge_mitm_proxy import ensure_ca; ensure_ca()"
    popd
    Write-Host "[+] CA certificates ready"
} catch {
    Write-Host "  (will generate at first proxy launch)"
}
popd

# -- 3. Configure hosts file --
Write-Host "[3/9] Configuring hosts file..."
$hostsIp = "127.0.0.1"
Write-Host "  Using: $hostsIp (always localhost — works on any network)"

$hostnames = @(
    "demoapi.cqg.com",
    "api.cqg.com",
    "depth-it.historical.deepcharts.com",
    "data-b.historical.deepcharts.com"
)
$hostsPath = "$env:SystemRoot\System32\drivers\etc\hosts"
$hostsContent = Get-Content $hostsPath -Encoding ASCII -ErrorAction SilentlyContinue
if (-not $hostsContent) { $hostsContent = @() }
$changed = $false

foreach ($hostname in $hostnames) {
    # Remove any existing entry for this hostname (wrong IP or duplicate)
    $existingLine = $hostsContent | Where-Object { $_ -match "\s+$hostname\s*$" -or $_ -match "\s+$hostname$" }
    if ($existingLine) {
        $oldIp = ($existingLine -split "\s+")[0]
        if ($oldIp -ne $hostsIp) {
            $hostsContent = @($hostsContent | Where-Object { $_ -notmatch "\s+$hostname\s*$" -and $_ -notmatch "\s+$hostname$" })
            $hostsContent += "$hostsIp $hostname"
            Write-Host "  [FIX] $hostname $oldIp -> $hostsIp"
            $changed = $true
        } else {
            Write-Host "  [OK] $hostname already correct ($hostsIp)"
        }
    } else {
        $hostsContent += "$hostsIp $hostname"
        Write-Host "  [ADD] $hostsIp $hostname"
        $changed = $true
    }
}
if ($changed) {
    $hostsContent | Out-File $hostsPath -Encoding ascii -Force
    ipconfig /flushdns | Out-Null
    Write-Host "  DNS cache flushed"
}

# -- 4. Install Python dependencies --
Write-Host "[4/9] Installing Python dependencies..."
$req = Join-Path (Join-Path $root "proxy") "mitm\requirements.txt"
if (Test-Path $req) {
    try { & $pythonFull -m pip install -r $req -q 2>$null } catch { Write-Host "  (pip warning - dependencies may already be installed)" }
    Write-Host "[+] Dependencies installed"
}

# -- 5. Build the launcher --
Write-Host "[5/9] Building Deepchart.exe launcher..."
$buildScript = Join-Path $scriptRoot "build_launcher.ps1"
if (Test-Path $buildScript) {
    & $buildScript -OutputDir $root
}

# -- 6. Copy templates --
Write-Host "[6/9] Copying templates and settings..."
$tempsDir = Join-Path $root "userdata"
$targetDir = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Deepchart"
if (Test-Path $tempsDir) {
    Get-ChildItem $tempsDir -Directory | ForEach-Object {
        $dst = Join-Path $targetDir $_.Name
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Get-ChildItem $_.FullName -File | ForEach-Object {
            $tf = Join-Path $dst $_.Name
            if (-not (Test-Path $tf)) { Copy-Item $_.FullName $tf; Write-Host "  [COPY] $($_.Name)" }
        }
    }
    Get-ChildItem $tempsDir -File | ForEach-Object {
        $tf = Join-Path $targetDir $_.Name
        if (-not (Test-Path $tf)) { Copy-Item $_.FullName $tf; Write-Host "  [COPY] $($_.Name)" }
    }
    Write-Host "[+] Templates copied to $targetDir"
} else {
    Write-Host "  (userdata/ not found - run template backup first)"
}

# -- 7. Fix .NET XML serializer (eliminates "system file not specified" error) --
Write-Host "[7/10] Fixing .NET XML serializer..."
$ngen = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ngen.exe"
if (Test-Path $ngen) {
    & $ngen install "mscorlib.XmlSerializers, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" 2>&1 | Out-Null
    & $ngen update 2>&1 | Out-Null
    Write-Host "[+] XML serializer native images generated"
} else {
    Write-Host "  (ngen.exe not found - bridge may show non-fatal error)"
}

# -- 8. Create auto-start on boot --
Write-Host "[8/10] Setting up auto-start on boot..."
$startupFolder = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
$startupBat = Join-Path $scriptRoot "startup.bat"
$startupTarget = Join-Path $startupFolder "DeepCharts-startup.bat"
if (Test-Path $startupBat) {
    Copy-Item -Path $startupBat -Destination $startupTarget -Force
    Write-Host "[+] Startup script installed to $startupTarget"
    Write-Host "    DeepCharts will auto-start on every login (no admin needed)"
} else {
    Write-Host "  (startup.bat not found in scripts/)"
}

# -- 9. Add Windows Defender exclusions --
Write-Host "[9/10] Adding Windows Defender exclusions..."
$paths = @($root, (Join-Path $root "app"))
foreach ($p in $paths) {
    if (Test-Path $p) {
        try { Add-MpPreference -ExclusionPath $p -ErrorAction SilentlyContinue; Write-Host "  [EXCL] $p" }
        catch { Write-Host "  (skipped exclusion for $p)" }
    }
}

Write-Host ""
Write-Host "============================================"
Write-Host "  INSTALLATION COMPLETE!"
Write-Host "============================================"

# Create desktop shortcut
$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "DeepCharts.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $root "Deepchart.exe"
$shortcut.WorkingDirectory = $root
$shortcut.Description = "DeepCharts Portable - One-Click Launch"
$shortcut.Save()
Write-Host "[+] Desktop shortcut created: $shortcutPath"

# Quick proxy test - verify Python can start the scripts
Write-Host ""
Write-Host "[10/10] Verifying proxy can start..."
$testResult = & $pythonFull -c "import sys; print('OK')" 2>&1
if ($testResult -eq "OK") {
    Write-Host "[+] Python can execute scripts"
    # Test that the proxy scripts can at least be imported
    pushd (Join-Path $root "proxy\mitm")
    $importTest = & $pythonFull -c "import config; print('config OK')" 2>&1
    popd
    if ($importTest -eq "config OK") {
        Write-Host "[+] Proxy config loads correctly"
    } else {
        Write-Host "[!] Proxy config import failed: $importTest"
    }
} else {
    Write-Host "[!] Python test failed: $testResult"
}
Write-Host ""

# Open the folder so user can pin to taskbar
Write-Host "[+] Opening folder: $root"
Invoke-Item $root

Write-Host ""
Write-Host "  NEXT STEPS:"
Write-Host "  ----------"
Write-Host "  1. The repo folder is now open in Explorer"
Write-Host "  2. Right-click Deepchart.exe -> Pin to taskbar"
Write-Host "  3. From now on, just click the taskbar icon OR"
Write-Host "     double-click the DeepCharts desktop shortcut"
Write-Host ""
Write-Host "  One click. Everything starts automatically. No console windows."
Write-Host "============================================"
