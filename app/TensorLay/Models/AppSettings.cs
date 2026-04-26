using System.IO;

namespace TensorLay.Models;

public class AppSettings
{
    public string VpsHost { get; set; } = "";
    public string VpsUser { get; set; } = "root";
    public int SshPort { get; set; } = 22;
    public string SshKeyPath { get; set; } = "";
    public string InstallDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ai-hub");
    public bool AutostartWithWindows { get; set; }
    public bool AutoconnectTunnel { get; set; }
    public Dictionary<string, bool> EnabledServices { get; set; } = new();

    // Pairing state
    public bool IsPaired { get; set; }
}
