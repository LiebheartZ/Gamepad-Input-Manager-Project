using System;
using System.Windows.Forms;
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
///   - Detección de dispositivos en tiempo real (WM_DEVICECHANGE)
///   - Logging automático al iniciar
/// </summary>
public partial class Main : Form
{
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
            this.Icon = new Icon("Resources\\icono.ico");
            LoggerService.Instance.Success("Aplicación GDM Input Manager iniciada correctamente.");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"Error al inicializar la aplicación: {ex.Message}");
            LoggerService.Instance.Error($"StackTrace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Override del método WndProc para capturar mensajes de Windows
    /// Estructura: Intercepta mensajes de Windows antes de procesarlos normalmente
    /// Propósito: Detectar cuando se conectan o desconectan dispositivos en tiempo real
    /// Proceso:
    ///   1. Llama a base.WndProc para procesamiento normal
    ///   2. Verifica si el mensaje es WM_DEVICECHANGE
    ///   3. Si es DBT_DEVICEARRIVAL, llama a OnDeviceConnected
    ///   4. Si es DBT_DEVICEREMOVECOMPLETE, llama a OnDeviceDisconnected
    /// Parámetros:
    ///   - m: Mensaje de Windows (contiene código de mensaje y parámetros)
    /// Nota: WM_DEVICECHANGE es un mensaje de Windows que indica cambios en dispositivos
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == WM_DEVICECHANGE)
        {
            if (m.WParam.ToInt32() == DBT_DEVICEARRIVAL)
            {
                // Dispositivo conectado
                OnDeviceConnected();
            }
            else if (m.WParam.ToInt32() == DBT_DEVICEREMOVECOMPLETE)
            {
                // Dispositivo desconectado
                OnDeviceDisconnected();
            }
        }
    }

    /// <summary>
    /// Manejador cuando se detecta un dispositivo conectado
    /// Estructura: Método que se llama cuando Windows detecta un nuevo dispositivo
    /// Propósito: Notificar a MandosControl que un dispositivo se conectó
    /// Estado actual: Implementación parcial, solo registra log
    /// Pendiente: Conectar con MandosControl para refrescar la lista automáticamente
    /// </summary>
    private void OnDeviceConnected()
    {
        LoggerService.Instance.Info("Dispositivo conectado detectado por WM_DEVICECHANGE.");
        // Notificar a MandosControl que un dispositivo se conectó
        // MandosControl.RefreshGamepadList();
        // Por ahora, esto se implementará cuando conectemos el control
    }

    /// <summary>
    /// Manejador cuando se detecta un dispositivo desconectado
    /// Estructura: Método que se llama cuando Windows detecta desconexión de dispositivo
    /// Propósito: Notificar a MandosControl que un dispositivo se desconectó
    /// Estado actual: Implementación parcial, solo registra log
    /// Pendiente: Conectar con MandosControl para refrescar la lista automáticamente
    /// </summary>
    private void OnDeviceDisconnected()
    {
        LoggerService.Instance.Info("Dispositivo desconectado detectado por WM_DEVICECHANGE.");
        // Notificar a MandosControl que un dispositivo se desconectó
        // MandosControl.RefreshGamepadList();
        // Por ahora, esto se implementará cuando conectemos el control
    }
}
