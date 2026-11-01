using System.Windows.Forms;
using GDMInputManager.Interfaces;
using GDMInputManager.Services;

namespace GDMInputManager;

/// <summary>
/// Formulario principal de la aplicación (Main Form)
/// Estructura: Formulario Windows Forms con TabControl y panel de header
/// Propósito: Ventana principal que contiene todas las pestañas de la aplicación
/// Componentes:
///   - Panel Header: Panel superior con icono y título
///   - TabControl: Contiene 6 pestañas (Mandos, Tester, Perfiles, HidHide, Opciones, Registro)
///   - UserControls: Cada pestaña tiene su propio UserControl independiente
/// Características:
///   - Icono personalizado en la barra de tareas
///   - Logging automático al iniciar
/// </summary>
public partial class Main : Form
{
    private readonly IGamepadManager _gamepadManager = GamepadCompositionRoot.CreateDefault();

    /// <summary>
    /// Constructor del formulario principal
    /// Estructura: Inicializa componentes, configura icono y registra log inicial
    /// Propósito: Preparar la ventana principal para su uso
    /// Proceso:
    ///   1. Llama a InitializeComponent() (generado por el diseñador)
    ///   2. Establece el icono del formulario
    ///   3. Registra un log de éxito en el sistema de logging
    ///   4. Maneja cualquier excepción durante la inicialización
    /// </summary>
    public Main()
    {
        try
        {
            InitializeComponent();
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "icono.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }

            LoggerService.Instance.Success("Aplicación GDM Input Manager iniciada correctamente.");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"Error al inicializar la aplicación: {ex.Message}");
            LoggerService.Instance.Error($"StackTrace: {ex.StackTrace}");
        }
    }

}
