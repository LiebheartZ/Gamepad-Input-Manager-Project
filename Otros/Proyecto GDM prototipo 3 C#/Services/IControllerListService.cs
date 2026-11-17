using GDMInputManager.Models;

namespace GDMInputManager.Services;

/// <summary>
/// Servicio que coordina múltiples fuentes de dispositivos y gestiona la lista combinada
/// </summary>
public interface IControllerListService
{
    /// <summary>
    /// Refresca la lista completa de dispositivos combinando todas las fuentes
    /// </summary>
    Task<IList<ControllerDevice>> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inicia una búsqueda de dispositivos disponibles
    /// </summary>
    Task<IList<ControllerDevice>> SearchAvailableAsync(TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fusiona y ordena resultados de múltiples fuentes
    /// </summary>
    IList<ControllerDevice> MergeAndSort(IList<ControllerDevice> sources);
}
