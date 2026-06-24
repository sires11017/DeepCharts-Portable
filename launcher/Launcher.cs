using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Collections.Generic;

class DeepChartsLauncher
{
    static string BaseDir;
    static List<Process> childProcs = new List<Process>();

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
            KillChildren();
        }
    }

    static void Run()
    {
        // Ensure proxy ports are up
        if (!CheckPort(443) || !CheckPort(12010))
        {
            if (!StartProxies())
            {
                Fail("Could not start proxy services.\nMake sure Python 3 is installed and run install.ps1 as Admin first.");
                return;
            }
            if (!WaitForPorts(443, 12010, 30))
            {
                Fail("Proxy ports (443, 12010) did not start.\nCheck logs/ folder for errors.");
                return;
            }
        }

        // Start VolumetricaBridge
        Process bridge = null;
        string bridgeDir = Path.Combine(BaseDir, "app", "bridge");
        string bridgeExe = Path.Combine(bridgeDir, "VolumetricaBridge.exe");
        if (File.Exists(bridgeExe))
        {
            bridge = new Process();
            bridge.StartInfo.FileName = bridgeExe;
            bridge.StartInfo.WorkingDirectory = bridgeDir;
            bridge.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            bridge.StartInfo.CreateNoWindow = true;
            try { bridge.Start(); childProcs.Add(bridge); }
            catch (Exception ex)
            {
                Fail("Failed to start VolumetricaBridge: " + ex.Message);
                return;
            }
        }

        // Start Deepchart.Core
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

    static string FindPython()
    {
        // 1. Check saved config
        string configPath = Path.Combine(BaseDir, ".python_path");
        if (File.Exists(configPath))
        {
            string saved = File.ReadAllText(configPath).Trim();
            if (saved.Length > 0 && File.Exists(saved)) return saved;
        }

        // 2. Try python command
        foreach (string cmd in new[] { "python", "python3" })
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(cmd, "--version");
                psi.RedirectStandardOutput = true;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (output.Contains("Python 3"))
                {
                    // Get full path
                    ProcessStartInfo psi2 = new ProcessStartInfo("where", cmd);
                    psi2.RedirectStandardOutput = true;
                    psi2.UseShellExecute = false;
                    psi2.CreateNoWindow = true;
                    Process p2 = Process.Start(psi2);
                    string path = p2.StandardOutput.ReadToEnd().Trim();
                    p2.WaitForExit();
                    if (path.Length > 0) return path.Split('\n')[0].Trim();
                }
            }
            catch { }
        }

        // 3. Search common locations
        string[] searchPaths = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
            @"C:\Python3",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Python3",
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\Python3"
        };

        foreach (string baseP in searchPaths)
        {
            if (Directory.Exists(baseP))
            {
                foreach (string dir in Directory.GetDirectories(baseP, "Python3*"))
                {
                    string exe = Path.Combine(dir, "python.exe");
                    if (File.Exists(exe)) return exe;
                }
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

        if (!File.Exists(proxyScript) || !File.Exists(histScript)) return false;

        // Start vol_hist_server
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(python, "\"" + histScript + "\"");
            psi.WorkingDirectory = Path.Combine(BaseDir, "proxy", "mitm");
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.CreateNoWindow = true;
            Process hist = Process.Start(psi);
            childProcs.Add(hist);
        }
        catch { return false; }

        Thread.Sleep(2000);

        // Start bridge_mitm_proxy
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(python, "\"" + proxyScript + "\"");
            psi.WorkingDirectory = Path.Combine(BaseDir, "proxy", "mitm");
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.CreateNoWindow = true;
            Process proxy = Process.Start(psi);
            childProcs.Add(proxy);
        }
        catch { return false; }

        return true;
    }

    static bool CheckPort(int port)
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

    static bool WaitForPorts(int port1, int port2, int maxSec)
    {
        for (int i = 0; i < maxSec; i++)
        {
            if (CheckPort(port1) && CheckPort(port2)) return true;
            Thread.Sleep(1000);
        }
        return CheckPort(port1) && CheckPort(port2);
    }

    static void KillChildren()
    {
        foreach (Process p in childProcs)
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch { }
        }
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
