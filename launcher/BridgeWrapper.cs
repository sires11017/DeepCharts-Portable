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
            Thread monitor = new Thread(() => MonitorDialogs(proc)) {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            monitor.Start();
            Log("Dialog monitor thread started");
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

    static void MonitorDialogs(Process target) {
        int dismissed = 0;

        while (!target.HasExited) {
            Thread.Sleep(200);
            try {
                EnumWindows((hWnd, _) => {
                    if (target.HasExited) return false;

                    if (!IsWindowVisible(hWnd)) return true;

                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid != (uint)target.Id) return true;

                    StringBuilder className = new StringBuilder(256);
                    GetClassName(hWnd, className, 256);
                    if (className.ToString() != "#32770") return true;

                    Log("Dialog found: HWND=" + hWnd);

                    IntPtr result;
                    IntPtr sent = SendMessageTimeout(hWnd, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero,
                        SMTO_ABORTIFHUNG, 2000, out result);

                    if (sent != IntPtr.Zero) {
                        Log("WM_COMMAND/IDOK sent successfully");
                        dismissed++;
                    } else {
                        Log("WM_COMMAND failed, trying WM_CLOSE");
                        SendMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        dismissed++;
                    }

                    return true;
                }, IntPtr.Zero);
            } catch { }
        }

        Log("Monitor ended. Dismissed: " + dismissed);
    }
}
