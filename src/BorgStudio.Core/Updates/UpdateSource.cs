using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BorgStudio.Core.Updates;

/// <summary>A file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A published release: version, notes and its files.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Where releases come from (GitHub; a fake in tests).</summary>
public interface IUpdateSource
{
    /// <summary>The newest published release; null if there is none.</summary>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads <paramref name="url"/> to <paramref name="path"/>, reporting the bytes written so far.</summary>
    Task DownloadAsync(string url, string path, IProgress<long>? progress, CancellationToken cancellationToken = default);

    /// <summary>A small text file of a release (the checksums).</summary>
    Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>Releases of BorgStudio on GitHub, read through the public REST API (no login).</summary>
public sealed class GitHubUpdateSource : IUpdateSource, IDisposable
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/cuban8IO/borgstudio/releases/latest";

    /// <summary>For trying an update against a local copy of the feed; same format as <see cref="LatestReleaseUrl"/>.</summary>
    public const string FeedVariable = "BORGSTUDIO_UPDATE_FEED";

    private readonly HttpClient _http;
    private readonly string _latestUrl;

    public GitHubUpdateSource(string userAgentVersion, HttpMessageHandler? handler = null, string? latestUrl = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BorgStudio", userAgentVersion.Replace(' ', '-')));
        var feed = Environment.GetEnvironmentVariable(FeedVariable);
        _latestUrl = latestUrl ?? (string.IsNullOrEmpty(feed) ? LatestReleaseUrl : feed);
    }

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, _latestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, timeout.Token);
        // No release published yet.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    public async Task DownloadAsync(string url, string path, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            progress?.Report(written);
        }
    }

    public async Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await _http.GetStringAsync(url, timeout.Token);
    }

    /// <summary>Reads GitHub's release JSON; null for a tag that is no version (e.g. "nightly").</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        var release = JsonSerializer.Deserialize<GitHubRelease>(json);
        if (release?.TagName is not { } tag || UpdateVersion.Parse(tag) is not { } version)
            return null;
        return new ReleaseInfo(version, tag, release.Body ?? "", release.HtmlUrl ?? "",
            [.. (release.Assets ?? []).Where(asset => asset.Name is not null && asset.Url is not null)
                .Select(asset => new ReleaseAsset(asset.Name!, asset.Url!, asset.Size))]);
    }

    public void Dispose() => _http.Dispose();

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("browser_download_url")] string? Url,
        [property: JsonPropertyName("size")] long Size);
}

/// <summary>Release versions: "v1.2.3" or "1.2.3"; anything else (e.g. "0.0.0-dev") is no release.</summary>
public static class UpdateVersion
{
    public static Version? Parse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        return trimmed.Count(c => c == '.') == 2 && Version.TryParse(trimmed, out var version) ? version : null;
    }
}
