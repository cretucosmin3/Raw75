using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Blossom;

namespace Raw75.Update;

/// <summary>An update could not be downloaded or installed; the message is for the user.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Installs a release over a Raw75 installation folder: downloads the archive for this platform, checks its size and
/// SHA-256 against what GitHub reports, unpacks it next to the installation and checks it holds the announced version
/// (<see cref="DownloadAsync"/>); then swaps it in (<see cref="Apply"/>).
/// <para>
/// Running files are renamed aside (allowed while in use, also on Windows) rather than overwritten, and files of the
/// old version that the new one no longer ships are renamed aside too, so the folder ends up holding exactly the new
/// release. <see cref="CleanUp"/> deletes everything renamed aside on the next start. A failed swap puts it all back.
/// Logs written next to the app are kept.
/// </para>
/// </summary>
public sealed class UpdateInstaller : IDisposable
{
    /// <summary>Folder inside the installation where an update is downloaded and unpacked.</summary>
    public const string StagingFolder = ".raw75-update";

    /// <summary>Suffix of files an update retired (removed by <see cref="CleanUp"/>).</summary>
    public const string OldSuffix = ".raw75-old";

    private const long MaxPackageBytes = 1L << 30;

    // A release folder drops a handful of files between versions; far more means the folder holds other things.
    private const int MaxStaleFiles = 200;
    private readonly HttpClient _http;

    /// <param name="root">The installation folder.</param>
    /// <param name="archiveRoot">The archive's top-level folder that becomes <paramref name="root"/> (e.g. <c>Raw75-linux-x64</c>).</param>
    /// <param name="executable">The executable inside both (<c>Raw75</c> / <c>Raw75.exe</c>).</param>
    public UpdateInstaller(string root, string archiveRoot, string executable)
    {
        Root = Path.GetFullPath(root);
        ArchiveRoot = archiveRoot;
        Executable = executable;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Raw75-updater");
    }

    public string Root { get; }
    public string ArchiveRoot { get; }
    public string Executable { get; }

    private string Staging => Path.Combine(Root, StagingFolder);

    /// <summary>Why this installation can't update itself (e.g. its folder is read-only), or null when it can.</summary>
    public string? CheckInstallable()
    {
        if (!File.Exists(Path.Combine(Root, Executable)))
            return "Raw75 is not running from an installed release.";
        string probe = Path.Combine(Root, $".raw75-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Raw75's folder ({Root}) can't be written to.";
        }
    }

