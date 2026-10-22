namespace Proyecto_GDM_C_;

internal sealed class ControllerForm : Form
{
    private readonly ListView _controllersList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        MultiSelect = false
    };

    private readonly Label _statusLabel = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Text = "Buscando mandos..."
    };

    internal ControllerForm()
    {
        Text = "Administrador de mandos";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 360);
        ClientSize = new Size(700, 440);

        _controllersList.Columns.Add("Mando", 300);
        _controllersList.Columns.Add("Dispositivo", 100);
        _controllersList.Columns.Add("Ejes", 80);
        _controllersList.Columns.Add("Botones", 90);

        var titleLabel = new Label
        {
            AutoSize = true,
            Text = "Mandos disponibles",
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };

        var descriptionLabel = new Label
        {
            AutoSize = true,
            Text = "Conecta un mando y actualiza la lista para detectarlo.",
            ForeColor = SystemColors.GrayText
        };

        var refreshButton = new Button
        {
            AutoSize = true,
            Text = "Actualizar",
            Anchor = AnchorStyles.Right
        };
        refreshButton.Click += (_, _) => RefreshControllers();

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(16)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(titleLabel, 0, 0);
        header.Controls.Add(refreshButton, 1, 0);
        header.Controls.Add(descriptionLabel, 0, 1);
        header.SetColumnSpan(descriptionLabel, 2);

        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 8, 16, 12),
            Height = 42
        };
        footer.Controls.Add(_statusLabel);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_controllersList, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        Shown += (_, _) => RefreshControllers();
    }

    private void RefreshControllers()
    {
        var controllers = ControllerDetector.GetConnectedControllers();
        _controllersList.BeginUpdate();
        _controllersList.Items.Clear();

        foreach (var controller in controllers)
        {
            var item = new ListViewItem(controller.Name);
            item.SubItems.Add(controller.DeviceId.ToString());
            item.SubItems.Add(controller.AxisCount.ToString());
            item.SubItems.Add(controller.ButtonCount.ToString());
            _controllersList.Items.Add(item);
        }

        _controllersList.EndUpdate();
        _statusLabel.Text = controllers.Count == 0
            ? "No se encontraron mandos conectados."
            : $"Se encontraron {controllers.Count} mando(s).";
    }
}
