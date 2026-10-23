using System.Runtime.InteropServices;

namespace TensorLay.Mac;

// Opt out of App Nap. TensorLay is mostly a background app — a tunnel, a
// bridge listener, health checks and relay polling — and macOS naps apps
// whose windows are hidden or occluded, stretching their timers to minutes.
// That drops SSH keepalives and stalls remote requests. An activity from
// -[NSProcessInfo beginActivityWithOptions:reason:] with "user initiated,
// allowing idle system sleep" keeps the process awake without stopping the
// Mac itself from sleeping. The token is held for the process lifetime.
// (Info.plist also sets NSAppSleepDisabled as a belt-and-braces.)
internal static class AppNap
{
    // NSActivityUserInitiatedAllowingIdleSystemSleep
    private const ulong UserInitiatedAllowingIdleSystemSleep = 0x00FFFFFFUL & ~(1UL << 20);

    private static IntPtr _activity;

    public static void Disable()
    {
        if (!OperatingSystem.IsMacOS() || _activity != IntPtr.Zero) return;
        try
        {
            IntPtr processInfo = objc_msgSend(objc_getClass("NSProcessInfo"), sel_registerName("processInfo"));
            IntPtr reason = objc_msgSend(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"),
                "TensorLay keeps the SSH tunnel and command bridge alive");
            _activity = objc_msgSend(processInfo, sel_registerName("beginActivityWithOptions:reason:"),
                UserInitiatedAllowingIdleSystemSleep, reason);
            // Retain explicitly: the token is autoreleased and there is no
            // pool on this thread that would otherwise own it for us.
            if (_activity != IntPtr.Zero)
                objc_msgSend(_activity, sel_registerName("retain"));
        }
        catch
        {
            // Napping only slows timers down; never fail startup over it.
        }
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC)] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, string arg);
    [DllImport(ObjC)] private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);
}
