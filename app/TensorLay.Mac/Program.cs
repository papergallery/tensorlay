using Avalonia;
using TensorLay.Platform;

namespace TensorLay.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything spawns a process: a Finder-launched app has a bare
        // PATH, and services/installers need our tools dir + Homebrew on it.
        PlatformEnv.Initialize();
        AppNap.Disable();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}
