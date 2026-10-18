using System;
using System.Runtime.InteropServices;
using Blossom;

namespace Raw75.Imaging;

/// <summary>
/// Logs the last breadcrumb when the process aborts on a native fault (e.g. inside LibRaw),
/// then the default terminate still runs (handler is one-shot via SIG_DFL restore).
/// Only SIGABRT is hooked: the .NET runtime owns SIGSEGV/SIGBUS to raise NullReferenceException
/// from managed code, and a managed handler there kills the process ("attempted to call an
/// UnmanagedCallersOnly method from managed code"). A native fault outside managed code still
/// ends in the runtime calling abort(), which lands here.
/// </summary>
internal static class NativeCrash
{
    private const int SIGABRT = 6;
    private static readonly nint SigDfl = 0;

    private static readonly SignalCallback Handler = OnSignal;
    private static GCHandle _keepAlive;
    private static bool _installed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SignalCallback(int signal);

    [DllImport("libc", EntryPoint = "signal", SetLastError = true)]
    private static extern nint LibcSignal(int signum, nint handler);

    public static void Install()
    {
        if (_installed || OperatingSystem.IsWindows())
            return;

        try
        {
            _keepAlive = GCHandle.Alloc(Handler);
            nint fn = Marshal.GetFunctionPointerForDelegate(Handler);
            LibcSignal(SIGABRT, fn);
            _installed = true;
            Log.Info("Native crash handler installed (SIGABRT).");
        }
        catch (Exception ex)
        {
            Log.Warning("Could not install native crash handlers: " + ex.Message);
        }
    }

    private static void OnSignal(int signal)
    {
        try
        {
            LibcSignal(signal, SigDfl);
        }
        catch
        {
        }

        Log.WriteNativeCrash(signal == SIGABRT ? "SIGABRT" : "signal " + signal);
    }
}
