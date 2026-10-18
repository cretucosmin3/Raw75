using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Blossom;

namespace Raw75.Update;

/// <summary>
/// Whether and how this copy of Raw75 can install updates itself. Only published release builds can: the
/// self-contained folder from a release archive, started through its own executable, on a platform that has release
/// archives. A development build (<c>dotnet run</c>, <c>dotnet Raw75.dll</c>) is still told about updates, but only
/// gets the release page.
/// </summary>
public static class SelfUpdate
{
    private static readonly string BaseDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    private static readonly string Executable = OperatingSystem.IsWindows() ? "Raw75.exe" : "Raw75";

    /// <summary>The release archive this build installs from, or null when it can't update itself.</summary>
    public static string? PackageName { get; } = IsReleaseBuild() ? UpdateChecker.PackageName(RuntimeInformation.RuntimeIdentifier) : null;

    public static UpdateInstaller CreateInstaller() =>
        new(BaseDir, $"Raw75-{RuntimeInformation.RuntimeIdentifier}", Executable);

    private static bool IsReleaseBuild()
    {
        string? process = Environment.ProcessPath;
        return process is not null
            && string.Equals(Path.GetFileName(process), Executable, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(BaseDir, "System.Private.CoreLib.dll")); // self-contained
    }

    /// <summary>
    /// Deletes what a previous update retired, retrying for a while: right after an update the old process may still
    /// be exiting and hold its files (Windows). Runs in the background; does nothing for development builds.
    /// </summary>
    public static void CleanUpInBackground()
    {
        if (PackageName is null)
            return;
        Task.Run(async () =>
        {
            using UpdateInstaller installer = CreateInstaller();
            for (int attempt = 0; attempt < 10 && !installer.CleanUp(); attempt++)
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        });
    }

    /// <summary>Opens <paramref name="url"/> in the default browser.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not open {url}: {ex.Message}");
        }
    }

    /// <summary>Starts the (just updated) app as a new process; false when it could not be started.</summary>
    public static bool Relaunch()
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(BaseDir, Executable))
            {
                WorkingDirectory = BaseDir,
                UseShellExecute = false
            };
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not restart Raw75 after the update: {ex.Message}");
            return false;
        }
    }
}
