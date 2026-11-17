using System.Diagnostics;
using System.Runtime.InteropServices;
using GDMInputManager.Interfaces;
using GDMInputManager.Models;
using GDMInputManager.Services;

namespace GDMInputManager.Controls;

public partial class MandosControl : UserControl
{
    private readonly IGamepadManager _manager;
    private readonly Dictionary<string, Image> _spriteCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _disabledSpriteCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GamepadCardView> _gamepadCards = new(StringComparer.Ordinal);
    private Control? _emptyStateCard;
    private IReadOnlyList<GamepadListItem>? _pendingItems;
    private int _renderScheduled;
    private bool _hasSuccessfulSnapshot;
    private volatile bool _isTabActive;
    private bool _isDisposed;

    public MandosControl(IGamepadManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        InitializeComponent();

        Load += MandosControl_Load;
        VisibleChanged += MandosControl_VisibleChanged;
        checkShowAllBluetooth.CheckedChanged += CheckShowAllBluetooth_CheckedChanged;
        checkAutoPair.CheckedChanged += CheckAutoPair_CheckedChanged;
        _manager.ListChanged += Manager_ListChanged;
        _manager.ErrorOccurred += Manager_ErrorOccurred;
        _manager.RefreshCompleted += Manager_RefreshCompleted;
        Disposed += MandosControl_Disposed;

        checkAutoPair.AccessibleDescription =
            "La opción está preparada; la automatización del emparejamiento se implementará después.";
        checkShowAllBluetooth.AccessibleDescription =
            "Muestra también los mandos Bluetooth emparejados o disponibles aunque no estén conectados.";
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
        VisibleChanged -= MandosControl_VisibleChanged;
        _gamepadCards.Clear();
        _emptyStateCard = null;
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
        if (IsDisposed)
        {
            return;
        }

        Interlocked.Exchange(ref _pendingItems, e.Items);
        if (_isTabActive && IsHandleCreated)
        {
            SchedulePendingRender();
        }
    }

