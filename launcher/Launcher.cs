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
        // Ensure proxy ports are up
        if (!CheckPort(443) || !CheckPort(12010))
        {
            if (!StartProxyService())
            {
                Fail("Proxy service could not be started.\nRun install.ps1 as admin first.");
                return;
            }
            if (!WaitForPorts(443, 12010, 30))
            {
                Fail("Proxy ports (443, 12010) did not become available.\nCheck that bridge_mitm_proxy.py is working.");
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
            try { bridge.Start(); }
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

        if (bridge != null && !bridge.HasExited)
        {
            try { bridge.CloseMainWindow(); } catch { }
            if (!bridge.WaitForExit(5000))
                try { bridge.Kill(); } catch { }
        }
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

    static bool StartProxyService()
    {
        try
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ps = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(ps)) ps = "powershell.exe";
            string proxyScript = Path.Combine(BaseDir, "scripts", "proxy_service.ps1");
            string args = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + proxyScript + "\"";
            Process p = new Process();
            p.StartInfo.FileName = ps;
            p.StartInfo.Arguments = args;
            p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            return true;
        }
        catch { return false; }
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
