namespace GDMInputManager;

partial class MainForm
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
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

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        this.panelHeader = new System.Windows.Forms.Panel();
        this.pictureBoxLogo = new System.Windows.Forms.PictureBox();
        this.lblTitle = new System.Windows.Forms.Label();
        this.tabControlMain = new System.Windows.Forms.TabControl();
        this.tabPageMandos = new System.Windows.Forms.TabPage();
        this.tabPageTester = new System.Windows.Forms.TabPage();
        this.tabPagePerfiles = new System.Windows.Forms.TabPage();
        this.tabPageHidHide = new System.Windows.Forms.TabPage();
        this.tabPageOpciones = new System.Windows.Forms.TabPage();
        this.tabPageRegistro = new System.Windows.Forms.TabPage();
        this.mandosControl = new GDMInputManager.Controls.MandosControl();
        this.testerControl = new GDMInputManager.Controls.TesterControl();
        this.perfilesControl = new GDMInputManager.Controls.PerfilesControl();
        this.hidHideControl = new GDMInputManager.Controls.HidHideControl();
        this.opcionesControl = new GDMInputManager.Controls.OpcionesControl();
        this.registroControl = new GDMInputManager.Controls.RegistroControl();
        this.panelHeader.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)(this.pictureBoxLogo)).BeginInit();
        this.tabControlMain.SuspendLayout();
        this.tabPageMandos.SuspendLayout();
        this.tabPageTester.SuspendLayout();
        this.tabPagePerfiles.SuspendLayout();
        this.tabPageHidHide.SuspendLayout();
        this.tabPageOpciones.SuspendLayout();
        this.tabPageRegistro.SuspendLayout();
        this.SuspendLayout();
        //
        // panelHeader
        //
        this.panelHeader.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(30)))), ((int)(((byte)(40)))), ((int)(((byte)(60)))));
        this.panelHeader.Controls.Add(this.pictureBoxLogo);
        this.panelHeader.Controls.Add(this.lblTitle);
        this.panelHeader.Dock = System.Windows.Forms.DockStyle.Top;
        this.panelHeader.Location = new System.Drawing.Point(0, 0);
        this.panelHeader.Name = "panelHeader";
        this.panelHeader.Size = new System.Drawing.Size(900, 60);
        this.panelHeader.TabIndex = 0;
        //
        // pictureBoxLogo
        //
        this.pictureBoxLogo.Location = new System.Drawing.Point(10, 8);
        this.pictureBoxLogo.Name = "pictureBoxLogo";
        this.pictureBoxLogo.Size = new System.Drawing.Size(44, 44);
        this.pictureBoxLogo.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
        this.pictureBoxLogo.TabIndex = 0;
        this.pictureBoxLogo.TabStop = false;
        //
        // lblTitle
        //
        this.lblTitle.AutoSize = true;
        this.lblTitle.Font = new System.Drawing.Font("Segoe UI", 14F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
        this.lblTitle.ForeColor = System.Drawing.Color.White;
        this.lblTitle.Location = new System.Drawing.Point(65, 18);
        this.lblTitle.Name = "lblTitle";
        this.lblTitle.Size = new System.Drawing.Size(276, 25);
        this.lblTitle.TabIndex = 1;
        this.lblTitle.Text = "GDM Gamepad Device Manager";
        //
        // tabControlMain
        //
        this.tabControlMain.Controls.Add(this.tabPageMandos);
        this.tabControlMain.Controls.Add(this.tabPageTester);
        this.tabControlMain.Controls.Add(this.tabPagePerfiles);
        this.tabControlMain.Controls.Add(this.tabPageHidHide);
        this.tabControlMain.Controls.Add(this.tabPageOpciones);
        this.tabControlMain.Controls.Add(this.tabPageRegistro);
        this.tabControlMain.Dock = System.Windows.Forms.DockStyle.Fill;
        this.tabControlMain.Location = new System.Drawing.Point(0, 60);
        this.tabControlMain.Name = "tabControlMain";
        this.tabControlMain.SelectedIndex = 0;
        this.tabControlMain.Size = new System.Drawing.Size(900, 540);
        this.tabControlMain.TabIndex = 1;
        this.tabControlMain.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(50)))), ((int)(((byte)(70)))), ((int)(((byte)(100)))));
        //
        // tabPageMandos
        //
        this.tabPageMandos.BackColor = System.Drawing.Color.White;
        this.tabPageMandos.Controls.Add(this.mandosControl);
        this.tabPageMandos.Location = new System.Drawing.Point(4, 25);
        this.tabPageMandos.Name = "tabPageMandos";
        this.tabPageMandos.Padding = new System.Windows.Forms.Padding(3);
        this.tabPageMandos.Size = new System.Drawing.Size(892, 511);
        this.tabPageMandos.TabIndex = 0;
        this.tabPageMandos.Text = "Mandos";
        this.tabPageMandos.UseVisualStyleBackColor = false;
        //
        // mandosControl
        //
        this.mandosControl.Dock = System.Windows.Forms.DockStyle.Fill;
        this.mandosControl.Location = new System.Drawing.Point(3, 3);
        this.mandosControl.Name = "mandosControl";
        this.mandosControl.Size = new System.Drawing.Size(886, 505);
        this.mandosControl.TabIndex = 0;
        //
        // tabPageTester
        //
        this.tabPageTester.BackColor = System.Drawing.Color.White;
        this.tabPageTester.Controls.Add(this.testerControl);
        this.tabPageTester.Location = new System.Drawing.Point(4, 25);
        this.tabPageTester.Name = "tabPageTester";
        this.tabPageTester.Padding = new System.Windows.Forms.Padding(3);
        this.tabPageTester.Size = new System.Drawing.Size(892, 511);
        this.tabPageTester.TabIndex = 1;
        this.tabPageTester.Text = "Tester";
        this.tabPageTester.UseVisualStyleBackColor = false;
        //
        // testerControl
        //
        this.testerControl.Dock = System.Windows.Forms.DockStyle.Fill;
        this.testerControl.Location = new System.Drawing.Point(3, 3);
        this.testerControl.Name = "testerControl";
        this.testerControl.Size = new System.Drawing.Size(886, 505);
        this.testerControl.TabIndex = 0;
        //
        // tabPagePerfiles
        //
        this.tabPagePerfiles.BackColor = System.Drawing.Color.White;
        this.tabPagePerfiles.Controls.Add(this.perfilesControl);
        this.tabPagePerfiles.Location = new System.Drawing.Point(4, 25);
        this.tabPagePerfiles.Name = "tabPagePerfiles";
        this.tabPagePerfiles.Padding = new System.Windows.Forms.Padding(3);
        this.tabPagePerfiles.Size = new System.Drawing.Size(892, 511);
        this.tabPagePerfiles.TabIndex = 2;
        this.tabPagePerfiles.Text = "Perfiles";
        this.tabPagePerfiles.UseVisualStyleBackColor = false;
        //
        // perfilesControl
        //
        this.perfilesControl.Dock = System.Windows.Forms.DockStyle.Fill;
        this.perfilesControl.Location = new System.Drawing.Point(3, 3);
        this.perfilesControl.Name = "perfilesControl";
        this.perfilesControl.Size = new System.Drawing.Size(886, 505);
        this.perfilesControl.TabIndex = 0;
        //
        // tabPageHidHide
        //
        this.tabPageHidHide.BackColor = System.Drawing.Color.White;
        this.tabPageHidHide.Controls.Add(this.hidHideControl);
        this.tabPageHidHide.Location = new System.Drawing.Point(4, 25);
        this.tabPageHidHide.Name = "tabPageHidHide";
        this.tabPageHidHide.Padding = new System.Windows.Forms.Padding(3);
        this.tabPageHidHide.Size = new System.Drawing.Size(892, 511);
        this.tabPageHidHide.TabIndex = 3;
        this.tabPageHidHide.Text = "HidHide";
        this.tabPageHidHide.UseVisualStyleBackColor = false;
        //
        // hidHideControl
        //
        this.hidHideControl.Dock = System.Windows.Forms.DockStyle.Fill;
        this.hidHideControl.Location = new System.Drawing.Point(3, 3);
        this.hidHideControl.Name = "hidHideControl";
        this.hidHideControl.Size = new System.Drawing.Size(886, 505);
        this.hidHideControl.TabIndex = 0;
        //
        // tabPageOpciones
        //
        this.tabPageOpciones.BackColor = System.Drawing.Color.White;
        this.tabPageOpciones.Controls.Add(this.opcionesControl);
        this.tabPageOpciones.Location = new System.Drawing.Point(4, 25);
        this.tabPageOpciones.Name = "tabPageOpciones";
        this.tabPageOpciones.Padding = new System.Windows.Forms.Padding(3);
        this.tabPageOpciones.Size = new System.Drawing.Size(892, 511);
        this.tabPageOpciones.TabIndex = 4;
        this.tabPageOpciones.Text = "Opciones";
        this.tabPageOpciones.UseVisualStyleBackColor = false;
        //
        // opcionesControl
        //
        this.opcionesControl.Dock = System.Windows.Forms.DockStyle.Fill;
        this.opcionesControl.Location = new System.Drawing.Point(3, 3);
        this.opcionesControl.Name = "opcionesControl";
        this.opcionesControl.Size = new System.Drawing.Size(886, 505);
        this.opcionesControl.TabIndex = 0;
        //
        // tabPageRegistro
        //
        this.tabPageRegistro.BackColor = System.Drawing.Color.White;
        this.tabPageRegistro.Controls.Add(this.registroControl);
        this.tabPageRegistro.Location = new System.Drawing.Point(4, 25);
        this.tabPageRegistro.Name = "tabPageRegistro";
        this.tabPageRegistro.Padding = new System.Windows.Forms.Padding(3);
        this.tabPageRegistro.Size = new System.Drawing.Size(892, 511);
        this.tabPageRegistro.TabIndex = 5;
        this.tabPageRegistro.Text = "Registro";
        this.tabPageRegistro.UseVisualStyleBackColor = false;
        //
        // registroControl
        //
        this.registroControl.Dock = System.Windows.Forms.DockStyle.Fill;
        this.registroControl.Location = new System.Drawing.Point(3, 3);
        this.registroControl.Name = "registroControl";
        this.registroControl.Size = new System.Drawing.Size(886, 505);
        this.registroControl.TabIndex = 0;
        //
        // MainForm
        //
        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.ClientSize = new System.Drawing.Size(900, 600);
        this.Controls.Add(this.tabControlMain);
        this.Controls.Add(this.panelHeader);
        this.MinimumSize = new System.Drawing.Size(900, 600);
        this.Name = "MainForm";
        this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        this.Text = "GDM Gamepad Device Manager";
        this.panelHeader.ResumeLayout(false);
        this.panelHeader.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)(this.pictureBoxLogo)).EndInit();
        this.tabControlMain.ResumeLayout(false);
        this.tabPageMandos.ResumeLayout(false);
        this.tabPageTester.ResumeLayout(false);
        this.tabPagePerfiles.ResumeLayout(false);
        this.tabPageHidHide.ResumeLayout(false);
        this.tabPageOpciones.ResumeLayout(false);
        this.tabPageRegistro.ResumeLayout(false);
        this.ResumeLayout(false);

    }

    #endregion

    private System.Windows.Forms.Panel panelHeader;
    private System.Windows.Forms.PictureBox pictureBoxLogo;
    private System.Windows.Forms.Label lblTitle;
    private System.Windows.Forms.TabControl tabControlMain;
    private System.Windows.Forms.TabPage tabPageMandos;
    private System.Windows.Forms.TabPage tabPageTester;
    private System.Windows.Forms.TabPage tabPagePerfiles;
    private System.Windows.Forms.TabPage tabPageHidHide;
    private System.Windows.Forms.TabPage tabPageOpciones;
    private System.Windows.Forms.TabPage tabPageRegistro;
    private Controls.MandosControl mandosControl;
    private Controls.TesterControl testerControl;
    private Controls.PerfilesControl perfilesControl;
    private Controls.HidHideControl hidHideControl;
    private Controls.OpcionesControl opcionesControl;
    private Controls.RegistroControl registroControl;
}
