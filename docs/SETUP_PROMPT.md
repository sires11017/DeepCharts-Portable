## Setup Prompt for New PC

Copy everything below this line and paste to an agent on your new machine:

---

You are setting up the DeepCharts Portable trading platform on a fresh Windows PC. This is a personal charting and template management tool for educational trading research. Follow these steps exactly, in order.

### Step 1: Check Prerequisites

Run these commands in PowerShell and verify output:

    python --version
    pip --version

If Python says "not recognized", install from https://www.python.org/downloads/ with "Add Python to PATH" checked, then restart PowerShell.

### Step 2: Clone the Repository

    cd "$env:USERPROFILE\Documents"
    git clone https://github.com/sires11017/DeepCharts-Portable.git DeepCharts
    cd DeepCharts

### Step 3: Run the Installer (as Admin)

Right-click PowerShell -> Run as Administrator, then:

    cd "$env:USERPROFILE\Documents\DeepCharts"
    .\scripts\install.ps1

This will:
1. Verify Python and .NET prerequisites
2. Generate CA certificates in certs/mitm_ca/
3. Add hosts file entries for CQG domains (127.0.0.1)
4. Install Python dependencies (cryptography, websockets, protobuf)
5. Compile the launcher (launcher/Launcher.cs -> Deepchart.exe)
6. Copy templates from userdata/ to Documents\Deepchart\
7. Create a Windows Scheduled Task DeepChartsProxy
8. Add Windows Defender exclusions
9. Create desktop shortcut

### Step 4: Verify Installation

After the installer completes, check these:

    Get-NetTCPConnection -LocalPort 443 -ErrorAction SilentlyContinue | Select-Object LocalPort, State
    Get-NetTCPConnection -LocalPort 12010 -ErrorAction SilentlyContinue | Select-Object LocalPort, State

Expected: Both ports show LISTENING.

### Step 5: Launch Deepchart

Close the Administrator PowerShell. Open a regular PowerShell and run:

    & "$env:USERPROFILE\Documents\DeepCharts\Deepchart.exe"

Or navigate to your Documents\DeepCharts folder and double-click Deepchart.exe.

To pin to taskbar: right-click Deepchart.exe -> Pin to taskbar.

### Step 6: Connect in Deepchart (IMPORTANT - do not skip)

Once Deepchart opens:
1. Click "Connections" in the top menu
2. Click "Add New"
3. Select "CQG" as the connection type
4. Enable "Use Demo Credentials"
5. Enter your AMP/CQG demo username and password
6. Click "Connect"
7. Wait for the connection status to show "Connected"
8. Open a chart — it should now show live price data

### Step 7: Verify Data Flow

If the chart shows "building chart" or no data:
1. Kill all processes: Get-Process python,Deepchart,Volumetrica* -ErrorAction SilentlyContinue | Stop-Process -Force
2. Re-launch Deepchart.exe
3. Reconnect in Connections -> CQG -> Demo Credentials -> Connect

If still no data, run the diagnostic:

    powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\Documents\DeepCharts\scripts\diagnose.ps1"

Copy the full output and send it for troubleshooting.

### If Things Break

| Problem | Fix |
|---------|-----|
| Port 443 permission denied | Run PowerShell as Admin |
| Python not found | Reinstall with "Add to PATH" checked |
| No module named... | pip install -r proxy\mitm\requirements.txt |
| No data after connect | Run .\scripts\toggle-hosts.ps1 as Admin, then relaunch |
| Deepchart won't start | Kill all: Get-Process python,Deepchart,Volumetrica* \| Stop-Process -Force, then relaunch |
| "Building chart" forever | Run diagnose.ps1, copy output for troubleshooting |

### Repo Structure Reference

    DeepCharts/
    ├── Deepchart.exe           # Launcher (double-click this)
    ├── app/                    # Runtime (Deepchart.Core, bridge, DLLs)
    ├── userdata/               # Templates, workspaces, settings
    ├── proxy/mitm/             # MITM proxy + config
    ├── proxy/cqg/              # CQG protobuf definitions
    ├── certs/mitm_ca/          # TLS certificates
    ├── launcher/Launcher.cs    # Launcher source
    ├── scripts/                # Install, proxy service, build, toggle, diagnose
    └── README.md
