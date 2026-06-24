# Deepchart CQG Proxy Toolkit

A man-in-the-middle proxy toolkit that lets you run **Deepchart** with a **CQG demo account** by intercepting and patching the connection between Deepchart and CQG's data feed.

## How It Works

```
Deepchart.exe ──▶ VolumetricaBridge.exe ──▶ Bridge MITM Proxy ──▶ CQG Servers
                         │
                         └──▶ Volumetrica Historical Mock Server
```

1. **`bridge_mitm_proxy.py`** — Intercepts the WebSocket connection between the VolumetricaBridge and CQG. Patches logon credentials so demo accounts connect as `AMPConnect`, and injects synthetic Best Bid/Ask quotes into TimeAndSales data.

2. **`vol_hist_server.py`** — Pretends to be Deepchart's own historical data server, responding with valid compressed protobuf keepalives so Deepchart doesn't hang waiting for historical data.

3. **`ipc_mitm.py`** — (Optional) Monitors the WebSocket IPC between Deepchart.exe and VolumetricaBridge.exe for debugging. Two modes: passive listener or full MITM.

---

## AI Agent Setup Guide

> **This section is written for AI coding agents (Claude, Copilot, Codex, etc.)**
> Follow these steps exactly, in order. Each step includes verification so you can confirm success before moving on.

### Step 1: Prerequisites Check

Before doing anything, verify these are available on the machine:

| Requirement | How to Check | What to Do if Missing |
|-------------|-------------|----------------------|
| **Windows OS** | `echo %OS%` should say `Windows_NT` | Stop — this only works on Windows |
| **Python 3.10+** | `python --version` or `py --version` | Install from python.org with "Add to PATH" checked |
| **pip** | `pip --version` | Comes with Python if PATH is set |
| **Administrator access** | `net session` (succeeds = admin) | Need admin for hosts file edits and port 443 binding |
| **CQG demo account** | User must provide credentials | Get from broker (e.g. AMP Futures) |

### Step 2: Clone the Repository

```powershell
cd C:\Users\<username>\Documents
git clone https://github.com/IllNoobis/sharing-charts.git Deepcharts
cd Deepcharts
```

### Step 3: Install Python Dependencies

```powershell
pip install -r requirements.txt
```

This installs:
- `cryptography` (≥44.0) — for TLS cert generation
- `websockets` (≥14.0) — for WebSocket servers
- `protobuf` (≥5.29) — for CQG protobuf decode/encode

**Verify installation:**
```powershell
python -c "import cryptography; import websockets; import google.protobuf; print('All dependencies OK')"
```

### Step 4: Set Up Hosts File (CRITICAL)

This step redirects CQG domains to your local machine so the proxy can intercept them. **This must be done every time you toggle between Deepchart and other platforms (MotiveWave, QuantTower, etc.).**

**Run as Administrator:**
```powershell
.\toggle-proxy-hosts.bat
```

Or use the PowerShell version:
```powershell
powershell -ExecutionPolicy Bypass -File .\toggle-proxy-hosts.ps1
```

**What it does:**
The script auto-detects your LAN IP and adds these entries to `C:\Windows\System32\drivers\etc\hosts`:

```
<your-LAN-IP>  demoapi.cqg.com
<your-LAN-IP>  api.cqg.com
<your-LAN-IP>  depth-it.historical.deepcharts.com
<your-LAN-IP>  data-b.historical.deepcharts.com
```

**Verify the hosts file was updated:**
```powershell
Get-Content C:\Windows\System32\drivers\etc\hosts | Select-String "cqg|deepcharts"
```

You should see 4 lines with your LAN IP pointing to the CQG/deepcharts domains.

If the entries are missing or have the wrong IP, re-run the toggle script.

### Step 5: Start All Servers

**Run as Administrator** (needed for port 443):
```powershell
.\start_servers.ps1
```

This script:
1. Kills any existing `python.exe` processes running the proxy scripts
2. Kills any running `Deepchart` or `VolumetricaBridge` processes
3. Checks if port 443 is free (attempts to stop `iphlpsvc` if occupied)
4. Starts the Volumetrica Historical Mock Server (port 12010)
5. Starts the Bridge MITM Proxy (port 443)
6. Launches `VolumetricaBridge.exe`
7. Launches `Deepchart.exe`

**Verify all services are running:**
```powershell
# Check port bindings
Get-NetTCPConnection -LocalPort 12010 -ErrorAction SilentlyContinue | Select-Object LocalPort, OwningProcess, State
Get-NetTCPConnection -LocalPort 443 -ErrorAction SilentlyContinue | Select-Object LocalPort, OwningProcess, State

# Check processes
Get-Process -Name python, Deepchart, VolumetricaBridge -ErrorAction SilentlyContinue | Select-Object ProcessName, Id
```

