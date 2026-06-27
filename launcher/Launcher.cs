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
    static string LogPath;
    static StreamWriter LogWriter;

    [STAThread]
    static void Main()
    {
        BaseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        LogPath = Path.Combine(BaseDir, "logs", "launcher.log");

        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)); } catch { }
        try { LogWriter = new StreamWriter(LogPath, false) { AutoFlush = true }; } catch { }

        Log("=== Launcher starting ===");
        Log("Base dir: " + BaseDir);

        string coreExe = Path.Combine(BaseDir, "app", "Deepchart.Core.exe");
        if (!File.Exists(coreExe))
        {
            Fail("Deepchart.Core.exe not found in " + BaseDir);
            Log("FATAL: Deepchart.Core.exe not found");
            return;
        }

        bool firstCreated;
        Mutex mtx = null;
        try
        {
            mtx = new Mutex(true, "DeepChartsLauncher", out firstCreated);
            if (!firstCreated)
            {
                Log("Another instance already running");
                Fail("DeepCharts is already running.");
                return;
            }
            Run();
        }
        finally
        {
            if (mtx != null) mtx.Close();
            if (LogWriter != null) { LogWriter.Close(); LogWriter = null; }
        }
    }

    static void Log(string msg)
    {
        if (LogWriter == null) return;
        try { LogWriter.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + msg); } catch { }
    }

    static void Run()
    {
        if (!EnsureProxies()) return;
        StartBridge();
        StartCore();
    }

    static bool EnsureProxies()
    {
        if (AreProxiesUp())
        {
            Log("Proxies already running");
            return true;
        }

        if (!StartProxies())
        {
            Log("Failed to start proxies");
            Fail("Could not start proxy services.\nMake sure Python 3 is installed and run install.ps1 as Admin first.");
            return false;
        }

        if (!WaitForProxiesReady(30))
        {
            Log("Proxies failed to become ready within 30s");
            Fail("Proxy ports (443, 12010) did not become available.\nCheck logs/ folder for errors.");
            return false;
        }

        Log("Proxies ready");
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
            if (saved.Length > 0 && File.Exists(saved))
            {
                Log("Python (config): " + saved);
                return saved;
            }
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
                    if (File.Exists(path))
                    {
                        Log("Python (where " + cmd + "): " + path);
                        return path;
                    }
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
            if (output.Length > 0 && File.Exists(output))
            {
                Log("Python (py -3): " + output);
                return output;
            }
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
                        if (File.Exists(exe))
                        {
                            Log("Python (wildcard): " + exe);
                            return exe;
                        }
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
                if (File.Exists(exe))
                {
                    Log("Python (AppData): " + exe);
                    return exe;
                }
            }
        }

        Log("Python not found");
        return null;
    }

    static bool StartProxies()
    {
        string python = FindPython();
        if (python == null) return false;

        string proxyScript = Path.Combine(BaseDir, "proxy", "mitm", "bridge_mitm_proxy.py");
        string histScript = Path.Combine(BaseDir, "proxy", "mitm", "vol_hist_server.py");
        string proxyWorkDir = Path.Combine(BaseDir, "proxy", "mitm");

        if (!File.Exists(proxyScript) || !File.Exists(histScript))
        {
            Log("Proxy scripts not found");
            return false;
        }

        ProcessStartInfo psiHist = new ProcessStartInfo(python, "\"" + histScript + "\"");
        psiHist.WorkingDirectory = proxyWorkDir;
        psiHist.CreateNoWindow = true;
        psiHist.UseShellExecute = false;
        try
        {
            Process hist = Process.Start(psiHist);
            Log("vol_hist_server started, PID: " + hist.Id);
        }
        catch (Exception ex)
        {
            Log("Failed to start vol_hist_server: " + ex.Message);
            return false;
        }

        Thread.Sleep(2000);

        ProcessStartInfo psiProxy = new ProcessStartInfo(python, "\"" + proxyScript + "\"");
        psiProxy.WorkingDirectory = proxyWorkDir;
        psiProxy.CreateNoWindow = true;
        psiProxy.UseShellExecute = false;
        try
        {
            Process proxy = Process.Start(psiProxy);
            Log("bridge_mitm_proxy started, PID: " + proxy.Id);
        }
        catch (Exception ex)
        {
            Log("Failed to start bridge_mitm_proxy: " + ex.Message);
            return false;
        }

        return true;
    }

    static void StartBridge()
    {
        string bridgeDir = Path.Combine(BaseDir, "app", "bridge");
        string bridgeExe = Path.Combine(bridgeDir, "VolumetricaBridge.exe");
        string wrapperExe = Path.Combine(BaseDir, "app", "BridgeWrapper.exe");

        if (!File.Exists(bridgeExe))
        {
            Log("VolumetricaBridge.exe not found, skipping");
            return;
        }

        ProcessStartInfo psi = new ProcessStartInfo();
        if (File.Exists(wrapperExe))
        {
            psi.FileName = wrapperExe;
            psi.Arguments = "--wait";
            Log("Starting bridge via BridgeWrapper");
        }
        else
        {
            psi.FileName = bridgeExe;
            Log("Starting bridge directly (no wrapper)");
        }
        psi.WorkingDirectory = bridgeDir;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        try
        {
            Process bridge = Process.Start(psi);
            Log("Bridge started, PID: " + bridge.Id);
        }
        catch (Exception ex)
        {
            Log("Failed to start bridge: " + ex.Message);
        }
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
            Log("Failed to start core: " + ex.Message);
            Fail("Failed to start Deepchart: " + ex.Message);
            return;
        }

        Log("Core started, PID: " + core.Id);
        core.WaitForExit();
        Log("Core exited, code: " + core.ExitCode);
    }

    static void Fail(string msg)
    {
        MessageBox.Show(msg, "DeepCharts Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
