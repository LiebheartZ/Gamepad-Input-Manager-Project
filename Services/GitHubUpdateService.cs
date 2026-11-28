using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using GDM.Services.Abstractions;

namespace GDM.Services;

public sealed class GitHubUpdateService : IAppUpdateService
{
    private const string RepositorySlug = "LiebheartZ/Gamepad-Input-Manager-Project";

    private static readonly HttpClient SharedClient = CreateClient();
    private readonly string _updatesDirectory;
    private readonly Func<string?>? _accessTokenProvider;

    public GitHubUpdateService(
        string? updatesDirectory = null,
        Func<string?>? accessTokenProvider = null)
    {
        _updatesDirectory = updatesDirectory ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GDM",
                "Updates");
        _accessTokenProvider = accessTokenProvider;
    }

    public string RepositoryDisplay => $"github.com/{RepositorySlug}";

    public async Task<AppReleaseInfo?> GetLatestReleaseAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        string endpoint = channel == "nightly"
            ? $"https://api.github.com/repos/{RepositorySlug}/releases?per_page=1"
            : $"https://api.github.com/repos/{RepositorySlug}/releases/latest";
        using HttpRequestMessage request = CreateRequest(endpoint);
        using HttpResponseMessage response = await SharedClient.SendAsync(
            request,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        JsonElement root = document.RootElement;
        if (channel == "nightly")
        {
            if (root.ValueKind != JsonValueKind.Array ||
                !root.EnumerateArray().Any())
            {
                return null;
            }

            root = root.EnumerateArray().First();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ParseRelease(root);
    }

    private static AppReleaseInfo? ParseRelease(JsonElement root)
    {
        string tagName = GetString(root, "tag_name") ?? string.Empty;
        string version = NormalizeVersion(tagName);
        if (version.Length == 0)
        {
            return null;
        }

        var assets = new List<AppReleaseAsset>();
        if (root.TryGetProperty("assets", out JsonElement assetsElement) &&
            assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetsElement.EnumerateArray())
            {
                string? name = GetString(asset, "name");
                string? url = GetString(asset, "browser_download_url");
                if (name is null || url is null)
                {
                    continue;
                }

                assets.Add(new AppReleaseAsset(
                    name,
                    url,
                    asset.TryGetProperty("size", out JsonElement size) &&
                    size.TryGetInt64(out long parsedSize)
                        ? parsedSize
                        : 0,
                    GetString(asset, "url")));
            }
        }

        AppReleaseAsset? package = SelectPackage(assets);
        return new AppReleaseInfo(
            tagName,
            version,
            GetString(root, "name") ?? tagName,
            GetString(root, "body"),
            package,
            root.TryGetProperty("published_at", out JsonElement published) &&
            DateTimeOffset.TryParse(published.GetString(), out DateTimeOffset publishedAt)
                ? publishedAt
                : DateTimeOffset.MinValue,
            root.TryGetProperty("prerelease", out JsonElement prerelease) &&
            prerelease.ValueKind == JsonValueKind.True);
    }

    public async Task<string> DownloadPackageAsync(
        AppReleaseAsset asset,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_updatesDirectory);
        string safeVersion = string.Concat(
            asset.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        string zipPath = Path.Combine(_updatesDirectory, safeVersion);
        string stagingDirectory = Path.Combine(
            _updatesDirectory,
            $"staging-{safeVersion}-{Guid.NewGuid():N}");

        string downloadUrl = asset.ApiUrl ?? asset.DownloadUrl;
        using HttpRequestMessage request = CreateRequest(downloadUrl);
        if (asset.ApiUrl is not null)
        {
            request.Headers.Accept.ParseAdd("application/octet-stream");
        }

        using HttpResponseMessage response = await SharedClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        {
            await using FileStream destination = new(
                zipPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                if (asset.Size > 0)
                {
                    progress?.Report((int)Math.Clamp(totalRead * 100 / asset.Size, 0, 100));
                }
            }
        }

        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        ZipFile.ExtractToDirectory(zipPath, stagingDirectory);
        return ResolveAppDirectory(stagingDirectory);
    }

    private static string ResolveAppDirectory(string stagingDirectory)
    {
        string[] directories = Directory.GetDirectories(stagingDirectory);
        string[] files = Directory.GetFiles(stagingDirectory);
        if (directories.Length == 1 && files.Length == 0)
        {
            return directories[0];
        }

        return stagingDirectory;
    }

    private static AppReleaseAsset? SelectPackage(IReadOnlyList<AppReleaseAsset> assets)
    {
        AppReleaseAsset? fallback = null;
        foreach (AppReleaseAsset asset in assets)
        {
            if (!asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            fallback ??= asset;
            if (asset.Name.Contains("gdm", StringComparison.OrdinalIgnoreCase))
            {
                return asset;
            }
        }

        return fallback;
    }

    private static string NormalizeVersion(string tagName)
    {
        string trimmed = tagName.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out _) ? trimmed : string.Empty;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        string? token = _accessTokenProvider?.Invoke()?.Trim();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"GDM-UpdateChecker/{AppBuildInfo.Display}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
