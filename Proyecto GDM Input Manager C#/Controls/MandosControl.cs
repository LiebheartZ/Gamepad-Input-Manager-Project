using System.Diagnostics;
using System.Runtime.InteropServices;
using GDMInputManager.Interfaces;
using GDMInputManager.Models;
using GDMInputManager.Services;

namespace GDMInputManager.Controls;

public partial class MandosControl : UserControl
{
    private readonly IGamepadManager _manager;
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<string, Image> _spriteCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _disabledSpriteCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GamepadListItem>? _pendingItems;
    private int _renderScheduled;
    private bool _hasSuccessfulSnapshot;
    private bool _isDisposed;

    public MandosControl(IGamepadManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        InitializeComponent();

        Load += MandosControl_Load;
        checkShowAllBluetooth.CheckedChanged += CheckShowAllBluetooth_CheckedChanged;
        checkAutoPair.CheckedChanged += CheckAutoPair_CheckedChanged;
        _manager.ListChanged += Manager_ListChanged;
        _manager.ErrorOccurred += Manager_ErrorOccurred;
        _manager.RefreshCompleted += Manager_RefreshCompleted;
        Disposed += MandosControl_Disposed;

        _toolTip.SetToolTip(
            checkAutoPair,
            "La opción está preparada; la automatización del emparejamiento se implementará después.");
        _toolTip.SetToolTip(
            checkShowAllBluetooth,
            "Muestra también los mandos Bluetooth emparejados o disponibles aunque no estén conectados.");
        labelBluetoothSearchStatus.Text = "Esperando la primera detección...";
    }

    private async void MandosControl_Load(object? sender, EventArgs e)
    {
        try
        {
            await _manager.InitializeAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo iniciar la lista de mandos: {ex.Message}", isError: true);
        }
    }

    private void MandosControl_Disposed(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _manager.ListChanged -= Manager_ListChanged;
        _manager.ErrorOccurred -= Manager_ErrorOccurred;
        _manager.RefreshCompleted -= Manager_RefreshCompleted;
        _manager.Dispose();
        _toolTip.Dispose();
        foreach (Image sprite in _spriteCache.Values)
        {
            sprite.Dispose();
        }

        _spriteCache.Clear();
        foreach (Image sprite in _disabledSpriteCache.Values)
        {
            sprite.Dispose();
        }

        _disabledSpriteCache.Clear();
    }

    private void Manager_ListChanged(object? sender, GamepadListChangedEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        Interlocked.Exchange(ref _pendingItems, e.Items);
        SchedulePendingRender();
    }

