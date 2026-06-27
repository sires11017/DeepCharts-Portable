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
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    const uint WM_CLOSE = 0x0010;
    const uint MB_OK = 0x00000000;

    static void Main(string[] args) {
        SetErrorMode(0x0001 | 0x0002 | 0x0008);

        string baseDir = System.IO.Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);
        string bridgePath = System.IO.Path.Combine(baseDir, "bridge", "VolumetricaBridge.exe");

        if (!System.IO.File.Exists(bridgePath)) {
            Console.Error.WriteLine("Bridge not found: " + bridgePath);
            Environment.Exit(1);
        }

        bool waitMode = args.Length > 0 && args[0] == "--wait";

        Process proc = Process.Start(new ProcessStartInfo {
            FileName = bridgePath,
            WorkingDirectory = System.IO.Path.GetDirectoryName(bridgePath),
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Thread monitor = new Thread(() => MonitorAndDismissDialogs(proc.Id)) {
            IsBackground = true
        };
        monitor.Start();

        if (waitMode) {
            proc.WaitForExit();
        }
    }

    static void MonitorAndDismissDialogs(int childPid) {
        StringBuilder className = new StringBuilder(256);
        StringBuilder windowText = new StringBuilder(512);

        while (true) {
            Thread.Sleep(150);
            try {
                EnumWindows((hWnd, _) => {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid != (uint)childPid) return true;
                    if (!IsWindowVisible(hWnd)) return true;

                    GetClassName(hWnd, className, 256);
                    string cls = className.ToString();

                    if (cls == "#32770") {
                        GetWindowText(hWnd, windowText, 512);
                        string title = windowText.ToString();

                        if (title.Length == 0 ||
                            title.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf(".NET", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf("mscorlib", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf("XmlSerializ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf("system file", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf("file not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            title.IndexOf("assembly", StringComparison.OrdinalIgnoreCase) >= 0) {
                            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            } catch { }
        }
    }
}
