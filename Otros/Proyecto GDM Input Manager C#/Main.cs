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
    private readonly WindowsBluetoothRadioStatusService _bluetoothRadioStatusService = new();
    private System.Windows.Forms.Timer? _bluetoothStatusTimer;
    private bool _isBluetoothStatusRefreshInProgress;

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
            tabControlMain.SelectedIndexChanged += TabControlMain_SelectedIndexChanged;
            UpdateActiveTab();
            pictureBoxBluetoothStatus.AccessibleDescription = "Consultando Bluetooth...";
            lblBluetoothStatus.AccessibleDescription = "Consultando Bluetooth...";
            pictureBoxBluetoothStatus.Paint += PictureBoxBluetoothStatus_Paint;
            Load += Main_Load;
            FormClosed += Main_FormClosed;
            _bluetoothStatusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            components?.Add(_bluetoothStatusTimer);
            _bluetoothStatusTimer.Tick += BluetoothStatusTimer_Tick;
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

    private async void Main_Load(object? sender, EventArgs e)
    {
        await RefreshBluetoothStatusAsync();
        _bluetoothStatusTimer?.Start();
    }

    private void TabControlMain_SelectedIndexChanged(object? sender, EventArgs e)
    {
        UpdateActiveTab();
    }

    private void UpdateActiveTab()
    {
        bool mandosActive = tabControlMain.SelectedTab == tabPageMandos;
        bool registroActive = tabControlMain.SelectedTab == tabPageRegistro;
        mandosControl.SetTabActive(mandosActive);
        registroControl.SetTabActive(registroActive);
        mandosControl.Visible = mandosActive;
        registroControl.Visible = registroActive;
    }

    private async void BluetoothStatusTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshBluetoothStatusAsync();
    }

    private async Task RefreshBluetoothStatusAsync()
    {
        if (_isBluetoothStatusRefreshInProgress || IsDisposed)
        {
            return;
        }

        _isBluetoothStatusRefreshInProgress = true;
        try
        {
            BluetoothRadioStatus status = await _bluetoothRadioStatusService.GetStatusAsync();
            if (IsDisposed)
            {
                return;
            }

            lblBluetoothStatus.Text = status.Availability switch
            {
                BluetoothRadioAvailability.On => "Bluetooth: Activo",
                BluetoothRadioAvailability.Off => "Bluetooth: Apagado",
                BluetoothRadioAvailability.Unavailable => "Bluetooth: No disponible",
                _ => "Bluetooth: Desconocido"
            };
            lblBluetoothStatus.AccessibleDescription = status.Detail;
            pictureBoxBluetoothStatus.AccessibleDescription = status.Detail;
            pictureBoxBluetoothStatus.Invalidate();
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"No se pudo actualizar el indicador Bluetooth: {ex}");
            lblBluetoothStatus.Text = "Bluetooth: Desconocido";
            lblBluetoothStatus.AccessibleDescription = ex.Message;
            pictureBoxBluetoothStatus.AccessibleDescription = ex.Message;
            pictureBoxBluetoothStatus.Invalidate();
        }
        finally
        {
            _isBluetoothStatusRefreshInProgress = false;
        }
    }

    private void PictureBoxBluetoothStatus_Paint(object? sender, PaintEventArgs e)
    {
        Color color = lblBluetoothStatus.Text switch
        {
            "Bluetooth: Activo" => Color.LightGreen,
            "Bluetooth: Apagado" => Color.Salmon,
            "Bluetooth: No disponible" => Color.DarkGray,
            _ => Color.Gold
        };
        using var pen = new Pen(color, 2.5F)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };

        float centerX = e.ClipRectangle.Left + e.ClipRectangle.Width / 2F;
        float top = e.ClipRectangle.Top + 4F;
        float bottom = e.ClipRectangle.Bottom - 4F;
        float middle = (top + bottom) / 2F;
        float right = centerX + 8F;
        float left = centerX - 8F;

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawLine(pen, centerX, top, centerX, bottom);
        e.Graphics.DrawLine(pen, centerX, top, right, middle - 1F);
        e.Graphics.DrawLine(pen, right, middle - 1F, centerX, middle);
        e.Graphics.DrawLine(pen, centerX, middle, right, middle + 1F);
        e.Graphics.DrawLine(pen, right, middle + 1F, centerX, bottom);
        e.Graphics.DrawLine(pen, left, middle - 7F, centerX, middle);
        e.Graphics.DrawLine(pen, centerX, middle, left, middle + 7F);
    }

    private void Main_FormClosed(object? sender, FormClosedEventArgs e)
    {
        _bluetoothStatusTimer?.Stop();
        _gamepadManager.Dispose();
    }
}
