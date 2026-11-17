namespace GDM.Services.Abstractions;

public sealed record AppReleaseAsset(
    string Name,
    string DownloadUrl,
    long Size,
    string? ApiUrl = null);

public sealed record AppReleaseInfo(
    string TagName,
    string Version,
    string Title,
    string? Notes,
    AppReleaseAsset? Package,
    DateTimeOffset PublishedAt,
    bool IsPrerelease);

public interface IAppUpdateService
{
    string RepositoryDisplay { get; }

    Task<AppReleaseInfo?> GetLatestReleaseAsync(
        string channel,
        CancellationToken cancellationToken = default);

    Task<string> DownloadPackageAsync(
        AppReleaseAsset asset,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default);
}