    private void MandosControl_VisibleChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !_isTabActive)
        {
            return;
        }

        Interlocked.Exchange(ref _pendingItems, _manager.CurrentItems);
        SchedulePendingRender();
    }

    public void SetTabActive(bool isActive)
    {
        if (IsDisposed || _isTabActive == isActive)
        {
            return;
        }

        _isTabActive = isActive;
        _manager.SetMonitoringActive(isActive);
        if (isActive)
        {
            Interlocked.Exchange(ref _pendingItems, _manager.CurrentItems);
            SchedulePendingRender();
        }
    }

    private void SchedulePendingRender()
    {
        if (IsDisposed || !_isTabActive || !IsHandleCreated ||
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
            if (items == null || IsDisposed || Disposing || !_isTabActive)
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
            if (_isTabActive && Volatile.Read(ref _pendingItems) != null)
            {
                SchedulePendingRender();
            }
        }
    }

    private void Manager_ErrorOccurred(object? sender, GamepadErrorEventArgs e)
    {
        if (_isTabActive)
        {
            SetStatus($"{e.Source}: {e.Message}", isError: true);
        }
    }

    private void Manager_RefreshCompleted(object? sender, GamepadRefreshCompletedEventArgs e)
    {
        if (!_isTabActive)
        {
            return;
        }

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
        if (IsDisposed || !_isTabActive || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() =>
            {
                if (_isTabActive)
                {
                    SetStatus(text, isError);
                }
            }));
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
        LoggerService.Instance.Info($"Renderizando {items.Count} elemento(s) en la lista de mandos.");
        HashSet<string> itemKeys = items.Select(GetItemKey).ToHashSet(StringComparer.Ordinal);
        flowLayoutPanelGamepads.SuspendLayout();
        try
        {
            foreach (string staleKey in _gamepadCards.Keys
                         .Where(key => !itemKeys.Contains(key))
                         .ToArray())
            {
                GamepadCardView staleCard = _gamepadCards[staleKey];
                flowLayoutPanelGamepads.Controls.Remove(staleCard.Card);
                staleCard.Card.Dispose();
                _gamepadCards.Remove(staleKey);
            }

            if (items.Count == 0)
            {
                _emptyStateCard ??= CreateEmptyStateCard();
                ((Label)_emptyStateCard).Text = _hasSuccessfulSnapshot
                    ? "No se encontraron mandos."
                    : "La detección aún no se ha completado.";
                if (_emptyStateCard.Parent != flowLayoutPanelGamepads)
                {
                    flowLayoutPanelGamepads.Controls.Add(_emptyStateCard);
                }
            }
            else
            {
                if (_emptyStateCard != null)
                {
                    flowLayoutPanelGamepads.Controls.Remove(_emptyStateCard);
                    _emptyStateCard.Dispose();
                    _emptyStateCard = null;
                }

                for (int index = 0; index < items.Count; index++)
                {
                    GamepadListItem item = items[index];
                    string key = GetItemKey(item);
                    if (!_gamepadCards.TryGetValue(key, out GamepadCardView? card))
                    {
                        card = CreateGamepadCard(item);
                        _gamepadCards.Add(key, card);
                    }
                    else
                    {
                        UpdateGamepadCard(card, item);
                    }

                    if (card.Card.Parent != flowLayoutPanelGamepads)
                    {
                        flowLayoutPanelGamepads.Controls.Add(card.Card);
                    }

                    flowLayoutPanelGamepads.Controls.SetChildIndex(card.Card, index);
                }
            }
        }
        finally
        {
            flowLayoutPanelGamepads.ResumeLayout(true);
            flowLayoutPanelGamepads.AutoScrollPosition = new Point(scrollX, scrollY);
        }
    }

    private GamepadCardView CreateGamepadCard(GamepadListItem item)
    {
        var card = new Panel
        {
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
        };
        var connectionIcon = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(3, 6, 3, 6)
        };
        var details = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Font = SystemFonts.MessageBoxFont
        };
        var profileLabel = CreateCardLabel(string.Empty);
        var batteryLabel = CreateCardLabel("Batería\nN/D");
        var unpairButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Desemparejar",
            Margin = new Padding(3)
        };
        var saveOrDeleteButton = new Button
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(3)
        };
        var renameButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Renombrar",
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

        var view = new GamepadCardView(
            card,
            gamepadLogo,
            connectionIcon,
            details,
            profileLabel,
            unpairButton,
            saveOrDeleteButton,
            renameButton,
            item);
        settingsButton.AccessibleDescription = "La configuración y el remapeo estarán disponibles más adelante.";
        unpairButton.Click += async (_, _) => await UnpairBluetoothDeviceAsync(view.Item);
        saveOrDeleteButton.Click += async (_, _) =>
        {
            GamepadListItem currentItem = view.Item;
            if (currentItem.RecordId is Guid recordId)
            {
                await RemoveGamepadRecordAsync(currentItem, recordId);
            }
            else if (!string.IsNullOrWhiteSpace(currentItem.DeviceKey))
            {
                await SaveDetectedDeviceAsync(currentItem.DeviceKey);
            }
        };
        renameButton.Click += async (_, _) =>
        {
            GamepadListItem currentItem = view.Item;
            if (currentItem.RecordId is Guid recordId)
            {
                string? newName = ShowRenameDialog(currentItem.DisplayName);
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
        UpdateGamepadCard(view, item);
        return view;
    }

    private void UpdateGamepadCard(GamepadCardView card, GamepadListItem item)
    {
        string displayName = string.IsNullOrWhiteSpace(item.DisplayName)
            ? "Mando detectado sin nombre"
            : item.DisplayName;
        string status = item.ConnectionStatus switch
        {
            GamepadConnectionState.Connected => $"Conectado · {GetConnectionName(item.ConnectionType)}",
            GamepadConnectionState.Disconnected when item.IsPaired => "Emparejado · Desconectado",
            GamepadConnectionState.Disconnected => "Desconectado",
            _ => "Disponible; conexión desconocida"
        };
        string profile = string.IsNullOrWhiteSpace(item.ActiveProfileId)
            ? "Sin perfil"
            : item.ActiveProfileId;
        bool isSaved = item.RecordId.HasValue;

        card.Item = item;
        card.Card.BackColor = item.IsConnected
            ? Color.FromArgb(239, 247, 255)
            : Color.FromArgb(224, 224, 224);
        card.Card.AccessibleName = displayName;
        card.GamepadLogo.Image = TryGetGamepadSprite(item.GamepadType, item.IsConnected);
        card.ConnectionIcon.Image = item.IsConnected ? GetConnectionSprite(item.ConnectionType) : null;
        card.ConnectionIcon.AccessibleName = status;
        card.Details.Text = $"{displayName}\n{status} · MAC: {FormatAddress(item.BluetoothAddress)}";
        card.Details.ForeColor = item.IsConnected
            ? SystemColors.ControlText
            : Color.FromArgb(95, 95, 95);
        card.ProfileLabel.Text = $"Perfil\n{profile}";
        card.UnpairButton.Enabled = item.CanUnpair && !string.IsNullOrWhiteSpace(item.BluetoothAddress);
        card.SaveOrDeleteButton.Text = isSaved ? "Borrar" : "Guardar";
        card.SaveOrDeleteButton.Enabled = isSaved || !string.IsNullOrWhiteSpace(item.DeviceKey);
        card.RenameButton.Enabled = item.RecordId.HasValue;

        card.SaveOrDeleteButton.AccessibleDescription = isSaved
            ? "Quita el registro de la aplicación sin desconectar ni desemparejar el mando."
            : "Guarda este dispositivo en la lista de mandos.";
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

    private Label CreateEmptyStateCard()
    {
        return new Label
        {
            AutoSize = false,
            Height = 58,
            Width = GetCardWidth(),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(90, 90, 90),
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 8)
        };
    }

    private static string GetItemKey(GamepadListItem item)
    {
        if (item.RecordId is Guid recordId)
        {
            return $"record:{recordId:D}";
        }

        if (!string.IsNullOrWhiteSpace(item.DeviceKey))
        {
            return $"device:{item.DeviceKey}";
        }

        throw new InvalidOperationException("La tarjeta del mando no tiene una identidad estable.");
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

    private sealed class GamepadCardView(
        Panel card,
        PictureBox gamepadLogo,
        PictureBox connectionIcon,
        Label details,
        Label profileLabel,
        Button unpairButton,
        Button saveOrDeleteButton,
        Button renameButton,
        GamepadListItem item)
    {
        public Panel Card { get; } = card;
        public PictureBox GamepadLogo { get; } = gamepadLogo;
        public PictureBox ConnectionIcon { get; } = connectionIcon;
        public Label Details { get; } = details;
        public Label ProfileLabel { get; } = profileLabel;
        public Button UnpairButton { get; } = unpairButton;
        public Button SaveOrDeleteButton { get; } = saveOrDeleteButton;
        public Button RenameButton { get; } = renameButton;
        public GamepadListItem Item { get; set; } = item;
    }
}
