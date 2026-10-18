using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Raw75.Update;

/// <summary>A release version <c>X.Y.Z</c>; build metadata (<c>+sha</c>) and anything after the patch number are ignored.</summary>
public readonly partial record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    /// <summary>This build's version (the project's <c>&lt;Version&gt;</c>), read from Raw75's own assembly.</summary>
    public static AppVersion Current { get; } = TryParse(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(), out var v) ? v : default;

    /// <summary>Parses the first <c>X.Y.Z</c> in <paramref name="text"/> (e.g. <c>v0.1.11</c>, <c>0.1.11+3f2a</c>).</summary>
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        Match m = text is null ? Match.Empty : Pattern().Match(text);
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int patch))
            return false;
        version = new AppVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        return c;
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    [GeneratedRegex(@"(?<![\d.])(\d{1,9})\.(\d{1,9})\.(\d{1,9})")]
    private static partial Regex Pattern();
}

/// <summary>A release archive: its download link, size and SHA-256 (hex, as GitHub reports it; null when it doesn't).</summary>
public sealed record UpdatePackage(string Name, string Url, long Size, string? Sha256);

/// <summary>A newer release: its version, page, release notes (GitHub markdown) and this platform's archive, if any.</summary>
public sealed record UpdateInfo(AppVersion Version, string PageUrl, string Notes, UpdatePackage? Package);

/// <summary>
/// Asks GitHub for the latest Raw75 release (tag <c>vX.Y.Z</c>, not a draft or pre-release). Only public release
/// metadata is read; nothing about the user is sent (just a User-Agent, as GitHub requires).
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const string Repository = "cretucosmin3/Raw75";
    public const string ReleasesUrl = $"https://github.com/{Repository}/releases";

    private const string LatestApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    private static readonly Regex ReleaseTag = new(@"^v\d{1,9}\.\d{1,9}\.\d{1,9}$", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;

    public UpdateChecker()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Raw75", AppVersion.Current.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>The release archive a self-contained build installs from (what CI publishes), or null for other platforms.</summary>
    public static string? PackageName(string runtimeIdentifier) => runtimeIdentifier switch
    {
        "linux-x64" => "Raw75-linux-x64.tar.gz",
        "win-x64" => "Raw75-win-x64.zip",
        "osx-arm64" => "Raw75-osx-arm64.tar.gz",
        "osx-x64" => "Raw75-osx-x64.tar.gz",
        _ => null
    };

    /// <summary>
    /// The latest release when it is newer than <paramref name="current"/>, else null. With <paramref name="package"/>
    /// the result carries that archive when the release has it. Throws <see cref="HttpRequestException"/>,
    /// <see cref="JsonException"/> or <see cref="OperationCanceledException"/> when GitHub can't be asked.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(AppVersion current, string? package, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await _http.GetAsync(LatestApi, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null; // nothing released yet
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Unexpected release format.");
        if (Flag(root, "draft") || Flag(root, "prerelease"))
            return null;
        if (Text(root, "tag_name") is not { } tag || !ReleaseTag.IsMatch(tag) || !AppVersion.TryParse(tag, out AppVersion version) || !(version > current))
            return null;

        // Links are built from the tag rather than taken from the response, so only this repository is ever downloaded.
        UpdatePackage? archive = null;
        if (package is not null && root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement a in assets.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object || Text(a, "name") != package || Text(a, "state") is not (null or "uploaded"))
                    continue;
                long size = a.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long n) ? n : 0;
                string? sha = Text(a, "digest") is { } d && d.StartsWith("sha256:", StringComparison.Ordinal) && d.Length == 71
                    ? d[7..].ToLowerInvariant() : null;
                archive = new UpdatePackage(package, $"{ReleasesUrl}/download/{tag}/{package}", size, sha);
            }
        }

        return new UpdateInfo(version, $"{ReleasesUrl}/tag/{tag}", Text(root, "body") ?? "", archive);
    }

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose() => _http.Dispose();
}
