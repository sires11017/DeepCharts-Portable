# DeepCharts Portable

A self-contained, portable DeepCharts trading environment with a CQG MITM proxy, one-click launcher, and all templates/workspaces/indicators bundled. Clone → install → launch. One click, everything starts, no console windows.

## Quick Start (New PC)

### Prerequisites

| Requirement | Check | Install if missing |
|-------------|-------|--------------------|
| **Windows 10/11** | `echo %OS%` → `Windows_NT` | N/A |
| **Admin access** | `net session` | Only needed for one-time install |

Python 3 and Git are **auto-installed** by the installer if missing.

.NET Framework 4.8 is already installed on all Windows 10/11 systems.

### Step 1: Clone and Install

Open PowerShell as Administrator, then run:

```powershell
cd "$env:USERPROFILE\Documents"
git clone https://github.com/sires11017/DeepCharts-Portable.git DeepCharts
cd DeepCharts
.\scripts\install.ps1
```

> **No Python or Git?** Install Git first from [git-scm.com](https://git-scm.com/download/win), then run the 3 commands above. The installer will auto-install Python 3 for you.

What `install.ps1` does (one-time, requires Admin):
1. Auto-installs Git and Python 3 if missing
2. Verifies Python 3 and .NET 4.8
2. Generates CA certificates in `certs/mitm_ca/`
3. Adds hosts file entries for CQG domains (127.0.0.1)
4. Installs Python dependencies (cryptography, websockets, protobuf)
5. Compiles the launcher from C# source
6. Copies templates to `Documents\Deepchart\`
7. Installs auto-start on boot via Windows Startup folder
8. Adds Windows Defender exclusions
9. Creates desktop shortcut

### Step 2: Launch

Close the Administrator window. Then either:
- **Double-click** `Deepchart.exe` at the repo root
- **Use the desktop shortcut** created by install
- **Pin to taskbar**: right-click `Deepchart.exe` → Pin to taskbar

Everything starts automatically — proxy, bridge, Deepchart. No console windows.

### Step 3: Connect in Deepchart

1. Open Deepchart
2. Go to Connections → Add New
3. Select CQG
4. Enable "Use Demo Credentials"
5. Enter your AMP/CQG demo credentials
6. Click Connect

### Troubleshooting

| Problem | Fix |
|---------|-----|
| Port 443 permission denied | Run PowerShell as Admin |
| Python not found | Reinstall with "Add to PATH" checked |
| No module named... | `pip install -r proxy\mitm\requirements.txt` |
| No data after connect | `.\scripts\toggle-hosts.ps1` as Admin |
| Port 443 occupied by iphlpsvc | `net stop iphlpsvc` |
| Deepchart won't start | Kill all: `Get-Process python,Deepchart,Volumetrica* \| Stop-Process -Force` then double-click `Deepchart.exe` |
| Need to use MotiveWave/QuantTower | Run `.\scripts\toggle-hosts.ps1` to remove hosts entries. Run again to re-add for Deepchart. |

Run `.\scripts\diagnose.ps1` to get a full diagnostic report.

---

## How It Works

```
Deepchart.exe ──▶ VolumetricaBridge.exe ──▶ Bridge MITM Proxy ──▶ CQG Servers
                         │
                         └──▶ Volumetrica Historical Mock Server
```

1. **Launcher** (`Deepchart.exe`) — Checks proxy ports, starts VolumetricaBridge + Deepchart.Core.exe. No console window.

2. **`proxy/mitm/bridge_mitm_proxy.py`** — Intercepts Bridge↔CQG WebSocket. Patches logon credentials to `AMPConnect`. Injects synthetic BBA quotes.

3. **`proxy/mitm/vol_hist_server.py`** — Mock historical data server. Responds with compressed protobuf keepalives.

4. **`proxy/cqg/`** — CQG WebAPI protobuf definitions, generated Python code, test scripts.

## Repo Structure

```
DeepCharts-Portable/
├── Deepchart.exe              # Launcher (double-click to run)
├── app/                       # Runtime binaries
│   ├── Deepchart.Core.exe     # Patched Deepchart
│   ├── BridgeWrapper.exe      # Dialog auto-dismiss wrapper
│   ├── bridge/                # VolumetricaBridge + DLLs
│   ├── Default/               # Symbol DB, exchanges
│   ├── Sounds/                # Voice + alert sounds
│   └── de/es/it/zh/           # Localization
├── userdata/                  # Templates, workspaces, settings
│   ├── Workspace/             # Saved chart workspaces
│   ├── Template/              # Chart templates
│   ├── Indicator Template/    # Indicator presets
│   ├── Settings/              # General + symbol configs
│   └── Alert Sound/           # Alert WAVs
├── proxy/
│   ├── mitm/                  # MITM proxy + vol_hist server
│   └── cqg/                   # CQG protobuf definitions + tests
├── certs/mitm_ca/             # TLS certificates (generated)
├── launcher/                  # C# source code
│   ├── Launcher.cs            # Main launcher
│   └── BridgeWrapper.cs       # Dialog auto-dismiss
├── scripts/
│   ├── install.ps1            # One-time admin setup
│   ├── start-deepcharts.ps1   # Full startup chain
│   ├── startup.bat            # Boot auto-start
│   ├── find-python.ps1        # Shared Python detection
│   ├── proxy_service.ps1      # Background service
│   ├── build_launcher.ps1     # Compile launcher
│   ├── diagnose.ps1           # Full diagnostic
│   └── toggle-hosts.ps1       # Toggle CQG hosts entries
└── docs/
```

## Configuration

All settings in `proxy/mitm/config.py`, overridable via environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `VOL_HIST_PORT` | `12010` | Historical mock server port |
| `BRIDGE_PROXY_PORT` | `443` | Bridge MITM proxy port |
| `REAL_CQG_HOST` | `208.48.16.22` | Real CQG upstream IP |
| `SNI_HOST` | `demoapi.cqg.com` | SNI hostname |
| `TARGET_PRIVATE_LABEL` | `AMPConnect` | Patched logon label |
| `LOG_LEVEL` | `DEBUG` | Logging verbosity |

## Toggling Between Deepchart and Other Platforms

If you use MotiveWave or QuantTower with CQG:

```powershell
# Remove hosts entries (use MW/QT normally)
.\scripts\toggle-hosts.ps1    # Run as Admin

# Re-add hosts entries (use Deepchart again)
.\scripts\toggle-hosts.ps1    # Run as Admin
```

## Manual Server Start (without launcher)

```powershell
# Start proxy (admin)
python proxy\mitm\bridge_mitm_proxy.py
python proxy\mitm\vol_hist_server.py

# Start app
app\bridge\VolumetricaBridge.exe
app\Deepchart.Core.exe
```

## Recompile Launcher

If you modify `launcher/Launcher.cs` or `launcher/BridgeWrapper.cs`:

```powershell
.\scripts\build_launcher.ps1
```

Output: `Deepchart.exe` at repo root, `app\BridgeWrapper.exe`.

## Logs

- `logs/bridge_mitm_YYYYMMDD_HHMMSS.log` — Full protobuf trace
- `logs/vol_hist_YYYYMMDD_HHMMSS.log` — Historical server activity
- `logs/launcher.log` — Launcher startup trace
- `%APPDATA%\DeepCharts\bridge_wrapper.log` — BridgeWrapper dialog dismissals

## File Reference

| File | Purpose |
|------|---------|
| `Deepchart.exe` | Launcher — starts everything, no console |
| `app/Deepchart.Core.exe` | Patched Deepchart core |
| `app/BridgeWrapper.exe` | Auto-dismisses .NET error dialogs |
| `app/bridge/VolumetricaBridge.exe` | Patched VolumetricaBridge |
| `proxy/mitm/bridge_mitm_proxy.py` | MITM proxy — intercepts Bridge↔CQG |
| `proxy/mitm/vol_hist_server.py` | Mock historical data server |
| `proxy/mitm/config.py` | Central config (env-var overridable) |
| `proxy/cqg/WebAPI/` | CQG protobuf Python definitions |
| `proxy/cqg/proto/` | CQG .proto files + protoc.exe |
| `certs/mitm_ca/` | TLS certificates (generated at install) |
| `launcher/Launcher.cs` | C# launcher source |
| `launcher/BridgeWrapper.cs` | C# wrapper source |
| `scripts/install.ps1` | One-time admin setup |
| `scripts/start-deepcharts.ps1` | Full startup chain |
| `scripts/startup.bat` | Boot auto-start entry point |
| `scripts/find-python.ps1` | Shared Python detection |
| `scripts/diagnose.ps1` | Full diagnostic report |
| `scripts/proxy_service.ps1` | Background proxy service |
| `scripts/toggle-hosts.ps1` | Toggle CQG hosts entries |
| `userdata/` | Templates, workspaces, settings, sounds |

## Disclaimer

For educational and testing purposes only. Use with your own CQG demo account.
