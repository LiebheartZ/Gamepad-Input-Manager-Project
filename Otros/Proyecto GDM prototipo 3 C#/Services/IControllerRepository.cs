using GDMInputManager.Models;

namespace GDMInputManager.Services;

/// <summary>
/// Repositorio para persistir datos de controladores
/// </summary>
public interface IControllerRepository
{
    /// <summary>
    /// Obtiene todos los controladores guardados en la base de datos
    /// </summary>
    Task<IList<ControllerDevice>> GetSavedControllersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda o actualiza un controlador en la base de datos
    /// </summary>
    Task SaveOrUpdateAsync(ControllerDevice controller, CancellationToken cancellationToken = default);

    /// <summary>
    /// Elimina un controlador de la base de datos por su identificador
    /// </summary>
    Task RemoveAsync(string stableId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtiene el perfil asociado a un controlador
    /// </summary>
    Task<(string? profileId, string? profileName)> GetProfileAsync(string stableId, CancellationToken cancellationToken = default);
}
