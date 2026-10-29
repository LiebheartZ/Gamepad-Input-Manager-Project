namespace GDMInputManager.Controls;

/// <summary>
/// Archivo de diseñador para RegistroControl (generado automáticamente)
/// Estructura: Contiene el código de inicialización de la UI (generado por el diseñador)
/// Propósito: Configurar todos los controles visuales del UserControl
/// Nota: Este archivo no debe editarse manualmente en un entorno de diseño normal
/// Componentes:
///   - richTextBoxLog: RichTextBox para mostrar la consola de logging
///   - buttonClear: Botón para limpiar la consola
///   - buttonCopy: Botón para copiar el contenido al portapapeles
/// </summary>
partial class RegistroControl
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
        this.richTextBoxLog = new System.Windows.Forms.RichTextBox();
        this.buttonClear = new System.Windows.Forms.Button();
        this.buttonCopy = new System.Windows.Forms.Button();
        this.SuspendLayout();
        //
        // richTextBoxLog
        //
        this.richTextBoxLog.BackColor = System.Drawing.Color.Black;
        this.richTextBoxLog.Dock = System.Windows.Forms.DockStyle.Fill;
        this.richTextBoxLog.Font = new System.Drawing.Font("Consolas", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
        this.richTextBoxLog.ForeColor = System.Drawing.Color.White;
        this.richTextBoxLog.Location = new System.Drawing.Point(0, 0);
        this.richTextBoxLog.Name = "richTextBoxLog";
        this.richTextBoxLog.ReadOnly = true;
        this.richTextBoxLog.Size = new System.Drawing.Size(880, 550);
        this.richTextBoxLog.TabIndex = 0;
        this.richTextBoxLog.Text = "";
        //
        // buttonClear
        //
        this.buttonClear.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
        this.buttonClear.Location = new System.Drawing.Point(710, 515);
        this.buttonClear.Name = "buttonClear";
        this.buttonClear.Size = new System.Drawing.Size(75, 30);
        this.buttonClear.TabIndex = 1;
        this.buttonClear.Text = "Limpiar";
        this.buttonClear.UseVisualStyleBackColor = true;
        this.buttonClear.Click += new System.EventHandler(this.buttonClear_Click);
        //
        // buttonCopy
        //
        this.buttonCopy.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
        this.buttonCopy.Location = new System.Drawing.Point(795, 515);
        this.buttonCopy.Name = "buttonCopy";
        this.buttonCopy.Size = new System.Drawing.Size(75, 30);
        this.buttonCopy.TabIndex = 2;
        this.buttonCopy.Text = "Copiar";
        this.buttonCopy.UseVisualStyleBackColor = true;
        this.buttonCopy.Click += new System.EventHandler(this.buttonCopy_Click);
        //
        // RegistroControl
        //
        this.Controls.Add(this.buttonCopy);
        this.Controls.Add(this.buttonClear);
        this.Controls.Add(this.richTextBoxLog);
        this.Name = "RegistroControl";
        this.Size = new System.Drawing.Size(880, 550);
        this.ResumeLayout(false);
    }

    private System.Windows.Forms.RichTextBox richTextBoxLog;
    private System.Windows.Forms.Button buttonClear;
    private System.Windows.Forms.Button buttonCopy;
}