    /// <summary>
    /// Downloads and unpacks <paramref name="update"/>'s archive next to the installation; returns what to pass to
    /// <see cref="Apply"/>. <paramref name="progress"/> gets 0..1 as the download goes.
    /// </summary>
    /// <exception cref="UpdateException">No archive, a failed or damaged download, or an unexpected archive.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        UpdatePackage package = update.Package ?? throw new UpdateException("This release has no download for this platform.");
        if (package.Size > MaxPackageBytes)
            throw new UpdateException("The download is unexpectedly large.");
        DeleteStaging();
        Directory.CreateDirectory(Staging);
        string archive = Path.Combine(Staging, package.Name);
        try
        {
            await DownloadFileAsync(package, archive, progress, ct).ConfigureAwait(false);
            string unpacked = Path.Combine(Staging, "unpacked");
            await Task.Run(() => Unpack(archive, unpacked, ct), ct).ConfigureAwait(false);
            File.Delete(archive);
            string root = Path.Combine(unpacked, ArchiveRoot);
            string exe = Path.Combine(root, Executable);
            if (!File.Exists(exe))
                throw new UpdateException("The download does not contain Raw75.");
            CheckVersion(root, update.Version);
            MakeExecutable(exe);
            return root;
        }
        catch (Exception ex) when (ex is not UpdateException and not OperationCanceledException)
        {
            DeleteStaging();
            throw new UpdateException($"The update could not be downloaded: {ex.Message}", ex);
        }
        catch
        {
            DeleteStaging();
            throw;
        }
    }

    private async Task DownloadFileAsync(UpdatePackage package, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using HttpResponseMessage response = await _http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"The download failed: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        long total = package.Size > 0 ? package.Size : response.Content.Headers.ContentLength ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > MaxPackageBytes || (package.Size > 0 && received > package.Size))
                    throw new UpdateException("The download is larger than the release says.");
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                if (total > 0)
                    progress?.Report(Math.Min(1, (double)received / total));
            }
            if (package.Size > 0 && received != package.Size)
                throw new UpdateException("The download was cut short.");
        }
        if (package.Sha256 is { } expected && Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
            throw new UpdateException("The download is damaged (its checksum does not match the release).");
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    // Both extractors refuse entries (and links) that would land outside `destination`.
    private static void Unpack(string archive, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination);
            return;
        }
        if (!archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("Unknown archive format.");
        ct.ThrowIfCancellationRequested();
        using FileStream file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
    }

    // The unpacked app must be the version the release announced (Raw75.dll's product version, "+sha" aside).
    private static void CheckVersion(string root, AppVersion expected)
    {
        string dll = Path.Combine(root, "Raw75.dll");
        string? product = File.Exists(dll) ? FileVersionInfo.GetVersionInfo(dll).ProductVersion : null;
        if (!AppVersion.TryParse(product, out AppVersion actual) || actual.CompareTo(expected) != 0)
            throw new UpdateException($"The download holds version {product ?? "(unknown)"}, not {expected}.");
    }

    /// <summary>
    /// Moves <paramref name="staged"/> (from <see cref="DownloadAsync"/>) into the installation. Each file it replaces,
    /// and each old file the new release no longer has, is renamed to <c>name</c> + <see cref="OldSuffix"/> first; if
    /// anything fails, the new files are removed, the old ones renamed back, and <see cref="UpdateException"/> is thrown.
    /// </summary>
    public void Apply(string staged)
    {
        string source = Path.GetFullPath(staged);
        if (!source.StartsWith(Staging + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Directory.Exists(source))
            throw new UpdateException("Nothing to install.");

        // Listed up front: the loops move files out of (and into) the folders being listed.
        var incoming = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(file => (File: file, Target: Path.Combine(Root, Path.GetRelativePath(source, file))))
            .ToList();
        var shipped = new HashSet<string>(incoming.Select(m => m.Target), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var stale = InstalledFiles().Where(f => !shipped.Contains(f)).ToList();
        if (stale.Count > MaxStaleFiles)
        {
            // Not a folder of Raw75's own (e.g. a release copied into a shared folder): only replace, never sweep.
            Log.Warning($"Update: {stale.Count} files in {Root} are not in the new release; leaving them in place.");
            stale.Clear();
        }

        var done = new List<(string Target, string? Old)>();
        try
        {
            foreach (string file in stale)
                done.Add((file, RetireAside(file)));

            foreach ((string file, string target) in incoming)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? old = File.Exists(target) ? RetireAside(target) : null;
                done.Add((target, old));
                File.Move(file, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Rollback(done);
            throw new UpdateException($"The update could not be installed: {ex.Message}", ex);
        }
        DeleteStaging();
    }

    // Files of the current installation an update owns: everything except the staging folder, files already retired,
    // and logs written next to the app.
    private IEnumerable<string> InstalledFiles()
    {
        foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Root, file);
            if (rel.StartsWith(StagingFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.EndsWith(OldSuffix, StringComparison.Ordinal)
                || file.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return file;
        }
    }

    private static string RetireAside(string file)
    {
        string old = file + OldSuffix;
        if (File.Exists(old))
            File.Delete(old);
        File.Move(file, old);
        return old;
    }

    private static void Rollback(List<(string Target, string? Old)> done)
    {
        for (int i = done.Count - 1; i >= 0; i--)
        {
            (string target, string? old) = done[i];
            try
            {
                if (old is not null && File.Exists(old))
                    File.Move(old, target, overwrite: true);
                else if (old is null && File.Exists(target))
                    File.Delete(target);
            }
            catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Could not restore {target} after a failed update: {undo.Message}");
            }
        }
    }

    /// <summary>
    /// Removes what an earlier update left behind: retired files, folders they leave empty, and the staging folder.
    /// Best effort; returns true when nothing is left (a file still in use is retried on a later call).
    /// </summary>
    public bool CleanUp()
    {
        DeleteStaging();
        bool clean = true;
        try
        {
            foreach (string old in Directory.EnumerateFiles(Root, "*" + OldSuffix, SearchOption.AllDirectories).ToList())
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    clean = false; // still in use (the previous process is exiting)
                }
            }
            RemoveEmptyFolders(Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not clean up after an update: {ex.Message}");
            clean = false;
        }
        return clean;
    }

    private static void RemoveEmptyFolders(string folder)
    {
        foreach (string sub in Directory.EnumerateDirectories(folder).ToList())
        {
            RemoveEmptyFolders(sub);
            try
            {
                if (!Directory.EnumerateFileSystemEntries(sub).Any())
                    Directory.Delete(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Discards a downloaded update that was not installed.</summary>
    public void DeleteStaging()
    {
        try
        {
            if (Directory.Exists(Staging))
                Directory.Delete(Staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not remove {Staging}: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
