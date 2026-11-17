namespace GDMInputManager;

/// <summary>
/// Clase de punto de entrada de la aplicación
/// Estructura: Clase estática con método Main
/// Propósito: Punto de inicio de la aplicación Windows Forms
/// </summary>
static class Program
{
    /// <summary>
    /// Punto de entrada principal de la aplicación
    /// Estructura: Método estático que inicializa y ejecuta la aplicación
    /// Propósito: Configurar la aplicación y lanzar el formulario principal
    /// Proceso:
    ///   1. [STAThread] Requerido para aplicaciones Windows Forms (COM)
    ///   2. Inicializa configuración de aplicación (DPI, fuentes, etc.)
    ///   3. Crea y ejecuta el formulario Main
    /// </summary>
    [STAThread]
    static void Main()
    {
        // Inicializa configuración de aplicación (DPI, temas, etc.)
        ApplicationConfiguration.Initialize();
        // Ejecuta el formulario principal (Main)
        Application.Run(new Main());
    }
}