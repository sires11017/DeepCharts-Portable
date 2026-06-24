# DeepCharts Portable

A self-contained, portable DeepCharts trading environment with a CQG MITM proxy, one-click launcher, and all templates/workspaces/indicators bundled.

```
DeepCharts-Portable/
├── Deepchart.exe           # Launcher (double-click to run)
├── app/                    # Runtime binaries (hidden from user)
│   ├── Deepchart.Core.exe  # Patched Deepchart (4.5 MB)
│   ├── bridge/             # VolumetricaBridge + 50+ DLLs
│   ├── Default/            # Symbol DB, exchanges
│   ├── Sounds/             # Voice + alert sounds
│   ├── Resources/          # Splash video
│   └── de/es/it/zh/        # Localization
├── userdata/               # Templates, workspaces, settings
│   ├── Workspace/          # Saved chart workspaces
│   ├── Template/           # Chart templates
│   ├── Indicator Template/ # Indicator presets
│   ├── Settings/           # General + symbol configs
│   ├── Trading Account/    # Sim + live accounts
│   ├── SimAccount/         # Sim definitions
│   ├── Hist Fills/         # Trade fill history
│   └── Alert Sound/        # Alert WAVs
├── proxy/
│   ├── mitm/               # MITM proxy + vol_hist server
│   └── cqg/                # CQG protobuf definitions + tests
├── certs/mitm_ca/          # TLS certificates
├── launcher/Launcher.cs    # C# launcher source
├── scripts/
│   ├── install.ps1         # One-click admin setup
│   ├── proxy_service.ps1   # Background service
│   ├── build_launcher.ps1  # Compile launcher
│   └── toggle-hosts.ps1    # Toggle CQG hosts entries
└── docs/
```

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

## Fresh-Clone Setup (New PC)

### Prerequisites

| Requirement | Check | Install if missing |
|-------------|-------|--------------------|
| **Windows OS** | `echo %OS%` → `Windows_NT` | N/A |
| **Python 3.10+** | `python --version` | python.org, check "Add to PATH" |
| **pip** | `pip --version` | Comes with Python |
| **Admin access** | `net session` | Need admin for hosts + port 443 |
| **.NET 4.8** | Already on Win10/11 | N/A |

### Step 1: Clone

```powershell
cd C:\Users\YourName\Documents
git clone https://github.com/sires11017/DeepCharts-Portable.git DeepCharts
cd DeepCharts
```

### Step 2: Install (one-time, as Admin)

```powershell
.\scripts\install.ps1
```

What it does:
1. Verifies Python 3 and .NET 4.8
2. Generates CA certificates (first run)
3. Adds hosts file entries for CQG domains
4. Installs Python dependencies
5. Compiles the launcher from source
6. Copies templates to `Documents\Deepchart\`
7. Creates `DeepChartsProxy` scheduled task (runs at login)
8. Adds Windows Defender exclusions

### Step 3: Launch

Double-click `Deepchart.exe` at the repo root. Or use the desktop shortcut.

That's it. Everything starts automatically — proxy, bridge, Deepchart. No console windows.

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

If you modify `launcher/Launcher.cs`:

```powershell
.\scripts\build_launcher.ps1
```

Output: `Deepchart.exe` at repo root.

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Port 443 permission denied | Run as Admin |
| Python not found | Reinstall with "Add to PATH" |
| No module named... | `pip install -r proxy\mitm\requirements.txt` |
| No data after connect | Re-run `.\scripts\toggle-hosts.ps1` as Admin |
| Port 443 occupied | `net stop iphlpsvc` or let installer handle it |

## Logs

Each run creates timestamped logs in `logs/`:
- `bridge_mitm_YYYYMMDD_HHMMSS.log` — Full protobuf trace
- `vol_hist_YYYYMMDD_HHMMSS.log` — Historical server activity

## File Reference

| File | Purpose |
|------|---------|
| `Deepchart.exe` | Launcher — starts everything, no console |
| `app/Deepchart.Core.exe` | Patched Deepchart core |
| `app/bridge/VolumetricaBridge.exe` | Patched VolumetricaBridge |
| `proxy/mitm/bridge_mitm_proxy.py` | MITM proxy — intercepts Bridge↔CQG |
| `proxy/mitm/vol_hist_server.py` | Mock historical data server |
| `proxy/mitm/config.py` | Central config (env-var overridable) |
| `proxy/cqg/WebAPI/` | CQG protobuf Python definitions |
| `proxy/cqg/proto/` | CQG .proto files + protoc.exe |
| `certs/mitm_ca/` | TLS certificates |
| `launcher/Launcher.cs` | C# launcher source |
| `scripts/install.ps1` | One-time admin setup |
| `scripts/proxy_service.ps1` | Background proxy service |
| `scripts/build_launcher.ps1` | Compile launcher |
| `scripts/toggle-hosts.ps1` | Toggle CQG hosts entries |
| `userdata/` | Templates, workspaces, settings, sounds |

## Disclaimer

For educational and testing purposes only. Use with your own CQG demo account.
