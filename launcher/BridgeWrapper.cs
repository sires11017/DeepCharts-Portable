using System;
using System.Runtime.InteropServices;
using System.Diagnostics;

class Program {
    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint uMode);
    
    static void Main() {
        // SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x0008
        SetErrorMode(0x0001 | 0x0002 | 0x0008);
        
        string bridgePath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
            "bridge", "VolumetricaBridge.exe");
        
        Process.Start(new ProcessStartInfo {
            FileName = bridgePath,
            WorkingDirectory = System.IO.Path.GetDirectoryName(bridgePath),
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
