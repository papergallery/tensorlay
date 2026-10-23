using System.Diagnostics;

namespace TensorLay.Mac;

// "Open TensorLay at login" (the shared AutostartWithWindows setting) as a
// per-user LaunchAgent that runs `open -a <bundle>` at login. A plist in
// ~/Library/LaunchAgents needs no entitlement or helper app, unlike
// SMAppService, which an ad-hoc-signed bundle can't use reliably.
public static class LoginItem
{
    private const string Label = "com.tensorlay.app.login";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{Label}.plist");

    public static void Apply(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(PlistPath)) File.Delete(PlistPath);
                return;
            }

            string? bundle = CurrentBundlePath();
            if (bundle is null) return; // not running from an .app (dev build)

            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key><string>{Label}</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>/usr/bin/open</string>
                        <string>-a</string>
                        <string>{System.Security.SecurityElement.Escape(bundle)}</string>
                    </array>
                    <key>RunAtLoad</key><true/>
                </dict>
                </plist>
                """);
        }
        catch
        {
            // Best-effort: a failed login item must not break saving settings.
        }
    }

    // .../TensorLay.app/Contents/MacOS/TensorLay -> .../TensorLay.app
    public static string? CurrentBundlePath()
    {
        string? exe = Process.GetCurrentProcess().MainModule?.FileName;
        string? bundle = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe ?? "")));
        return bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }
}
