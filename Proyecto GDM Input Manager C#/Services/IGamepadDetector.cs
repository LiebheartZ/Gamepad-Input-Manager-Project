using GDMInputManager.Models;

namespace GDMInputManager.Services;

/// <summary>
/// Interfaz para detectores de mandos (Strategy Pattern)
/// Permite cambiar entre SDL2, SharpDX, RawInput, etc.
/// Estructura: Define un contrato que todas las implementaciones de detectores deben cumplir
/// Propósito: Abstrae la lógica de detección de mandos para permitir cambiar de método fácilmente
/// </summary>
public interface IGamepadDetector
{
    /// <summary>
    /// Nombre del detector (ej. "SDL2", "SharpDX", "RawInput")
    /// Propósito: Identificar qué método de detección se está usando
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Inicializa el detector
    /// Estructura: Método que prepara el detector para su uso (carga DLLs, configura API, etc.)
    /// Propósito: Realizar la configuración inicial necesaria antes de detectar mandos
    /// Retorna: true si la inicialización fue exitosa, false si falló
    /// </summary>
    bool Initialize();

    /// <summary>
    /// Obtiene la lista de mandos actualmente conectados
    /// Estructura: Escanea el sistema y retorna una lista de objetos Gamepad
    /// Propósito: Detectar todos los mandos físicamente conectados al sistema
    /// Retorna: Lista de mandos conectados (vacía si no hay ninguno)
    /// </summary>
    List<Gamepad> GetConnectedGamepads();

    /// <summary>
    /// Identifica un mando por su información de hardware
    /// Estructura: Toma VID/PID/MAC y crea un objeto Gamepad con tipo y modelo identificados
    /// Propósito: Clasificar un mando específico basándose en sus identificadores de hardware
    /// Parámetros:
    ///   - vid: Vendor ID (identificador del fabricante)
    ///   - pid: Product ID (identificador del producto)
    ///   - macAddress: Dirección MAC (opcional, para Bluetooth)
    /// Retorna: Objeto Gamepad con la información identificada, o null si no se puede identificar
    /// </summary>
    Gamepad? IdentifyGamepad(int vid, int pid, string? macAddress = null);

    /// <summary>
    /// Limpia recursos
    /// Estructura: Libera memoria y recursos nativos usados por el detector
    /// Propósito: Asegurar que no haya fugas de memoria al cerrar la aplicación
    /// </summary>
    void Dispose();
}
