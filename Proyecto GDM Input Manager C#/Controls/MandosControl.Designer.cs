namespace GDMInputManager.Controls;

/// <summary>
/// Archivo de diseñador para MandosControl (generado automáticamente)
/// Estructura: Contiene el código de inicialización de la UI (generado por el diseñador)
/// Propósito: Configurar todos los controles visuales del UserControl
/// Nota: Este archivo no debe editarse manualmente en un entorno de diseño normal
/// Componentes:
///   - listViewGamepads: ListView para mostrar la lista de mandos
///   - buttonRefresh: Botón para actualizar la lista
///   - buttonPair: Botón para emparejar mandos
///   - buttonUnpair: Botón para desemparejar mandos
/// </summary>
partial class MandosControl
{
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Limpia recursos usados por el control
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Inicializa todos los componentes visuales del control
    /// Estructura: Código generado automáticamente por el diseñador de Windows Forms
    /// Propósito: Crear y configurar todos los controles del UserControl
    /// </summary>
    private void InitializeComponent()
    {
        this.listViewGamepads = new System.Windows.Forms.ListView();
        this.columnHeaderName = new System.Windows.Forms.ColumnHeader();
        this.columnHeaderType = new System.Windows.Forms.ColumnHeader();
        this.columnHeaderConnection = new System.Windows.Forms.ColumnHeader();
        this.columnHeaderStatus = new System.Windows.Forms.ColumnHeader();
        this.buttonRefresh = new System.Windows.Forms.Button();
        this.buttonPair = new System.Windows.Forms.Button();
        this.buttonUnpair = new System.Windows.Forms.Button();
        this.SuspendLayout();
        //
        // listViewGamepads
        //
        this.listViewGamepads.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.columnHeaderName,
            this.columnHeaderType,
            this.columnHeaderConnection,
            this.columnHeaderStatus});
        this.listViewGamepads.Location = new System.Drawing.Point(10, 10);
        this.listViewGamepads.Name = "listViewGamepads";
        this.listViewGamepads.Size = new System.Drawing.Size(860, 450);
        this.listViewGamepads.TabIndex = 0;
        this.listViewGamepads.UseCompatibleStateImageBehavior = false;
        this.listViewGamepads.View = System.Windows.Forms.View.Details;
        //
        // columnHeaderName
        //
        this.columnHeaderName.Text = "Nombre";
        this.columnHeaderName.Width = 250;
        //
        // columnHeaderType
        //
        this.columnHeaderType.Text = "Tipo";
        this.columnHeaderType.Width = 150;
        //
        // columnHeaderConnection
        //
        this.columnHeaderConnection.Text = "Conexión";
        this.columnHeaderConnection.Width = 120;
        //
        // columnHeaderStatus
        //
        this.columnHeaderStatus.Text = "Estado";
        this.columnHeaderStatus.Width = 100;
        //
        // buttonRefresh
        //
        this.buttonRefresh.Location = new System.Drawing.Point(10, 470);
        this.buttonRefresh.Name = "buttonRefresh";
        this.buttonRefresh.Size = new System.Drawing.Size(100, 30);
        this.buttonRefresh.TabIndex = 1;
        this.buttonRefresh.Text = "Actualizar";
        this.buttonRefresh.UseVisualStyleBackColor = true;
        this.buttonRefresh.Click += new System.EventHandler(this.buttonRefresh_Click);
        //
        // buttonPair
        //
        this.buttonPair.Location = new System.Drawing.Point(120, 470);
        this.buttonPair.Name = "buttonPair";
        this.buttonPair.Size = new System.Drawing.Size(100, 30);
        this.buttonPair.TabIndex = 2;
        this.buttonPair.Text = "Emparejar";
        this.buttonPair.UseVisualStyleBackColor = true;
        this.buttonPair.Click += new System.EventHandler(this.buttonPair_Click);
        //
        // buttonUnpair
        //
        this.buttonUnpair.Location = new System.Drawing.Point(230, 470);
        this.buttonUnpair.Name = "buttonUnpair";
        this.buttonUnpair.Size = new System.Drawing.Size(100, 30);
        this.buttonUnpair.TabIndex = 3;
        this.buttonUnpair.Text = "Desemparejar";
        this.buttonUnpair.UseVisualStyleBackColor = true;
        this.buttonUnpair.Click += new System.EventHandler(this.buttonUnpair_Click);
        //
        // MandosControl
        //
        this.Controls.Add(this.buttonUnpair);
        this.Controls.Add(this.buttonPair);
        this.Controls.Add(this.buttonRefresh);
        this.Controls.Add(this.listViewGamepads);
        this.Name = "MandosControl";
        this.Size = new System.Drawing.Size(880, 550);
        this.ResumeLayout(false);
    }

    private System.Windows.Forms.ListView listViewGamepads;
    private System.Windows.Forms.ColumnHeader columnHeaderName;
    private System.Windows.Forms.ColumnHeader columnHeaderType;
    private System.Windows.Forms.ColumnHeader columnHeaderConnection;
    private System.Windows.Forms.ColumnHeader columnHeaderStatus;
    private System.Windows.Forms.Button buttonRefresh;
    private System.Windows.Forms.Button buttonPair;
    private System.Windows.Forms.Button buttonUnpair;
}