    private void SchedulePendingRender()
    {
        if (IsDisposed || !IsHandleCreated ||
            Interlocked.CompareExchange(ref _renderScheduled, 1, 0) != 0)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(RenderPendingItems));
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing)
        {
            Interlocked.Exchange(ref _renderScheduled, 0);
        }
    }

    private void RenderPendingItems()
    {
        try
        {
            IReadOnlyList<GamepadListItem>? items = Interlocked.Exchange(ref _pendingItems, null);
            if (items == null || IsDisposed)
            {
                return;
            }

            _hasSuccessfulSnapshot = true;
            RenderItems(items);
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"No se pudo actualizar la lista visual de mandos: {ex}");
            SetStatus($"No se pudo dibujar la lista de mandos: {ex.Message}", isError: true);
        }
        finally
        {
            Interlocked.Exchange(ref _renderScheduled, 0);
            if (Volatile.Read(ref _pendingItems) != null)
            {
                SchedulePendingRender();
            }
        }
    }

    private void Manager_ErrorOccurred(object? sender, GamepadErrorEventArgs e)
    {
        SetStatus($"{e.Source}: {e.Message}", isError: true);
    }

    private void Manager_RefreshCompleted(object? sender, GamepadRefreshCompletedEventArgs e)
    {
        if (!e.Succeeded)
        {
            SetStatus("No se pudo actualizar; se conserva la última lista válida.", isError: true);
            return;
        }

        _hasSuccessfulSnapshot = true;
        if (e.Warnings.Count > 0)
        {
            SetStatus($"Lista actualizada con avisos: {string.Join(" · ", e.Warnings)}", isError: true);
        }
        else
        {
            SetStatus(string.Empty, isError: false);
        }
    }

    private void SetStatus(string text, bool isError)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(text, isError)));
            return;
        }

        labelBluetoothSearchStatus.Text = text;
        labelBluetoothSearchStatus.ForeColor = isError
            ? Color.FromArgb(180, 45, 35)
            : SystemColors.ControlText;
    }

    private void RenderItems(IReadOnlyList<GamepadListItem> items)
    {
        int scrollX = Math.Abs(flowLayoutPanelGamepads.AutoScrollPosition.X);
        int scrollY = Math.Abs(flowLayoutPanelGamepads.AutoScrollPosition.Y);
        flowLayoutPanelGamepads.SuspendLayout();
        try
        {
            foreach (Control control in flowLayoutPanelGamepads.Controls
                         .Cast<Control>()
                         .ToArray())
            {
                control.Dispose();
            }

            flowLayoutPanelGamepads.Controls.Clear();
            if (items.Count == 0)
            {
                AddEmptyStateCard(_hasSuccessfulSnapshot
                    ? "No se encontraron mandos."
                    : "La detección aún no se ha completado.");
            }
            else
            {
                foreach (GamepadListItem item in items)
                {
                    AddGamepadCard(item);
                }
            }
        }
        finally
        {
            flowLayoutPanelGamepads.ResumeLayout(true);
            flowLayoutPanelGamepads.AutoScrollPosition = new Point(scrollX, scrollY);
        }
    }

    private void AddGamepadCard(GamepadListItem item)
    {
        string status = item.ConnectionStatus switch
        {
            GamepadConnectionState.Connected => $"Conectado · {GetConnectionName(item.ConnectionType)}",
            GamepadConnectionState.Disconnected when item.IsPaired => "Emparejado · Desconectado",
            GamepadConnectionState.Disconnected => "Desconectado",
            _ => "Disponible; conexión desconocida"
        };
        string macAddress = FormatAddress(item.BluetoothAddress);
        string profile = string.IsNullOrWhiteSpace(item.ActiveProfileId)
            ? "Sin perfil"
            : item.ActiveProfileId;

        var card = new Panel
        {
            BackColor = item.IsConnected ? Color.FromArgb(239, 247, 255) : Color.FromArgb(224, 224, 224),
            BorderStyle = BorderStyle.FixedSingle,
            Height = 82,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(8),
            Width = GetCardWidth()
        };
        var layout = new TableLayoutPanel
        {
            ColumnCount = 9,
            RowCount = 1,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));

        var gamepadLogo = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(2),
            Image = TryGetGamepadSprite(item.GamepadType, item.IsConnected)
        };
        var connectionIcon = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(3, 6, 3, 6),
            Image = item.IsConnected ? GetConnectionSprite(item.ConnectionType) : null,
            AccessibleName = status
        };
        var details = new Label
        {
            Dock = DockStyle.Fill,
            Text = $"{item.DisplayName}\n{status} · MAC: {macAddress}",
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Font = SystemFonts.MessageBoxFont,
            ForeColor = item.IsConnected ? SystemColors.ControlText : Color.FromArgb(95, 95, 95)
        };
        var profileLabel = CreateCardLabel($"Perfil\n{profile}");
        var batteryLabel = CreateCardLabel("Batería\nN/D");
        var unpairButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Desemparejar",
            Enabled = item.CanUnpair && !string.IsNullOrWhiteSpace(item.BluetoothAddress),
            Margin = new Padding(3)
        };
        bool isSaved = item.RecordId.HasValue;
        var saveOrDeleteButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = isSaved ? "Borrar" : "Guardar",
            Enabled = isSaved || !string.IsNullOrWhiteSpace(item.DeviceKey),
            Margin = new Padding(3)
        };
        var renameButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Renombrar",
            Enabled = item.RecordId.HasValue,
            Margin = new Padding(3)
        };
        var settingsButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "\u2699",
            Enabled = false,
            Margin = new Padding(3),
            Font = SystemFonts.MessageBoxFont,
            AccessibleName = "Configuración del mando"
        };

        _toolTip.SetToolTip(settingsButton, "La configuración y el remapeo estarán disponibles más adelante.");
        _toolTip.SetToolTip(saveOrDeleteButton, isSaved
            ? "Quita el registro de la aplicación sin desconectar ni desemparejar el mando."
            : "Guarda este dispositivo en la lista de mandos.");
        unpairButton.Click += async (_, _) => await UnpairBluetoothDeviceAsync(item);
        saveOrDeleteButton.Click += async (_, _) =>
        {
            if (item.RecordId is Guid recordId)
            {
                await RemoveGamepadRecordAsync(item, recordId);
            }
            else if (!string.IsNullOrWhiteSpace(item.DeviceKey))
            {
                await SaveDetectedDeviceAsync(item.DeviceKey);
            }
        };
        renameButton.Click += async (_, _) =>
        {
            if (item.RecordId is Guid recordId)
            {
                string? newName = ShowRenameDialog(item.DisplayName);
                if (newName != null)
                {
                    await RenameGamepadRecordAsync(recordId, newName);
                }
            }
        };

        layout.Controls.Add(gamepadLogo, 0, 0);
        layout.Controls.Add(connectionIcon, 1, 0);
        layout.Controls.Add(details, 2, 0);
        layout.Controls.Add(profileLabel, 3, 0);
        layout.Controls.Add(batteryLabel, 4, 0);
        layout.Controls.Add(unpairButton, 5, 0);
        layout.Controls.Add(saveOrDeleteButton, 6, 0);
        layout.Controls.Add(renameButton, 7, 0);
        layout.Controls.Add(settingsButton, 8, 0);
        card.Controls.Add(layout);
        flowLayoutPanelGamepads.Controls.Add(card);
    }

    private async Task SaveDetectedDeviceAsync(string deviceKey)
    {
        try
        {
            SetStatus("Guardando mando...", isError: false);
            await _manager.SaveDetectedDeviceAsync(deviceKey);
        }
        catch (Exception ex)
        {
            ShowActionError("No se pudo guardar el mando", ex);
        }
    }

    private async Task RemoveGamepadRecordAsync(GamepadListItem item, Guid recordId)
    {
        DialogResult result = MessageBox.Show(
            this,
            $"¿Quitar “{item.DisplayName}” de la lista de la aplicación?\n\nEsto no lo desconectará ni lo desemparejará de Windows.",
            "Borrar mando",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
        {
            return;
        }

        try
        {
            await _manager.RemoveRecordAsync(recordId);
        }
        catch (Exception ex)
        {
            ShowActionError("No se pudo borrar el mando", ex);
        }
    }

    private async Task RenameGamepadRecordAsync(Guid recordId, string newName)
    {
        try
        {
            await _manager.RenameAsync(recordId, newName);
        }
        catch (Exception ex)
        {
            ShowActionError("No se pudo renombrar el mando", ex);
        }
    }

    private async Task UnpairBluetoothDeviceAsync(GamepadListItem item)
    {
        if (string.IsNullOrWhiteSpace(item.BluetoothAddress))
        {
            return;
        }

        DialogResult result = MessageBox.Show(
            this,
            $"¿Quieres quitar de Windows el emparejamiento de “{item.DisplayName}”?\n\nEsto puede desconectarlo.",
            "Desemparejar mando",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
        {
            return;
        }

        try
        {
            await _manager.UnpairBluetoothAsync(item.BluetoothAddress);
        }
        catch (Exception ex)
        {
            ShowActionError("No se pudo desemparejar el mando", ex);
        }
    }

    private string? ShowRenameDialog(string currentName)
    {
        using var dialog = new Form
        {
            Text = "Renombrar mando",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(360, 112)
        };
        var nameBox = new TextBox
        {
            Location = new Point(12, 14),
            Size = new Size(336, 24),
            MaxLength = 120,
            Text = currentName
        };
        var acceptButton = new Button
        {
            Text = "Guardar",
            DialogResult = DialogResult.OK,
            Location = new Point(192, 64),
            Size = new Size(75, 28)
        };
        var cancelButton = new Button
        {
            Text = "Cancelar",
            DialogResult = DialogResult.Cancel,
            Location = new Point(273, 64),
            Size = new Size(75, 28)
        };
        dialog.Controls.Add(nameBox);
        dialog.Controls.Add(acceptButton);
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = acceptButton;
        dialog.CancelButton = cancelButton;
        return dialog.ShowDialog(this) == DialogResult.OK ? nameBox.Text : null;
    }

    private void ShowActionError(string title, Exception exception)
    {
        LoggerService.Instance.Error($"{title}: {exception}");
        MessageBox.Show(this, exception.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private Image GetSprite(string spriteName)
    {
        if (_spriteCache.TryGetValue(spriteName, out Image? sprite))
        {
            return sprite;
        }

        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "GamepadSprites", spriteName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No se encontró el sprite requerido '{spriteName}'.", path);
        }

        using Image loaded = Image.FromFile(path);
        sprite = new Bitmap(loaded);
        _spriteCache.Add(spriteName, sprite);
        return sprite;
    }

    private Image? TryGetGamepadSprite(GamepadType type, bool isConnected)
    {
        string spriteName = GetGamepadSpriteName(type);
        try
        {
            return isConnected ? GetSprite(spriteName) : GetDisabledSprite(spriteName);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
        {
            LoggerService.Instance.Error($"No se pudo cargar el sprite '{spriteName}': {ex.Message}");
            return null;
        }
    }

    private Image GetDisabledSprite(string spriteName)
    {
        if (_disabledSpriteCache.TryGetValue(spriteName, out Image? disabledSprite))
        {
            return disabledSprite;
        }

        Image source = GetSprite(spriteName);
        var disabled = new Bitmap(source.Width, source.Height);
        using Graphics graphics = Graphics.FromImage(disabled);
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(
            new[]
            {
                new[] { 0.55f, 0, 0, 0, 0 },
                new[] { 0, 0.55f, 0, 0, 0 },
                new[] { 0, 0, 0.55f, 0, 0 },
                new[] { 0, 0, 0, 0.7f, 0 },
                new[] { 0, 0, 0, 0, 1f }
            }));
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, disabled.Width, disabled.Height),
            0,
            0,
            source.Width,
            source.Height,
            GraphicsUnit.Pixel,
            attributes);
        _disabledSpriteCache.Add(spriteName, disabled);
        return disabled;
    }

    private static string GetGamepadSpriteName(GamepadType type)
    {
        return type switch
        {
            GamepadType.PlayStationDualShock4 or GamepadType.PlayStationDualSense => "controller_playstation.png",
            GamepadType.XboxOne or GamepadType.XboxSeries or GamepadType.Xbox360 or GamepadType.GenericXInput =>
                "controller_xbox.png",
            _ => "controller_generic.png"
        };
    }

    private Image? GetConnectionSprite(ConnectionType type)
    {
        return type switch
        {
            ConnectionType.USB => TryGetSprite("transport_usb.png"),
            ConnectionType.Bluetooth => TryGetSprite("transport_bluetooth.png"),
            _ => null
        };
    }

    private Image? TryGetSprite(string spriteName)
    {
        try
        {
            return GetSprite(spriteName);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
        {
            LoggerService.Instance.Error($"No se pudo cargar el sprite '{spriteName}': {ex.Message}");
            return null;
        }
    }

    private static Label CreateCardLabel(string text)
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            Text = text,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = true,
            Font = SystemFonts.MessageBoxFont,
            ForeColor = Color.FromArgb(70, 70, 70)
        };
    }

    private void AddEmptyStateCard(string text)
    {
        flowLayoutPanelGamepads.Controls.Add(new Label
        {
            AutoSize = false,
            Height = 58,
            Width = GetCardWidth(),
            Text = text,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(90, 90, 90),
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 8)
        });
    }

    private int GetCardWidth()
    {
        return Math.Max(300, flowLayoutPanelGamepads.ClientSize.Width - flowLayoutPanelGamepads.Padding.Horizontal - 12);
    }

    private static string GetConnectionName(ConnectionType type)
    {
        return type switch
        {
            ConnectionType.USB => "USB",
            ConnectionType.Bluetooth => "Bluetooth",
            ConnectionType.WirelessProprietary => "Inalámbrico",
            _ => "Conexión desconocida"
        };
    }

    private static string FormatAddress(string? address)
    {
        string normalized = string.Concat((address ?? string.Empty).Where(Uri.IsHexDigit)).ToUpperInvariant();
        return normalized.Length == 12
            ? string.Join(":", Enumerable.Range(0, 6).Select(index => normalized.Substring(index * 2, 2)))
            : "No disponible";
    }

    private void buttonGameControllers_Click(object? sender, EventArgs e)
    {
        StartWindowsTool("joy.cpl");
    }

    private async void buttonRefreshGamepads_Click(object? sender, EventArgs e)
    {
        labelBluetoothSearchStatus.Text = "Actualizando mandos...";
        LoggerService.Instance.Info("Se solicitó una actualización manual de mandos desde la pestaña Mandos.");
        await RefreshFromUserActionAsync(checkShowAllBluetooth.Checked);
    }

    private async void CheckShowAllBluetooth_CheckedChanged(object? sender, EventArgs e)
    {
        await RefreshFromUserActionAsync(checkShowAllBluetooth.Checked);
    }

    private void CheckAutoPair_CheckedChanged(object? sender, EventArgs e)
    {
        labelBluetoothSearchStatus.Text = checkAutoPair.Checked
            ? "El emparejamiento automático estará disponible más adelante."
            : string.Empty;
    }

    private async Task RefreshFromUserActionAsync(bool includeAvailableBluetooth)
    {
        try
        {
            await _manager.RefreshAsync(includeAvailableBluetooth);
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo actualizar la lista: {ex.Message}", isError: true);
        }
    }

    private async void buttonSearchBluetooth_Click(object? sender, EventArgs e)
    {
        try
        {
            Rectangle bounds = buttonSearchBluetooth.RectangleToScreen(buttonSearchBluetooth.ClientRectangle);
            labelBluetoothSearchStatus.Text = "Abriendo dispositivos Bluetooth...";
            await _manager.ShowBluetoothPickerAsync(Handle, bounds);
        }
        catch (Exception ex)
        {
            ShowActionError("No se pudo abrir el selector Bluetooth", ex);
        }
    }

    private void StartWindowsTool(string fileName)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowActionError($"No se pudo abrir '{fileName}'", ex);
        }
    }

    private void flowLayoutPanelGamepads_SizeChanged(object? sender, EventArgs e)
    {
        int cardWidth = GetCardWidth();
        foreach (Control control in flowLayoutPanelGamepads.Controls)
        {
            control.Width = cardWidth;
        }
    }
}
