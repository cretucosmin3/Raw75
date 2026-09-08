using System;
using System.IO;
using System.Threading.Tasks;
using Blossom;
using Raw75.Imaging;

namespace Raw75;

internal static class Program
{
    private static readonly string AppName = Path.GetFileNameWithoutExtension(
        Environment.GetCommandLineArgs().Length > 0 ? Environment.GetCommandLineArgs()[0] : "Raw75");

    static void Main()
    {
        Log.Initialize();
        Log.Info($"Starting {AppName}");
        Log.Info($"Application directory: {AppDomain.CurrentDomain.BaseDirectory}");
        LibRawNative.EnsureLoaded();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal($"Unhandled domain exception (IsTerminating={e.IsTerminating}): {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error($"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Info("Application exiting");
        NativeCrash.Install();

        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.Equals("--show-fps", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--fps", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--debug-overlay", StringComparison.OrdinalIgnoreCase))
            {
                Browser.ShowDebugOverlay = true;
            }
        }

        Browser.Initialize(new Raw75Application());
    }
}
