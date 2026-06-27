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
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    const uint WM_COMMAND = 0x0111;
    const uint WM_CLOSE = 0x0010;
    const uint BM_CLICK = 0x00F5;
    const uint IDOK = 1;

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
        SetErrorMode(0x0001 | 0x0002 | 0x0004 | 0x0008);

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
            Thread monitor = new Thread(() => MonitorDialogs(proc)) {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            monitor.Start();
            Log("Per-process dialog monitor started");

            Thread globalMonitor = new Thread(() => MonitorGlobalDialogs()) {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            globalMonitor.Start();
            Log("Global dialog monitor started");

            proc.WaitForExit();
            Log("Bridge exited, code: " + proc.ExitCode);

            Thread.Sleep(5000);
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

    static void ClickDialogButton(IntPtr hDialog) {
        IntPtr hBtn = FindWindowEx(hDialog, IntPtr.Zero, "Button", "OK");
        if (hBtn == IntPtr.Zero)
            hBtn = FindWindowEx(hDialog, IntPtr.Zero, "Button", "&OK");
        if (hBtn == IntPtr.Zero)
            hBtn = FindWindowEx(hDialog, IntPtr.Zero, "Button", null);

        if (hBtn != IntPtr.Zero) {
            Log("Found button, sending BM_CLICK");
            PostMessage(hBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }
    }

    static void DismissDialog(IntPtr hWnd) {
        StringBuilder cn = new StringBuilder(256);
        GetClassName(hWnd, cn, 256);
        if (cn.ToString() != "#32770") return;

        Log("Dialog found: HWND=" + hWnd);

        ClickDialogButton(hWnd);

        PostMessage(hWnd, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero);

        SendMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    static void MonitorDialogs(Process target) {
        int dismissed = 0;

        while (!target.HasExited) {
            Thread.Sleep(100);
            try {
                EnumWindows((hWnd, _) => {
                    if (target.HasExited) return false;
                    if (!IsWindowVisible(hWnd)) return true;

                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid != (uint)target.Id) return true;

                    DismissDialog(hWnd);
                    dismissed++;
                    return true;
                }, IntPtr.Zero);
            } catch { }
        }

        Log("Per-process monitor ended. Dismissed: " + dismissed);
    }

    static void MonitorGlobalDialogs() {
        int dismissed = 0;

        Thread.Sleep(300);

        for (int i = 0; i < 300; i++) {
            Thread.Sleep(100);
            try {
                EnumWindows((hWnd, _) => {
                    if (!IsWindowVisible(hWnd)) return true;

                    StringBuilder cn = new StringBuilder(256);
                    GetClassName(hWnd, cn, 256);
                    if (cn.ToString() != "#32770") return true;

                    DismissDialog(hWnd);
                    dismissed++;
                    return true;
                }, IntPtr.Zero);
            } catch { }
        }

        Log("Global monitor ended. Dismissed: " + dismissed);
    }
}
