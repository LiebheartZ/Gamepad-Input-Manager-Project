using GDMInputManager.Infrastructure;
using GDMInputManager.Models;
using GDMInputManager.Services;

namespace GDMInputManager.Controls;

public partial class MandosControl : UserControl
{
    private readonly IControllerListService _controllerListService;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public MandosControl()
    {
        InitializeComponent();

        // Inicializar servicios
        var discoveryService = new SdlControllerDiscoveryService();
        var repository = new InMemoryControllerRepository();
        _controllerListService = new ControllerListService(discoveryService, repository);

        // Suscribir eventos
        btnSearch.Click += BtnSearch_Click;
        chkShowAll.CheckedChanged += ChkShowAll_CheckedChanged;
        chkAutoPair.CheckedChanged += ChkAutoPair_CheckedChanged;

        // Cargar dispositivos iniciales
        _ = LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            lblSection.Text = "Cargando dispositivos...";
            var devices = await _controllerListService.RefreshAsync(_cancellationTokenSource.Token);
            DisplayDevices(devices);
            lblSection.Text = chkShowAll.Checked ? "Todos los dispositivos" : "Dispositivos conectados";
        }
        catch (Exception ex)
        {
            lblSection.Text = $"Error: {ex.Message}";
        }
    }

    private void DisplayDevices(IList<ControllerDevice> devices)
    {
        flowLayoutPanelDevices.Controls.Clear();

        foreach (var device in devices)
        {
            var card = CreateDeviceCard(device);
            flowLayoutPanelDevices.Controls.Add(card);
        }

        if (devices.Count == 0)
        {
            var emptyLabel = new Label
            {
                Text = "No se detectaron mandos. Conecta un mando o busca dispositivos.",
                AutoSize = true,
                Font = new System.Drawing.Font("Segoe UI", 10F),
                ForeColor = System.Drawing.Color.Gray,
                Location = new System.Drawing.Point(20, 20)
            };
            flowLayoutPanelDevices.Controls.Add(emptyLabel);
        }
    }

    private Panel CreateDeviceCard(ControllerDevice device)
    {
        var card = new Panel
        {
            Width = 300,
            Height = 180,
            BackColor = System.Drawing.Color.White,
            BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
            Margin = new System.Windows.Forms.Padding(10),
            Padding = new System.Windows.Forms.Padding(10)
        };

        var y = 10;

        // Nombre
        var lblName = new Label
        {
            Text = device.DisplayName,
            Font = new System.Drawing.Font("Segoe UI", 11F, System.Drawing.FontStyle.Bold),
            AutoSize = true,
            Location = new System.Drawing.Point(10, y)
        };
        card.Controls.Add(lblName);
        y += 25;

        // Estado
        var lblStatus = new Label
        {
            Text = $"Estado: {device.GetDisplayStatus()}",
            AutoSize = true,
            Location = new System.Drawing.Point(10, y),
            ForeColor = device.IsConnected ? System.Drawing.Color.Green : System.Drawing.Color.Gray
        };
        card.Controls.Add(lblStatus);
        y += 20;

        // Tipo de conexión
        var lblConnection = new Label
        {
            Text = $"Conexión: {device.ConnectionType}",
            AutoSize = true,
            Location = new System.Drawing.Point(10, y)
        };
        card.Controls.Add(lblConnection);
        y += 20;

        // Batería
        var lblBattery = new Label
        {
            Text = $"Batería: {device.GetBatteryDisplay()}",
            AutoSize = true,
            Location = new System.Drawing.Point(10, y)
        };
        card.Controls.Add(lblBattery);
        y += 20;

        // Perfil
        var lblProfile = new Label
        {
            Text = $"Perfil: {device.ProfileName ?? "Por defecto"}",
            AutoSize = true,
            Location = new System.Drawing.Point(10, y)
        };
        card.Controls.Add(lblProfile);
        y += 30;

        // Botón de acción
        var btnAction = new Button
        {
            Text = device.IsConnected ? "Desconectar" : "Conectar",
            Location = new System.Drawing.Point(10, y),
            Width = 120,
            Height = 30
        };
        card.Controls.Add(btnAction);

        // Botón de configuración
        var btnConfig = new Button
        {
            Text = "⚙",
            Location = new System.Drawing.Point(140, y),
            Width = 40,
            Height = 30
        };
        card.Controls.Add(btnConfig);

        return card;
    }

    private async void BtnSearch_Click(object? sender, EventArgs e)
    {
        try
        {
            btnSearch.Enabled = false;
            btnSearch.Text = "Buscando...";
            lblSection.Text = "Buscando dispositivos Bluetooth...";

            var devices = await _controllerListService.SearchAvailableAsync(
                TimeSpan.FromSeconds(10),
                _cancellationTokenSource.Token);

            DisplayDevices(devices);
            lblSection.Text = "Búsqueda completada";
        }
        catch (Exception ex)
        {
            lblSection.Text = $"Error en búsqueda: {ex.Message}";
        }
        finally
        {
            btnSearch.Enabled = true;
            btnSearch.Text = "Buscar dispositivos";
        }
    }

    private async void ChkShowAll_CheckedChanged(object? sender, EventArgs e)
    {
        await LoadDevicesAsync();
    }

    private void ChkAutoPair_CheckedChanged(object? sender, EventArgs e)
    {
        // Implementación futura: activar/desactivar emparejamiento automático
    }
}
