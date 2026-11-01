namespace GDMInputManager.Controls;

partial class MandosControl
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        checkAutoPair = new CheckBox();
        checkShowAllBluetooth = new CheckBox();
        buttonGameControllers = new Button();
        buttonSearchBluetooth = new Button();
        buttonRefreshGamepads = new Button();
        labelDevices = new Label();
        labelBluetoothSearchStatus = new Label();
        panelGamepadList = new Panel();
        flowLayoutPanelGamepads = new FlowLayoutPanel();
        panelGamepadList.SuspendLayout();
        SuspendLayout();
        //
        // checkAutoPair
        //
        checkAutoPair.AutoSize = true;
        checkAutoPair.Location = new Point(14, 14);
        checkAutoPair.Name = "checkAutoPair";
        checkAutoPair.Size = new Size(288, 19);
        checkAutoPair.TabIndex = 0;
        checkAutoPair.Text = "Emparejar mandos automáticamente al detectarlos";
        checkAutoPair.UseVisualStyleBackColor = true;
        //
        // checkShowAllBluetooth
        //
        checkShowAllBluetooth.AutoSize = true;
        checkShowAllBluetooth.Location = new Point(14, 42);
        checkShowAllBluetooth.Name = "checkShowAllBluetooth";
        checkShowAllBluetooth.Size = new Size(240, 19);
        checkShowAllBluetooth.TabIndex = 1;
        checkShowAllBluetooth.Text = "Mostrar dispositivos Bluetooth disponibles";
        checkShowAllBluetooth.UseVisualStyleBackColor = true;
        //
        // buttonGameControllers
        //
        buttonGameControllers.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        buttonGameControllers.Location = new Point(380, 14);
        buttonGameControllers.Name = "buttonGameControllers";
        buttonGameControllers.Size = new Size(170, 36);
        buttonGameControllers.TabIndex = 2;
        buttonGameControllers.Text = "Dispositivos de juego";
        buttonGameControllers.UseVisualStyleBackColor = true;
        buttonGameControllers.Click += buttonGameControllers_Click;
        //
        // buttonSearchBluetooth
        //
        buttonSearchBluetooth.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        buttonSearchBluetooth.Location = new Point(562, 14);
        buttonSearchBluetooth.Name = "buttonSearchBluetooth";
        buttonSearchBluetooth.Size = new Size(170, 36);
        buttonSearchBluetooth.TabIndex = 3;
        buttonSearchBluetooth.Text = "Buscar dispositivos";
        buttonSearchBluetooth.UseVisualStyleBackColor = true;
        buttonSearchBluetooth.Click += buttonSearchBluetooth_Click;
        //
        // labelDevices
        //
        labelDevices.AutoSize = true;
        labelDevices.Location = new Point(14, 82);
        labelDevices.Name = "labelDevices";
        labelDevices.Size = new Size(133, 15);
        labelDevices.TabIndex = 4;
        labelDevices.Text = "Lista de mandos";
        //
        // buttonRefreshGamepads
        //
        buttonRefreshGamepads.Location = new Point(156, 76);
        buttonRefreshGamepads.Name = "buttonRefreshGamepads";
        buttonRefreshGamepads.Size = new Size(92, 24);
        buttonRefreshGamepads.TabIndex = 5;
        buttonRefreshGamepads.Text = "Actualizar";
        buttonRefreshGamepads.UseVisualStyleBackColor = true;
        buttonRefreshGamepads.Click += buttonRefreshGamepads_Click;
        //
        // labelBluetoothSearchStatus
        //
        labelBluetoothSearchStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        labelBluetoothSearchStatus.Location = new Point(360, 78);
        labelBluetoothSearchStatus.Name = "labelBluetoothSearchStatus";
        labelBluetoothSearchStatus.Size = new Size(372, 22);
        labelBluetoothSearchStatus.TabIndex = 6;
        labelBluetoothSearchStatus.TextAlign = ContentAlignment.MiddleRight;
        //
        // panelGamepadList
        //
        panelGamepadList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        panelGamepadList.BackColor = Color.FromArgb(220, 235, 249);
        panelGamepadList.BorderStyle = BorderStyle.FixedSingle;
        panelGamepadList.Controls.Add(flowLayoutPanelGamepads);
        panelGamepadList.Location = new Point(10, 104);
        panelGamepadList.Name = "panelGamepadList";
        panelGamepadList.Padding = new Padding(7);
        panelGamepadList.Size = new Size(722, 326);
        panelGamepadList.TabIndex = 7;
        //
        // flowLayoutPanelGamepads
        //
        flowLayoutPanelGamepads.AutoScroll = true;
        flowLayoutPanelGamepads.Dock = DockStyle.Fill;
        flowLayoutPanelGamepads.FlowDirection = FlowDirection.TopDown;
        flowLayoutPanelGamepads.Location = new Point(7, 7);
        flowLayoutPanelGamepads.Name = "flowLayoutPanelGamepads";
        flowLayoutPanelGamepads.Padding = new Padding(4);
        flowLayoutPanelGamepads.Size = new Size(706, 310);
        flowLayoutPanelGamepads.TabIndex = 0;
        flowLayoutPanelGamepads.WrapContents = false;
        flowLayoutPanelGamepads.SizeChanged += flowLayoutPanelGamepads_SizeChanged;
        //
        // MandosControl
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        Controls.Add(panelGamepadList);
        Controls.Add(labelBluetoothSearchStatus);
        Controls.Add(buttonRefreshGamepads);
        Controls.Add(labelDevices);
        Controls.Add(buttonSearchBluetooth);
        Controls.Add(buttonGameControllers);
        Controls.Add(checkShowAllBluetooth);
        Controls.Add(checkAutoPair);
        Name = "MandosControl";
        Size = new Size(742, 440);
        panelGamepadList.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();
    }

    private CheckBox checkAutoPair;
    private CheckBox checkShowAllBluetooth;
    private Button buttonGameControllers;
    private Button buttonSearchBluetooth;
    private Button buttonRefreshGamepads;
    private Label labelDevices;
    private Label labelBluetoothSearchStatus;
    private Panel panelGamepadList;
    private FlowLayoutPanel flowLayoutPanelGamepads;
}
