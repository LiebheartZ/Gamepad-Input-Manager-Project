namespace GDM.Services.Abstractions;

public sealed record HidHideStatus(bool IsAvailable, bool IsEnabled, bool IsInverseWhitelist);

public interface IHidHideService
{
    Task<HidHideStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task HideUntilGdmClosesAsync(string devicePath, CancellationToken cancellationToken = default);
    Task ShowDeviceAsync(string devicePath, CancellationToken cancellationToken = default);
    Task RecoverPendingChangesAsync(CancellationToken cancellationToken = default);
    Task RestoreManagedChangesAsync(CancellationToken cancellationToken = default);
    Task RestoreManagedChangesAsync(
        IReadOnlyDictionary<string, bool> hiddenDeviceExitPolicies,
        bool keepUnlistedDevicesHidden = false,
        CancellationToken cancellationToken = default);
    bool IsHiddenByGdm(string devicePath);
    bool IsPhysicalDevicePresent(string devicePath);
}
