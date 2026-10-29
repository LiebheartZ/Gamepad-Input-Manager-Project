using System.Windows.Forms;
using System.Drawing;
using GDMInputManager.Services;

namespace GDMInputManager.Controls;

/// <summary>
/// Control de usuario para la pestaña "Registro"
/// Estructura: UserControl con RichTextBox como consola de logging
/// Propósito: Mostrar todos los logs del sistema en tiempo real con colores
/// Componentes:
///   - RichTextBox: Consola con fondo negro para mostrar mensajes
///   - Botón Limpiar: Borra todos los mensajes de la consola
///   - Botón Copiar: Copia el contenido de la consola al portapapeles
/// Colores:
///   - Rojo: Errores
///   - Amarillo: Advertencias
///   - Cian (azul claro): Éxitos
///   - Blanco: Información
/// </summary>
public partial class RegistroControl : UserControl
{
    /// <summary>
    /// Constructor del control
    /// Estructura: Inicializa componentes y suscribe al logger global
    /// Propósito: Preparar la consola para recibir mensajes de logging
    /// </summary>
    public RegistroControl()
    {
        InitializeComponent();
        InitializeLogger();
    }

    /// <summary>
    /// Inicializa la suscripción al sistema de logging global
    /// Estructura: Suscribe el evento OnLogMessage al método OnLogMessage
    /// Propósito: Recibir todos los logs del sistema y mostrarlos en la consola
    /// Proceso:
    ///   1. Suscribe al evento OnLogMessage de LoggerService
    ///   2. Registra un mensaje inicial indicando que la consola está activa
    /// Nota: LoggerService usa el patrón Singleton, accesible desde cualquier parte
    /// </summary>
    private void InitializeLogger()
    {
        // Suscribirse al evento de logging global
        LoggerService.Instance.OnLogMessage += OnLogMessage;
        LoggerService.Instance.Info("Sistema de logging iniciado. Consola de registro activa.");
    }

    /// <summary>
    /// Manejador de evento cuando se recibe un mensaje de log
    /// Estructura: Muestra el mensaje en el RichTextBox con el color apropiado
    /// Propósito: Renderizar mensajes de log con formato y colores según su nivel
    /// Proceso:
    ///   1. Verifica si necesita Invoke (cross-thread)
    ///   2. Agrega el mensaje al RichTextBox
    ///   3. Colorea el texto según el nivel (rojo=error, amarillo=warning, etc.)
    ///   4. Mantiene el scroll al final si estaba al final
    /// Parámetros:
    ///   - message: Texto del mensaje con timestamp
    ///   - level: Nivel de severidad del mensaje
    /// Nota: InvokeRequired maneja llamadas desde otros threads (thread-safe)
    /// </summary>
    private void OnLogMessage(string message, LogLevel level)
    {
        if (InvokeRequired)
        {
            Invoke(new Action<string, LogLevel>(OnLogMessage), message, level);
            return;
        }

        int originalSelectionStart = richTextBoxLog.SelectionStart;
        int originalSelectionLength = richTextBoxLog.SelectionLength;
        bool originalScrollToEnd = richTextBoxLog.SelectionStart == richTextBoxLog.TextLength;

        richTextBoxLog.AppendText(message + Environment.NewLine);

        // Colorear según el nivel
        int lastLineStart = richTextBoxLog.Text.LastIndexOf('\n', richTextBoxLog.Text.Length - message.Length - 1) + 1;
        if (lastLineStart < 0) lastLineStart = 0;

        richTextBoxLog.Select(lastLineStart, message.Length);

        Color color = level switch
        {
            LogLevel.Error => Color.Red,
            LogLevel.Warning => Color.Yellow,
            LogLevel.Success => Color.Cyan,
            LogLevel.Info => Color.White,
            _ => Color.White
        };

        richTextBoxLog.SelectionColor = color;
        richTextBoxLog.SelectionLength = 0;

        // Restaurar selección y scroll
        richTextBoxLog.SelectionStart = originalSelectionStart;
        richTextBoxLog.SelectionLength = originalSelectionLength;

        if (originalScrollToEnd)
        {
            richTextBoxLog.ScrollToCaret();
        }
    }

    /// <summary>
    /// Evento del botón Limpiar
    /// Propósito: Borra todo el contenido de la consola
    /// </summary>
    private void buttonClear_Click(object sender, EventArgs e)
    {
        richTextBoxLog.Clear();
        LoggerService.Instance.Info("Consola limpiada por el usuario.");
    }

    /// <summary>
    /// Evento del botón Copiar
    /// Propósito: Copia el contenido de la consola al portapapeles
    /// </summary>
    private void buttonCopy_Click(object sender, EventArgs e)
    {
        if (!string.IsNullOrEmpty(richTextBoxLog.Text))
        {
            Clipboard.SetText(richTextBoxLog.Text);
            LoggerService.Instance.Success("Registro copiado al portapapeles.");
        }
    }
}
