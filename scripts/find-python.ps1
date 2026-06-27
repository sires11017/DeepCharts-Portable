<#
.SYNOPSIS
    Shared Python detection for DeepCharts.
    Dot-source this script to get $script:PythonExe.
.DESCRIPTION
    Searches for Python 3 using multiple methods:
    1. .python_path config file (from installer)
    2. python / python3 in PATH
    3. py launcher
    4. Common install locations
    5. AppData\Local\Programs\Python
.EXAMPLE
    . .\find-python.ps1
    if ($script:PythonExe) { ... }
#>

$script:PythonExe = $null

# 1. Check saved config from installer
$pyPathFile = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) ".python_path"
if (Test-Path $pyPathFile) {
    try {
        $saved = (Get-Content $pyPathFile -Raw).Trim()
        if ($saved -and (Test-Path $saved)) {
            $script:PythonExe = $saved
            return
        }
    } catch {}
}

# 2. Try commands in PATH
foreach ($cmd in @("python", "python3")) {
    try {
        $info = Get-Command $cmd -ErrorAction SilentlyContinue
        if ($info -and $info.Source -and (Test-Path $info.Source)) {
            $v = & $info.Source --version 2>&1
            if ($v -match "Python 3\.\d+") {
                $script:PythonExe = $info.Source
                return
            }
        }
    } catch {}
}

# 3. Try py launcher
try {
    $pyPath = (Get-Command "py" -ErrorAction SilentlyContinue).Source
    if ($pyPath) {
        $test = & $pyPath -3 --version 2>&1
        if ($test -match "Python 3") {
            $pyExe = & $pyPath -3 -c "import sys; print(sys.executable)" 2>&1
            if ($pyExe -and (Test-Path $pyExe)) {
                $script:PythonExe = $pyExe
                return
            }
            $script:PythonExe = $pyPath
            return
        }
    }
} catch {}

# 4. Search common install locations (wildcards)
$searchPatterns = @(
    "$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe",
    "C:\Python3*\python.exe",
    "$env:ProgramFiles\Python3*\python.exe",
    "$env:ProgramFiles(x86)\Python3*\python.exe"
)
foreach ($pattern in $searchPatterns) {
    try {
        $found = Resolve-Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) {
            $script:PythonExe = $found.Path
            return
        }
    } catch {}
}

# 5. Last resort - just return "python" and hope PATH works
$script:PythonExe = "python"
