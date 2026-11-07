namespace GDMInputManager.Controls;

partial class MandosControl
{
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Component Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        this.panelTop = new System.Windows.Forms.Panel();
        this.chkAutoPair = new System.Windows.Forms.CheckBox();
        this.chkShowAll = new System.Windows.Forms.CheckBox();
        this.btnGameDevices = new System.Windows.Forms.Button();
        this.btnSearch = new System.Windows.Forms.Button();
        this.lblSection = new System.Windows.Forms.Label();
        this.panelDevices = new System.Windows.Forms.Panel();
        this.flowLayoutPanelDevices = new System.Windows.Forms.FlowLayoutPanel();
        this.panelTop.SuspendLayout();
        this.panelDevices.SuspendLayout();
        this.flowLayoutPanelDevices.SuspendLayout();
        this.SuspendLayout();
        //
        // panelTop
        //
        this.panelTop.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(240)))), ((int)(((byte)(244)))), ((int)(((byte)(248)))));
        this.panelTop.Controls.Add(this.chkAutoPair);
        this.panelTop.Controls.Add(this.chkShowAll);
        this.panelTop.Controls.Add(this.btnGameDevices);
        this.panelTop.Controls.Add(this.btnSearch);
        this.panelTop.Controls.Add(this.lblSection);
        this.panelTop.Dock = System.Windows.Forms.DockStyle.Top;
        this.panelTop.Location = new System.Drawing.Point(0, 0);
        this.panelTop.Name = "panelTop";
        this.panelTop.Size = new System.Drawing.Size(892, 80);
        this.panelTop.TabIndex = 0;
        //
        // chkAutoPair
        //
        this.chkAutoPair.AutoSize = true;
        this.chkAutoPair.Location = new System.Drawing.Point(10, 15);
        this.chkAutoPair.Name = "chkAutoPair";
        this.chkAutoPair.Size = new System.Drawing.Size(220, 19);
        this.chkAutoPair.TabIndex = 0;
        this.chkAutoPair.Text = "Emparejar mandos automáticamente";
        this.chkAutoPair.UseVisualStyleBackColor = true;
        //
        // chkShowAll
        //
        this.chkShowAll.AutoSize = true;
        this.chkShowAll.Location = new System.Drawing.Point(10, 40);
        this.chkShowAll.Name = "chkShowAll";
        this.chkShowAll.Size = new System.Drawing.Size(160, 19);
        this.chkShowAll.TabIndex = 1;
        this.chkShowAll.Text = "Mostrar todos los dispositivos";
        this.chkShowAll.UseVisualStyleBackColor = true;
        //
        // btnGameDevices
        //
        this.btnGameDevices.Location = new System.Drawing.Point(700, 15);
        this.btnGameDevices.Name = "btnGameDevices";
        this.btnGameDevices.Size = new System.Drawing.Size(140, 30);
        this.btnGameDevices.TabIndex = 2;
        this.btnGameDevices.Text = "Dispositivos de Juego";
        this.btnGameDevices.UseVisualStyleBackColor = true;
        //
        // btnSearch
        //
        this.btnSearch.Location = new System.Drawing.Point(700, 50);
        this.btnSearch.Name = "btnSearch";
        this.btnSearch.Size = new System.Drawing.Size(140, 30);
        this.btnSearch.TabIndex = 3;
        this.btnSearch.Text = "Buscar dispositivos";
        this.btnSearch.UseVisualStyleBackColor = true;
        //
        // lblSection
        //
        this.lblSection.AutoSize = true;
        this.lblSection.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
        this.lblSection.Location = new System.Drawing.Point(250, 15);
        this.lblSection.Name = "lblSection";
        this.lblSection.Size = new System.Drawing.Size(173, 21);
        this.lblSection.TabIndex = 4;
        this.lblSection.Text = "Dispositivos conectados";
        //
        // panelDevices
        //
        this.panelDevices.Controls.Add(this.flowLayoutPanelDevices);
        this.panelDevices.Dock = System.Windows.Forms.DockStyle.Fill;
        this.panelDevices.Location = new System.Drawing.Point(0, 80);
        this.panelDevices.Name = "panelDevices";
        this.panelDevices.Size = new System.Drawing.Size(892, 431);
        this.panelDevices.TabIndex = 1;
        //
        // flowLayoutPanelDevices
        //
        this.flowLayoutPanelDevices.AutoScroll = true;
        this.flowLayoutPanelDevices.Dock = System.Windows.Forms.DockStyle.Fill;
        this.flowLayoutPanelDevices.Location = new System.Drawing.Point(0, 0);
        this.flowLayoutPanelDevices.Name = "flowLayoutPanelDevices";
        this.flowLayoutPanelDevices.Size = new System.Drawing.Size(892, 431);
        this.flowLayoutPanelDevices.TabIndex = 0;
        //
        // MandosControl
        //
        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.BackColor = System.Drawing.Color.White;
        this.Controls.Add(this.panelDevices);
        this.Controls.Add(this.panelTop);
        this.Name = "MandosControl";
        this.Size = new System.Drawing.Size(892, 511);
        this.panelTop.ResumeLayout(false);
        this.panelTop.PerformLayout();
        this.panelDevices.ResumeLayout(false);
        this.flowLayoutPanelDevices.ResumeLayout(false);
        this.ResumeLayout(false);

    }

    #endregion

    private System.Windows.Forms.Panel panelTop;
    private System.Windows.Forms.CheckBox chkAutoPair;
    private System.Windows.Forms.CheckBox chkShowAll;
    private System.Windows.Forms.Button btnGameDevices;
    private System.Windows.Forms.Button btnSearch;
    private System.Windows.Forms.Label lblSection;
    private System.Windows.Forms.Panel panelDevices;
    private System.Windows.Forms.FlowLayoutPanel flowLayoutPanelDevices;
}