You should see:
- Port 12010 bound to a python process (vol_hist_server)
- Port 443 bound to a python process (bridge_mitm_proxy)
- `Deepchart` process running
- `VolumetricaBridge` process running

### Step 6: Connect in Deepchart

1. In Deepchart, go to **Connections** → **Add New**
2. Select **CQG** as the data feed
3. Enable **Use Demo Credentials**
4. Enter your AMP/CQG demo account credentials (username + password)
5. Click **Connect**

**Verify connection:**
- Deepchart should show "Connected" status
- You should see market data loading (charts, DOM, etc.)
- The proxy logs (in `logs/`) will show `LOGON_RESULT: code=0` if successful

### Step 7: Troubleshooting

| Problem | Solution |
|---------|----------|
| "Permission denied" on port 443 | Run PowerShell **as Administrator** |
| "Python is not recognized" | Reinstall Python with "Add Python to PATH" checked |
| "No module named..." | Run `pip install -r requirements.txt` |
| Deepchart connects but no data | Re-run `toggle-proxy-hosts.bat` as Admin (LAN IP may have changed) |
| Port 443 occupied by `iphlpsvc` | `net stop iphlpsvc` or let `start_servers.ps1` handle it |
| Proxy starts but Deepchart won't connect | Kill all and restart: `Get-Process python,Deepchart,Volumetrica* | Stop-Process -Force` then `.\start_servers.ps1` |

---

## Configuration

All settings are in `config.py` and can be overridden via environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `VOL_HIST_PORT` | `12010` | Historical mock server port |
| `BRIDGE_PROXY_PORT` | `443` | Bridge MITM proxy listen port |
| `REAL_CQG_HOST` | `208.48.16.22` | Real CQG upstream IP |
| `SNI_HOST` | `demoapi.cqg.com` | SNI hostname for CQG TLS |
| `TARGET_PRIVATE_LABEL` | `AMPConnect` | Patched logon private label |
| `TARGET_CLIENT_APP_ID` | `AMPConnect` | Patched logon client app ID |
| `TARGET_CLIENT_VERSION` | `7.0.238` | Patched logon client version |
| `LOG_LEVEL` | `DEBUG` | Logging verbosity |

## File Reference

| File | Purpose |
|------|---------|
| `bridge_mitm_proxy.py` | MITM proxy — intercepts Bridge↔CQG WebSocket, patches logon, injects BBA quotes |
| `vol_hist_server.py` | Mock historical data server — responds with valid protobuf keepalives |
| `ipc_mitm.py` | IPC monitor — logs Deepchart↔Bridge WebSocket frames (optional) |
| `config.py` | Central configuration (env-var overridable) |
| `start_servers.ps1` | Launches all servers + Deepchart with port checks and process cleanup |
| `run_patched_deepchart.ps1` | Launches VolumetricaBridge + Deepchart only |
| `toggle-proxy-hosts.bat` | Toggles CQG hosts entries on/off (run as Admin) |
| `toggle-proxy-hosts.ps1` | PowerShell implementation of hosts toggle |
| `dc_toggle_hosts.ps1` | Alternate hosts toggle script |
| `requirements.txt` | Python dependencies: `cryptography`, `websockets`, `protobuf` |
| `patched_run/` | Patched Deepchart and VolumetricaBridge executables + dependencies |
| `patched_run/bridge/` | VolumetricaBridge and all its DLL dependencies |
| `cqg_test/` | CQG protobuf definitions, generated Python protobuf code, and test scripts |
| `mitm_ca/` | Auto-generated CA and server TLS certificates (generated on first run) |

## Toggling Between Deepchart and Other Platforms

If you use MotiveWave or QuantTower with AMP/CQG demo accounts, the hosts file redirect will break those connections:

```powershell
# Remove hosts entries (use MW/QT normally)
.\toggle-proxy-hosts.bat   # Run as Admin

# Re-add hosts entries (use Deepchart again)
.\toggle-proxy-hosts.bat   # Run as Admin
```

The script auto-detects your LAN IP and toggles entries on/off.

## Logs

Each run creates timestamped logs in the `logs/` directory:
- `bridge_mitm_YYYYMMDD_HHMMSS.log` — Full proxy trace with protobuf decode
- `vol_hist_YYYYMMDD_HHMMSS.log` — Historical server activity
- `ipc_mitm_YYYYMMDD_HHMMSS.log` — IPC monitor output (if used)

## Disclaimer

This tool is for **educational and testing purposes only**. Use with your own CQG demo account.
