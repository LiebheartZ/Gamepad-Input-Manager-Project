using GDMInputManager.Models;
using GDMInputManager.Services;

namespace GDMInputManager.Infrastructure;

/// <summary>
/// Implementación en memoria del repositorio de controladores
/// Para propósitos de prototipo/desarrollo
/// </summary>
public class InMemoryControllerRepository : IControllerRepository
{
    private readonly Dictionary<string, ControllerDevice> _controllers = new();

    public async Task<IList<ControllerDevice>> GetSavedControllersAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(_controllers.Values.ToList());
    }

    public async Task SaveOrUpdateAsync(ControllerDevice controller, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            _controllers[controller.StableId] = controller;
        }, cancellationToken);
    }

    public async Task RemoveAsync(string stableId, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            _controllers.Remove(stableId);
        }, cancellationToken);
    }

    public async Task<(string? profileId, string? profileName)> GetProfileAsync(string stableId, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        if (_controllers.TryGetValue(stableId, out var controller))
        {
            return (controller.ProfileId, controller.ProfileName);
        }
        return (null, null);
    }
}
