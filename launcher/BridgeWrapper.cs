using System;
using System.Diagnostics;
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
    static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    const uint WM_COMMAND = 0x0111;
    const uint IDOK = 1;
    const uint IDCANCEL = 2;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    const uint MB_CLASS = 0x0000FFFF;

    static void Main(string[] args) {
        SetErrorMode(0x0001 | 0x0002 | 0x0008);

        string baseDir = System.IO.Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);
        string bridgePath = System.IO.Path.Combine(baseDir, "bridge", "VolumetricaBridge.exe");

        if (!System.IO.File.Exists(bridgePath)) {
            Environment.ExitCode = 1;
            return;
        }

        bool waitMode = args.Length > 0 && args[0] == "--wait";

        Process proc = Process.Start(new ProcessStartInfo {
            FileName = bridgePath,
            WorkingDirectory = System.IO.Path.GetDirectoryName(bridgePath),
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (waitMode) {
            Thread monitor = new Thread(() => MonitorDialogs(proc)) {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            monitor.Start();
            proc.WaitForExit();
        }
    }

    static void MonitorDialogs(Process target) {
        StringBuilder className = new StringBuilder(256);
        IntPtr dummy;

        while (!target.HasExited) {
            Thread.Sleep(200);
            try {
                EnumWindows((hWnd, _) => {
                    if (target.HasExited) return false;

                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid != (uint)target.Id) return true;
                    if (!IsWindowVisible(hWnd)) return true;

                    GetClassName(hWnd, className, 256);
                    if (className.ToString() != "#32770") return true;

                    SendMessageTimeout(hWnd, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero,
                        SMTO_ABORTIFHUNG, 2000, out dummy);
                    return true;
                }, IntPtr.Zero);
            } catch { }
        }
    }
}
