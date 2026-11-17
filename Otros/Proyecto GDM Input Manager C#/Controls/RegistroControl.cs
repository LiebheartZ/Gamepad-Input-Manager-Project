using System.Collections.Concurrent;
using System.Text;
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
    private const int MaximumPendingLogs = 4000;
    private const int MaximumDisplayedCharacters = 500_000;
    private const int MaximumLogsPerUiBatch = 250;
    private readonly ConcurrentQueue<(string Message, LogLevel Level)> _pendingLogs = new();
    private readonly object _pendingLogsLock = new();
    private readonly System.Windows.Forms.Timer _logFlushTimer;
    private int _pendingLogCount;
    private int _droppedLogCount;
    private bool _canScheduleFlush;
    private volatile bool _isDisposed;
    private bool _isTabActive;

    /// <summary>
    /// Constructor del control
    /// Estructura: Inicializa componentes; Main activa el logger al seleccionar la pestaña
    /// Propósito: Preparar la consola para recibir mensajes de logging
    /// </summary>
    public RegistroControl()
    {
        InitializeComponent();
        _logFlushTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _logFlushTimer.Tick += LogFlushTimer_Tick;
        Disposed += RegistroControl_Disposed;
    }

    /// <summary>
    /// Activa o desactiva la recepción de logs según la selección de la pestaña.
    /// </summary>
    public void SetTabActive(bool isActive)
    {
        if (_isDisposed || _isTabActive == isActive)
        {
            return;
        }

        _isTabActive = isActive;
        if (isActive)
        {
            lock (_pendingLogsLock)
            {
                _canScheduleFlush = true;
            }

            LoggerService.Instance.OnLogMessage += OnLogMessage;
            _logFlushTimer.Start();
            LoggerService.Instance.Info("Consola de registro activa.");
            return;
        }

        _logFlushTimer.Stop();
        LoggerService.Instance.OnLogMessage -= OnLogMessage;
        lock (_pendingLogsLock)
        {
            _canScheduleFlush = false;
            ClearPendingLogs();
        }
    }

    /// <summary>
    /// Encola el mensaje para que el temporizador de UI lo procese en un lote.
    /// </summary>
    private void OnLogMessage(string message, LogLevel level)
    {
        lock (_pendingLogsLock)
        {
            if (_isDisposed || !_canScheduleFlush)
            {
                return;
            }

            _pendingLogs.Enqueue((message, level));
            int pendingCount = ++_pendingLogCount;
            if (pendingCount > MaximumPendingLogs && _pendingLogs.TryDequeue(out _))
            {
                _pendingLogCount--;
                Interlocked.Increment(ref _droppedLogCount);
            }
        }
    }

    private void LogFlushTimer_Tick(object? sender, EventArgs e)
    {
        if (_isTabActive)
        {
            FlushPendingLogs();
        }
    }

    private void RegistroControl_Disposed(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _isTabActive = false;
        _logFlushTimer.Stop();
        _logFlushTimer.Tick -= LogFlushTimer_Tick;
        _logFlushTimer.Dispose();
        LoggerService.Instance.OnLogMessage -= OnLogMessage;
        lock (_pendingLogsLock)
        {
            _canScheduleFlush = false;
            ClearPendingLogs();
        }
    }

    private void ClearPendingLogs()
    {
        while (_pendingLogs.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingLogCount);
        }

        Interlocked.Exchange(ref _droppedLogCount, 0);
    }

    private void FlushPendingLogs()
    {
        if (_isDisposed || Disposing || IsDisposed || !_isTabActive)
        {
            return;
        }

        bool scrollToEnd = richTextBoxLog.SelectionStart == richTextBoxLog.TextLength;
        int originalSelectionStart = richTextBoxLog.SelectionStart;
        int originalSelectionLength = richTextBoxLog.SelectionLength;
        int droppedCount = Interlocked.Exchange(ref _droppedLogCount, 0);
        if (droppedCount > 0)
        {
            AppendLog(
                $"[Registro] Se descartaron {droppedCount} mensajes antiguos pendientes para limitar el uso de memoria.",
                LogLevel.Warning);
        }

        var batch = new List<(string Message, LogLevel Level)>(MaximumLogsPerUiBatch);
        lock (_pendingLogsLock)
        {
            while (batch.Count < MaximumLogsPerUiBatch &&
                   _pendingLogs.TryDequeue(out (string Message, LogLevel Level) entry))
            {
                _pendingLogCount--;
                batch.Add(entry);
            }
        }

        AppendLogBatch(batch);
        int trimmedCharacters = TrimDisplayedLogs();
        if (scrollToEnd)
        {
            richTextBoxLog.SelectionStart = richTextBoxLog.TextLength;
            richTextBoxLog.SelectionLength = 0;
            richTextBoxLog.ScrollToCaret();
        }
        else
        {
            int adjustedSelectionStart = Math.Max(0, originalSelectionStart - trimmedCharacters);
            richTextBoxLog.SelectionStart = Math.Min(adjustedSelectionStart, richTextBoxLog.TextLength);
            richTextBoxLog.SelectionLength = Math.Min(
                originalSelectionLength,
                richTextBoxLog.TextLength - richTextBoxLog.SelectionStart);
        }
    }

    private void AppendLogBatch(IReadOnlyList<(string Message, LogLevel Level)> batch)
    {
        int start = 0;
        while (start < batch.Count)
        {
            LogLevel level = batch[start].Level;
            var text = new StringBuilder();
            while (start < batch.Count && batch[start].Level == level)
            {
                text.AppendLine(batch[start].Message);
                start++;
            }

            richTextBoxLog.SelectionStart = richTextBoxLog.TextLength;
            richTextBoxLog.SelectionLength = 0;
            richTextBoxLog.SelectionColor = GetLogColor(level);
            richTextBoxLog.SelectedText = text.ToString();
        }
    }

    private void AppendLog(string message, LogLevel level)
    {
        richTextBoxLog.SelectionStart = richTextBoxLog.TextLength;
        richTextBoxLog.SelectionLength = 0;
        richTextBoxLog.SelectionColor = GetLogColor(level);
        richTextBoxLog.SelectedText = message + Environment.NewLine;
    }

    private static Color GetLogColor(LogLevel level)
    {
        return level switch
        {
            LogLevel.Error => Color.Red,
            LogLevel.Warning => Color.Yellow,
            LogLevel.Success => Color.Cyan,
            _ => Color.White
        };
    }

    private int TrimDisplayedLogs()
    {
        int excessCharacters = richTextBoxLog.TextLength - MaximumDisplayedCharacters;
        if (excessCharacters <= 0)
        {
            return 0;
        }

        string currentText = richTextBoxLog.Text;
        int lineBreak = currentText.IndexOf('\n', excessCharacters);
        int charactersToRemove = lineBreak >= 0 ? lineBreak + 1 : excessCharacters;
        richTextBoxLog.Select(0, charactersToRemove);
        richTextBoxLog.SelectedText = string.Empty;
        return charactersToRemove;
    }

    /// <summary>
    /// Evento del botón Limpiar
    /// Propósito: Borra todo el contenido de la consola
    /// </summary>
    private void buttonClear_Click(object sender, EventArgs e)
    {
        richTextBoxLog.Clear();
        lock (_pendingLogsLock)
        {
            ClearPendingLogs();
        }

        Interlocked.Exchange(ref _droppedLogCount, 0);
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
