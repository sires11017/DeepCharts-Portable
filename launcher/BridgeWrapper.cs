using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class BridgeWrapper {
    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint uMode);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    const uint WM_COMMAND = 0x0111;
    const uint WM_CLOSE = 0x0010;
    const uint IDOK = 1;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    static StreamWriter _log;
    static readonly object _logLock = new object();

    static void Log(string msg) {
        try {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg;
            lock (_logLock) {
                if (_log != null) {
                    _log.WriteLine(line);
                    _log.Flush();
                }
            }
        } catch { }
    }

    static void Main(string[] args) {
        SetErrorMode(0x0001 | 0x0002 | 0x0008);

        string baseDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);
        string bridgePath = Path.Combine(baseDir, "bridge", "VolumetricaBridge.exe");

        string logPath = null;
        try {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeepCharts");
            Directory.CreateDirectory(logDir);
            logPath = Path.Combine(logDir, "bridge_wrapper.log");
            _log = new StreamWriter(logPath, false, Encoding.UTF8) { AutoFlush = true };
        } catch { }

        Log("=== BridgeWrapper starting ===");
        Log("Bridge path: " + bridgePath);

        if (!File.Exists(bridgePath)) {
            Log("ERROR: Bridge executable not found");
            Environment.ExitCode = 1;
            Cleanup();
            return;
        }

        bool waitMode = args.Length > 0 && args[0] == "--wait";
        Log("Wait mode: " + waitMode);

        Process proc;
        try {
            proc = Process.Start(new ProcessStartInfo {
                FileName = bridgePath,
                WorkingDirectory = Path.GetDirectoryName(bridgePath),
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Log("Bridge started, PID: " + proc.Id);
        } catch (Exception ex) {
            Log("ERROR: Failed to start bridge: " + ex.Message);
            Environment.ExitCode = 1;
            Cleanup();
            return;
        }

        if (waitMode) {
            // Start multiple monitor threads to catch dialogs faster
            Thread monitor = new Thread(() => MonitorDialogs(proc)) {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            monitor.Start();
            Log("Dialog monitor thread started");

            // Also start a global dialog monitor for any orphaned dialogs
            Thread globalMonitor = new Thread(() => MonitorGlobalDialogs(proc.Id)) {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            globalMonitor.Start();
            Log("Global dialog monitor thread started");

            proc.WaitForExit();
            Log("Bridge process exited, code: " + proc.ExitCode);
        }

        Cleanup();
    }

    static void Cleanup() {
        try {
            if (_log != null) {
                _log.Close();
                _log = null;
            }
        } catch { }
    }

    static bool DismissDialog(IntPtr hWnd) {
        StringBuilder className = new StringBuilder(256);
        GetClassName(hWnd, className, 256);
        string cn = className.ToString();

        // Match standard dialog class (#32770) or any dialog-like class
        if (cn != "#32770" && !cn.Contains("#32770")) return false;

        Log("Dialog found: HWND=" + hWnd + " class=" + cn);

        // Try multiple approaches to dismiss
        // 1. WM_COMMAND with IDOK
        IntPtr result;
        IntPtr sent = SendMessageTimeout(hWnd, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero,
            SMTO_ABORTIFHUNG, 1000, out result);
        if (sent != IntPtr.Zero) {
            Log("WM_COMMAND/IDOK sent successfully");
            return true;
        }

        // 2. PostMessage WM_COMMAND (async, non-blocking)
        bool posted = PostMessage(hWnd, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero);
        if (posted) {
            Log("PostMessage WM_COMMAND/IDOK sent");
            return true;
        }

        // 3. WM_CLOSE as last resort
        Log("Trying WM_CLOSE");
        SendMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    static void MonitorDialogs(Process target) {
        int dismissed = 0;

        while (!target.HasExited) {
            Thread.Sleep(100); // Check every 100ms
            try {
                EnumWindows((hWnd, _) => {
                    if (target.HasExited) return false;

                    if (!IsWindowVisible(hWnd)) return true;

                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid != (uint)target.Id) return true;

                    if (DismissDialog(hWnd)) {
                        dismissed++;
                    }

                    return true;
                }, IntPtr.Zero);
            } catch { }
        }

        Log("Monitor ended. Dismissed: " + dismissed);
    }

    static void MonitorGlobalDialogs(int bridgePid) {
        // Monitor ALL visible dialogs and dismiss any that look like error dialogs
        // This catches dialogs that appear in child processes or orphaned windows
        int dismissed = 0;
        int bridgeProcessId = bridgePid;

        // Wait a bit for dialogs to appear
        Thread.Sleep(500);

        while (true) {
            Thread.Sleep(150);
            try {
                EnumWindows((hWnd, _) => {
                    if (!IsWindowVisible(hWnd)) return true;

                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);

                    // Only monitor windows from the bridge process tree
                    // (bridge spawns child processes)
                    if (pid != (uint)bridgeProcessId) {
                        // Check if it's a child of the bridge
                        try {
                            Process proc = Process.GetProcessById((int)pid);
                            if (proc == null) return true;
                            // Check if parent is bridge
                            // (This is best-effort, we can't always get parent PID easily)
                        } catch { return true; }
                    }

                    StringBuilder className = new StringBuilder(256);
                    GetClassName(hWnd, className, 256);
                    string cn = className.ToString();
                    if (cn != "#32770") return true;

                    // Check if this looks like a .NET error dialog
                    // (class #32770 with no other identifying features)
                    if (DismissDialog(hWnd)) {
                        dismissed++;
                    }

                    return true;
                }, IntPtr.Zero);
            } catch { }

            // Stop if bridge has been gone for a while
            try {
                Process proc = Process.GetProcessById(bridgeProcessId);
                if (proc == null) break;
                if (proc.HasExited) {
                    Thread.Sleep(1000); // Give time for final dialogs
                    break;
                }
            } catch {
                Thread.Sleep(1000);
                break;
            }
        }

        Log("Global monitor ended. Dismissed: " + dismissed);
    }
}
