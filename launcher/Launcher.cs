using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
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
            Fail("Could not start proxy services.\nMake sure Python 3 is installed and run install.ps1 as Admin first.");
            return false;
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

        ProcessStartInfo psiHist = new ProcessStartInfo(python, "\"" + histScript + "\"");
        psiHist.WorkingDirectory = proxyWorkDir;
        psiHist.CreateNoWindow = true;
        psiHist.UseShellExecute = false;
        try { Process.Start(psiHist); }
        catch { return false; }

        Thread.Sleep(2000);

        ProcessStartInfo psiProxy = new ProcessStartInfo(python, "\"" + proxyScript + "\"");
        psiProxy.WorkingDirectory = proxyWorkDir;
        psiProxy.CreateNoWindow = true;
        psiProxy.UseShellExecute = false;
        try { Process.Start(psiProxy); }
        catch { return false; }

        return true;
    }

    static void StartBridge()
    {
        string bridgeDir = Path.Combine(BaseDir, "app", "bridge");
        string bridgeExe = Path.Combine(bridgeDir, "VolumetricaBridge.exe");
        string wrapperExe = Path.Combine(BaseDir, "app", "BridgeWrapper.exe");

        if (!File.Exists(bridgeExe)) return;

        ProcessStartInfo psi = new ProcessStartInfo();
        if (File.Exists(wrapperExe))
        {
            psi.FileName = wrapperExe;
            psi.Arguments = "--wait";
        }
        else
        {
            psi.FileName = bridgeExe;
        }
        psi.WorkingDirectory = bridgeDir;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        try { Process.Start(psi); }
        catch { }
    }

    static void StartCore()
    {
        ProcessStartInfo psi = new ProcessStartInfo(
            Path.Combine(BaseDir, "app", "Deepchart.Core.exe"));
        psi.WorkingDirectory = BaseDir;
        psi.UseShellExecute = false;

        Process core;
        try { core = Process.Start(psi); }
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
