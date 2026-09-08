using System;
using System.Runtime.InteropServices;
using Blossom;

namespace Raw75.Imaging;

/// <summary>
/// SIGSEGV never becomes a C# exception. <c>signal()</c> logs the last breadcrumb
/// then the default terminate still runs (handler is one-shot via SIG_DFL restore).
/// </summary>
internal static class NativeCrash
{
    private const int SIGABRT = 6;
    private const int SIGBUS = 7;
    private const int SIGSEGV = 11;
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
            LibcSignal(SIGSEGV, fn);
            LibcSignal(SIGABRT, fn);
            LibcSignal(SIGBUS, fn);
            _installed = true;
            Log.Info("Native crash handlers installed (SIGSEGV/SIGABRT/SIGBUS).");
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

        string name = signal switch
        {
            SIGSEGV => "SIGSEGV",
            SIGABRT => "SIGABRT",
            SIGBUS => "SIGBUS",
            _ => "signal " + signal
        };
        Log.WriteNativeCrash(name);
    }
}
