using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Blossom;

namespace Raw75.Io;

/// <summary>Native multi-select open / save dialogs (zenity, kdialog, WinForms via PowerShell).</summary>
public static class FileDialogs
{
    private const string PhotoFilterName = "Photos";
    private const string PhotoGlobs =
        "*.jpg *.jpeg *.png *.webp *.tif *.tiff *.dng *.cr2 *.cr3 *.crw *.nef *.nrw *.arw *.srf *.sr2 *.raf *.orf *.rw2 *.pef *.ptx *.raw *.rwl *.3fr *.fff *.mos *.kdc *.dcr *.mrw *.erf *.x3f *.srw *.iiq *.heic *.heif";

    private static string? _lastDir;

    public static string[]? OpenPhotos()
    {
        string start = ExistingDir(_lastDir) ?? PicturesOrHome();

        if (OperatingSystem.IsWindows())
            return Remember(OpenWindows(start, multi: true));

        if (OperatingSystem.IsMacOS())
            return Remember(OpenMac(start));

        return Remember(OpenLinux(start));
    }

    public static string? OpenFolder()
    {
        string start = ExistingDir(_lastDir) ?? PicturesOrHome();
        string[]? one;
        if (OperatingSystem.IsWindows())
            one = OpenFolderWindows(start);
        else if (OperatingSystem.IsMacOS())
            one = OpenFolderMac(start);
        else
            one = OpenFolderLinux(start);

        if (one == null || one.Length == 0)
            return null;

        string? path = NormalizeDir(one[0]);
        if (path == null)
        {
            Log.Warning("Folder dialog returned a path that is not a directory: '" + one[0] + "'");
            return null;
        }

        _lastDir = path;
        return path;
    }

