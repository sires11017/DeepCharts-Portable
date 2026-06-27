using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

class DeepChartsLauncher
{
    static string BaseDir;

    [STAThread]
    static void Main()
    {
        BaseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string coreExe = Path.Combine(BaseDir, "app", "Deepchart.Core.exe");

        if (!File.Exists(coreExe))
        {
            Fail("Deepchart.Core.exe not found in " + BaseDir);
            return;
        }

        bool firstCreated;
        Mutex mtx = null;
        try
        {
            mtx = new Mutex(true, "DeepChartsLauncher", out firstCreated);
            if (!firstCreated)
            {
                BringExistingToFront();
                return;
            }
            Run();
        }
        finally
        {
            if (mtx != null) mtx.Close();
        }
    }

    static void Run()
    {
        if (!EnsureProxies()) return;
        StartBridge();
        StartCore();
    }

    static bool EnsureProxies()
    {
        if (AreProxiesUp()) return true;

        if (!StartProxies())
        {
            if (!StartProxyService())
            {
                Fail("Could not start proxy services.\nMake sure Python 3 is installed and run install.ps1 as Admin first.");
                return false;
            }
        }

        if (!WaitForProxiesReady(30))
        {
            Fail("Proxy ports (443, 12010) did not become available.\nCheck logs/ folder for errors.");
            return false;
        }
        return true;
    }

    static bool AreProxiesUp()
    {
        return IsPortListening(443) && IsPortListening(12010);
    }

    static bool IsPortListening(int port)
    {
        try
        {
            IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var ep in props.GetActiveTcpListeners())
                if (ep.Port == port) return true;
        }
        catch { }
        return false;
    }

    static bool WaitForProxiesReady(int maxSec)
    {
        for (int i = 0; i < maxSec; i++)
        {
            if (AreProxiesUp()) return true;
            Thread.Sleep(1000);
        }
        return AreProxiesUp();
    }

    static string FindPython()
    {
        string configPath = Path.Combine(BaseDir, ".python_path");
        if (File.Exists(configPath))
        {
            string saved = File.ReadAllText(configPath).Trim();
            if (saved.Length > 0 && File.Exists(saved)) return saved;
        }

        foreach (string cmd in new[] { "python", "python3" })
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("where", cmd);
                psi.RedirectStandardOutput = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
                if (p.ExitCode == 0 && output.Length > 0)
                {
                    string path = output.Split('\n')[0].Trim();
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
        }

        try
        {
            ProcessStartInfo psi = new ProcessStartInfo("py", "-3 -c \"import sys; print(sys.executable)\"");
            psi.RedirectStandardOutput = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (output.Length > 0 && File.Exists(output)) return output;
        }
        catch { }

        string[] searchPatterns = new[] {
            @"C:\Python3*\python.exe",
            @"C:\Python3*\python3.exe",
        };
        foreach (string pattern in searchPatterns)
        {
            try
            {
                string dir = Path.GetDirectoryName(pattern);
                if (Directory.Exists(dir))
                {
                    foreach (string d in Directory.GetDirectories(dir, "Python3*"))
                    {
                        string exe = Path.Combine(d, "python.exe");
                        if (File.Exists(exe)) return exe;
                    }
                }
            }
            catch { }
        }

        string appDataPython = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python");
        if (Directory.Exists(appDataPython))
        {
            foreach (string d in Directory.GetDirectories(appDataPython, "Python3*"))
            {
                string exe = Path.Combine(d, "python.exe");
                if (File.Exists(exe)) return exe;
            }
        }

        return null;
    }

    static bool StartProxies()
    {
        string python = FindPython();
        if (python == null) return false;

        string proxyScript = Path.Combine(BaseDir, "proxy", "mitm", "bridge_mitm_proxy.py");
        string histScript = Path.Combine(BaseDir, "proxy", "mitm", "vol_hist_server.py");
        string proxyWorkDir = Path.Combine(BaseDir, "proxy", "mitm");

        if (!File.Exists(proxyScript) || !File.Exists(histScript)) return false;

        if (!StartHiddenProcess(python, "\"" + histScript + "\"", proxyWorkDir))
            return false;

        Thread.Sleep(2000);

        if (!StartHiddenProcess(python, "\"" + proxyScript + "\"", proxyWorkDir))
            return false;

        return true;
    }

    static bool StartHiddenProcess(string exe, string args, string workDir)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = workDir;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    static bool StartProxyService()
    {
        try
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ps = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(ps)) ps = "powershell.exe";
            string proxyScript = Path.Combine(BaseDir, "scripts", "proxy_service.ps1");
            string args = "-NoProfile -ExecutionPolicy Bypass -File \"" + proxyScript + "\"";
            ProcessStartInfo psi = new ProcessStartInfo(ps, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    static void StartBridge()
    {
        string bridgeDir = Path.Combine(BaseDir, "app", "bridge");
        string bridgeExe = Path.Combine(bridgeDir, "VolumetricaBridge.exe");
        string wrapperExe = Path.Combine(BaseDir, "app", "BridgeWrapper.exe");

        if (!File.Exists(bridgeExe)) return;

        Process bridge = new Process();
        if (File.Exists(wrapperExe))
        {
            bridge.StartInfo.FileName = wrapperExe;
            bridge.StartInfo.Arguments = "--wait";
        }
        else
        {
            bridge.StartInfo.FileName = bridgeExe;
        }
        bridge.StartInfo.WorkingDirectory = bridgeDir;
        bridge.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
        bridge.StartInfo.CreateNoWindow = true;
        bridge.StartInfo.UseShellExecute = false;
        try { bridge.Start(); }
        catch { }
    }

    static void StartCore()
    {
        Process core = new Process();
        core.StartInfo.FileName = Path.Combine(BaseDir, "app", "Deepchart.Core.exe");
        core.StartInfo.WorkingDirectory = BaseDir;
        try { core.Start(); }
        catch (Exception ex)
        {
            Fail("Failed to start Deepchart: " + ex.Message);
            return;
        }

        core.WaitForExit();
    }

    static void BringExistingToFront()
    {
        foreach (Process p in Process.GetProcessesByName("Deepchart.Core"))
        {
            try { p.Refresh(); } catch { }
        }
    }

    static void Fail(string msg)
    {
        MessageBox.Show(msg, "DeepCharts Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
