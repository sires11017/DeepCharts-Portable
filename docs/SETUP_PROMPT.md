## Setup Prompt for New PC

Copy everything below this line and paste to an agent on your new machine:

---

You are setting up the DeepCharts Portable trading environment on a fresh Windows PC. Follow these steps exactly, in order. Do not skip steps. Do not debug issues unless a step fails — fix the failure before moving on.

### Step 1: Check Prerequisites

Run these commands and verify the output before proceeding:

```powershell
# Python 3.10+
python --version

# pip
pip --version

# .NET 4.8 (should show Release value >= 528040)
Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full\" -Name Release

# Admin check (must succeed)
net session
```

If Python is missing, install from python.org with "Add to PATH" checked. If .NET 4.8 is missing, it comes pre-installed on Windows 10/11. If you are not admin, open an elevated PowerShell.

### Step 2: Clone the Repository

```powershell
cd C:\Users\$env:USERNAME\Documents
git clone https://github.com/sires11017/DeepCharts-Portable.git DeepCharts
cd DeepCharts
```

### Step 3: Run the Installer (as Admin)

Right-click PowerShell → Run as Administrator, then:

```powershell
cd C:\Users\$env:USERNAME\Documents\DeepCharts
.\scripts\install.ps1
```

This will:
1. Verify Python and .NET prerequisites
2. Generate CA certificates in `certs/mitm_ca/`
3. Add hosts file entries for CQG domains (demoapi.cqg.com, api.cqg.com, depth-it.historical.deepcharts.com, data-b.historical.deepcharts.com)
4. Install Python dependencies (cryptography, websockets, protobuf)
5. Compile the launcher from C# source (`launcher/Launcher.cs` → `Deepchart.exe`)
6. Copy templates from `userdata/` to `Documents\Deepchart\`
7. Create a Windows Scheduled Task `DeepChartsProxy` (runs at login as SYSTEM)
8. Add Windows Defender exclusions
9. Create desktop shortcut

### Step 4: Verify Installation

After the installer completes, check these:

```powershell
# Ports should be bound
Get-NetTCPConnection -LocalPort 443 -ErrorAction SilentlyContinue | Select-Object LocalPort, State
Get-NetTCPConnection -LocalPort 12010 -ErrorAction SilentlyContinue | Select-Object LocalPort, State

# Scheduled task should exist
Get-ScheduledTask -TaskName "DeepChartsProxy" -ErrorAction SilentlyContinue | Select-Object TaskName, State

# Launcher should exist
Get-Item Deepchart.exe | Select-Object Name, Length
```

Expected results:
- Port 443: LISTENING (bridge_mitm_proxy)
- Port 12010: LISTENING (vol_hist_server)
- DeepChartsProxy: Ready
- Deepchart.exe: ~7168 bytes

### Step 5: Launch

Double-click `Deepchart.exe` at the repo root. Everything starts automatically — no console windows.

To pin to taskbar: right-click `Deepchart.exe` → Pin to taskbar.

### Step 6: Connect in Deepchart

1. Open Deepchart
2. Go to Connections → Add New
3. Select CQG
4. Enable "Use Demo Credentials"
5. Enter your AMP/CQG demo credentials
6. Click Connect

### If Things Break

| Problem | Fix |
|---------|-----|
| Port 443 permission denied | Run PowerShell as Admin |
| Python not found | Reinstall with "Add to PATH" checked |
| No module named... | `pip install -r proxy\mitm\requirements.txt` |
| No data after connect | `.\scripts\toggle-hosts.ps1` as Admin |
| Port 443 occupied by iphlpsvc | `net stop iphlpsvc` |
| Deepchart won't start | Kill all: `Get-Process python,Deepchart,Volumetrica* \| Stop-Process -Force` then double-click `Deepchart.exe` |
| Need to use MotiveWave/QuantTower | Run `.\scripts\toggle-hosts.ps1` to remove hosts entries. Run again to re-add for Deepchart. |

### Repo Structure Reference

```
DeepCharts/
├── Deepchart.exe           # Launcher (double-click this)
├── app/                    # Runtime (Deepchart.Core, bridge, DLLs)
├── userdata/               # Templates, workspaces, settings
├── proxy/mitm/             # MITM proxy + config
├── proxy/cqg/              # CQG protobuf definitions
├── certs/mitm_ca/          # TLS certificates
├── launcher/Launcher.cs    # Launcher source
├── scripts/                # Install, proxy service, build, toggle
└── README.md
```