    /// <summary>Turn a dialog/URI/file path into an existing directory, or null.</summary>
    public static string? NormalizeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string p = path.Trim().Trim('"').Trim('\'');
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(p, UriKind.Absolute, out Uri? uri)
            && uri.IsFile)
        {
            p = uri.LocalPath;
        }

        return ExistingDir(p);
    }

    public static string? SavePhoto(string defaultPath)
    {
        defaultPath ??= "";
        string? dir = ExistingDir(Path.GetDirectoryName(defaultPath))
                      ?? ExistingDir(_lastDir)
                      ?? PicturesOrHome();
        string name = Path.GetFileName(defaultPath);
        if (string.IsNullOrWhiteSpace(name))
            name = "export.jpg";
        string suggested = Path.Combine(dir, name);

        string[]? one;
        if (OperatingSystem.IsWindows())
            one = SaveWindows(suggested);
        else if (OperatingSystem.IsMacOS())
            one = SaveMac(suggested);
        else
            one = SaveLinux(suggested);

        if (one == null || one.Length == 0)
            return null;

        Remember(one);
        return one[0];
    }

    private static string[]? Remember(string[]? paths)
    {
        if (paths == null || paths.Length == 0)
            return paths;
        string? dir = Path.GetDirectoryName(paths[0]);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            _lastDir = dir;
        return paths;
    }

    private static string[]? OpenLinux(string start)
    {
        var zenity = Run("zenity",
            "--file-selection",
            "--multiple",
            "--separator=|",
            "--title=Open photos",
            "--filename=" + TrailingSep(start),
            "--file-filter=" + PhotoFilterName + " | " + PhotoGlobs,
            "--file-filter=All files | *");
        if (zenity.Status == RunStatus.Ok)
            return SplitPaths(zenity.Output, '|');
        if (zenity.Status == RunStatus.Cancel)
            return null;

        var kdialog = Run("kdialog",
            "--multiple",
            "--separate-output",
            "--title", "Open photos",
            "--getopenfilename",
            TrailingSep(start),
            PhotoFilterName + " (" + PhotoGlobs + ")");
        if (kdialog.Status == RunStatus.Ok)
            return SplitPaths(kdialog.Output, '\n');
        return null;
    }

    private static string[]? SaveLinux(string suggested)
    {
        var zenity = Run("zenity",
            "--file-selection",
            "--save",
            "--confirm-overwrite",
            "--title=Export photo",
            "--filename=" + suggested,
            "--file-filter=" + PhotoFilterName + " | " + PhotoGlobs,
            "--file-filter=All files | *");
        if (zenity.Status == RunStatus.Ok)
            return SplitPaths(zenity.Output, '|');
        if (zenity.Status == RunStatus.Cancel)
            return null;

        var kdialog = Run("kdialog",
            "--title", "Export photo",
            "--getsavefilename",
            suggested,
            PhotoFilterName + " (" + PhotoGlobs + ")");
        if (kdialog.Status == RunStatus.Ok)
            return SplitPaths(kdialog.Output, '\n');
        return null;
    }

    private static string[]? OpenWindows(string start, bool multi)
    {
        string filter = WinFilter();
        string startEsc = PsSingle(start);
        string filterEsc = PsSingle(filter);
        string script = multi
            ? "$d = New-Object System.Windows.Forms.OpenFileDialog; $d.Multiselect = $true; $d.Title = 'Open photos'; $d.InitialDirectory = " + startEsc + "; $d.Filter = " + filterEsc + "; if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write(($d.FileNames -join '|')) }"
            : "$d = New-Object System.Windows.Forms.OpenFileDialog; $d.Title = 'Open photos'; $d.InitialDirectory = " + startEsc + "; $d.Filter = " + filterEsc + "; if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write($d.FileName) }";
        var r = RunPowerShell(script);
        if (r.Status != RunStatus.Ok)
            return null;
        return SplitPaths(r.Output, '|');
    }

    private static string[]? SaveWindows(string suggested)
    {
        string dir = ExistingDir(Path.GetDirectoryName(suggested)) ?? PicturesOrHome();
        string name = Path.GetFileName(suggested);
        string script =
            "$d = New-Object System.Windows.Forms.SaveFileDialog; $d.Title = 'Export photo'; $d.OverwritePrompt = $true; $d.InitialDirectory = " +
            PsSingle(dir) + "; $d.FileName = " + PsSingle(name) + "; $d.Filter = " + PsSingle(WinFilter()) +
            "; if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write($d.FileName) }";
        var r = RunPowerShell(script);
        if (r.Status != RunStatus.Ok)
            return null;
        return SplitPaths(r.Output, '|');
    }

    private static string[]? OpenFolderLinux(string start)
    {
        var zenity = Run("zenity",
            "--file-selection",
            "--directory",
            "--title=Workspace folder",
            "--filename=" + TrailingSep(start));
        if (zenity.Status == RunStatus.Ok)
            return SplitPaths(zenity.Output, '|');
        if (zenity.Status == RunStatus.Cancel)
            return null;

        var kdialog = Run("kdialog",
            "--title", "Workspace folder",
            "--getexistingdirectory",
            TrailingSep(start));
        if (kdialog.Status == RunStatus.Ok)
            return SplitPaths(kdialog.Output, '\n');
        return null;
    }

    private static string[]? OpenFolderWindows(string start)
    {
        string script =
            "$d = New-Object System.Windows.Forms.FolderBrowserDialog; $d.Description = 'Workspace folder'; $d.SelectedPath = " +
            PsSingle(start) + "; if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write($d.SelectedPath) }";
        var r = RunPowerShell(script);
        if (r.Status != RunStatus.Ok)
            return null;
        return SplitPaths(r.Output, '|');
    }

    private static string[]? OpenFolderMac(string start)
    {
        string script =
            "try\n" +
            "set theFolder to choose folder with prompt \"Workspace folder\" default location POSIX file " +
            AppleString(start) + "\n" +
            "return POSIX path of theFolder\n" +
            "on error\n" +
            "return \"\"\n" +
            "end try";
        var r = Run("osascript", "-e", script);
        if (r.Status != RunStatus.Ok)
            return null;
        return SplitPaths(r.Output, '|');
    }

    private static string[]? OpenMac(string start)
    {
        string script =
            "try\n" +
            "set theFiles to choose file with prompt \"Open photos\" default location POSIX file " +
            AppleString(start) + " with multiple selections allowed\n" +
            "set output to \"\"\n" +
            "repeat with f in theFiles\n" +
            "set output to output & (POSIX path of f) & \"|\"\n" +
            "end repeat\n" +
            "return output\n" +
            "on error\n" +
            "return \"\"\n" +
            "end try";
        var r = Run("osascript", "-e", script);
        if (r.Status != RunStatus.Ok)
            return null;
        return SplitPaths(r.Output, '|');
    }

    private static string[]? SaveMac(string suggested)
    {
        string dir = ExistingDir(Path.GetDirectoryName(suggested)) ?? PicturesOrHome();
        string name = Path.GetFileName(suggested);
        string script =
            "try\n" +
            "set theFile to choose file name with prompt \"Export photo\" default name " +
            AppleString(name) + " default location POSIX file " + AppleString(dir) + "\n" +
            "return POSIX path of theFile\n" +
            "on error\n" +
            "return \"\"\n" +
            "end try";
        var r = Run("osascript", "-e", script);
        if (r.Status != RunStatus.Ok)
            return null;
        return SplitPaths(r.Output, '|');
    }

    private static string WinFilter()
    {
        string semi = PhotoGlobs.Replace(' ', ';');
        return "Photos|" + semi + "|All files|*.*";
    }

    private static string PsSingle(string value) =>
        "'" + (value ?? "").Replace("'", "''") + "'";

    private static string AppleString(string value) =>
        "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string TrailingSep(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) || dir.EndsWith(Path.AltDirectorySeparatorChar)
            ? dir
            : dir + Path.DirectorySeparatorChar;

    private static string PicturesOrHome()
    {
        try
        {
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!string.IsNullOrEmpty(pics) && Directory.Exists(pics))
                return pics;
        }
        catch
        {
            // ignore
        }

        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
                return home;
        }
        catch
        {
            // ignore
        }

        return Directory.GetCurrentDirectory();
    }

    private static string? ExistingDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            if (Directory.Exists(path))
                return Path.GetFullPath(path);
            if (File.Exists(path))
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    return Path.GetFullPath(dir);
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string[]? SplitPaths(string? output, char sep)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        string[] parts = output.Split(new[] { sep, '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>(parts.Length);
        foreach (string raw in parts)
        {
            string p = raw.Trim().Trim('"');
            if (p.Length == 0)
                continue;
            if (p.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                p = Uri.UnescapeDataString(p.Substring("file://".Length));
            list.Add(p);
        }

        return list.Count == 0 ? null : list.ToArray();
    }

    private enum RunStatus
    {
        NotFound,
        Cancel,
        Ok
    }

    private readonly struct RunResult
    {
        public RunStatus Status { get; init; }
        public string Output { get; init; }
    }

    private static RunResult Run(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p == null)
                return new RunResult { Status = RunStatus.NotFound, Output = "" };

            string stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
                return new RunResult { Status = RunStatus.Cancel, Output = stdout ?? "" };

            return new RunResult { Status = RunStatus.Ok, Output = stdout ?? "" };
        }
        catch (Exception ex)
        {
            Log.Warning($"FileDialogs: {fileName} unavailable ({ex.Message})");
            return new RunResult { Status = RunStatus.NotFound, Output = "" };
        }
    }

    private static RunResult RunPowerShell(string command)
    {
        string prelude = "Add-Type -AssemblyName System.Windows.Forms; ";
        var result = Run("powershell.exe", "-NoProfile", "-STA", "-Command", prelude + command);
        if (result.Status != RunStatus.NotFound)
            return result;
        return Run("pwsh", "-NoProfile", "-STA", "-Command", prelude + command);
    }
}
