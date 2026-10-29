using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GDMInputManager.Controls;

namespace GDMInputManager;

/// <summary>
/// Archivo de diseñador para Main (formulario principal)
/// Estructura: Contiene el código de inicialización de la UI (generado por el diseñador)
/// Propósito: Configurar todos los controles visuales del formulario principal
/// Nota: Este archivo no debe editarse manualmente en un entorno de diseño normal
/// Componentes:
///   - panelHeader: Panel superior con icono y título
///   - tabControlMain: TabControl con 6 pestañas
///   - UserControls: Cada pestaña contiene su UserControl correspondiente
/// </summary>
partial class Main
{
    /// <summary>
    /// Contenedor de componentes del formulario
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Limpia recursos usados por el formulario
    /// </summary>
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
    /// Inicializa todos los componentes visuales del formulario
    /// Estructura: Código generado automáticamente por el diseñador de Windows Forms
    /// Propósito: Crear y configurar todos los controles del formulario principal
    /// Proceso:
    ///   1. Crea el panel header con icono y título
    ///   2. Crea el TabControl con 6 pestañas
    ///   3. Crea los UserControls para cada pestaña
    ///   4. Agrega todos los controles al formulario
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(900, 650);
        Text = "GDM Gamepad Device Manager";
        StartPosition = FormStartPosition.CenterScreen;

        // Panel superior (header)
        panelHeader = new Panel();
        panelHeader.Location = new Point(0, 0);
        panelHeader.Size = new Size(900, 60);
        panelHeader.BackColor = Color.FromArgb(0, 51, 102); // Azul oscuro
        panelHeader.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        // Icono del gamepad
        pictureBoxIcon = new PictureBox();
        pictureBoxIcon.Size = new Size(40, 40);
        pictureBoxIcon.Location = new Point(15, 10);
        pictureBoxIcon.SizeMode = PictureBoxSizeMode.Zoom;
        // Cargar icono desde archivo
        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icono.ico");
            if (File.Exists(iconPath))
            {
                pictureBoxIcon.Image = System.Drawing.Image.FromFile(iconPath);
            }
        }
        catch
        {
            // Si no se puede cargar el icono, dejar vacío
        }

        // Label del título
        lblTitle = new Label();
        lblTitle.Text = "GDM Gamepad Device Manager";
        lblTitle.Location = new Point(65, 15);
        lblTitle.Size = new Size(400, 30);
        lblTitle.Font = new Font("Microsoft Sans Serif", 14, FontStyle.Bold);
        lblTitle.ForeColor = Color.White;
        lblTitle.TextAlign = ContentAlignment.MiddleLeft;

        // Agregar controles al panel header
        panelHeader.Controls.Add(pictureBoxIcon);
        panelHeader.Controls.Add(lblTitle);

        // Agregar panel header al formulario
        Controls.Add(panelHeader);

        // TabControl principal
        tabControlMain = new TabControl();
        tabControlMain.Location = new Point(0, 60);
        tabControlMain.Size = new Size(900, 590);
        tabControlMain.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        tabControlMain.BackColor = Color.SkyBlue; // Azul oscuro igual al panel header

        // Pestaña Mandos
        tabPageMandos = new TabPage();
        tabPageMandos.Text = "Mandos";
        tabPageMandos.BackColor = Color.White; // Azul oscuro
        mandosControl = new MandosControl();
        mandosControl.Dock = DockStyle.Fill;
        tabPageMandos.Controls.Add(mandosControl);

        // Pestaña Tester
        tabPageTester = new TabPage();
        tabPageTester.Text = "Tester";
        tabPageTester.BackColor = Color.White; // Azul oscuro
        testerControl = new TesterControl();
        testerControl.Dock = DockStyle.Fill;
        tabPageTester.Controls.Add(testerControl);

        // Pestaña Perfiles
        tabPagePerfiles = new TabPage();
        tabPagePerfiles.Text = "Perfiles";
        tabPagePerfiles.BackColor = Color.White; // Azul oscuro
        perfilesControl = new PerfilesControl();
        perfilesControl.Dock = DockStyle.Fill;
        tabPagePerfiles.Controls.Add(perfilesControl);

        // Pestaña HidHide
        tabPageHidHide = new TabPage();
        tabPageHidHide.Text = "HidHide";
        tabPageHidHide.BackColor = Color.White; // Azul oscuro
        hidHideControl = new HidHideControl();
        hidHideControl.Dock = DockStyle.Fill;
        tabPageHidHide.Controls.Add(hidHideControl);

        // Pestaña Opciones
        tabPageOpciones = new TabPage();
        tabPageOpciones.Text = "Opciones";
        tabPageOpciones.BackColor = Color.White; // Azul oscuro
        opcionesControl = new OpcionesControl();
        opcionesControl.Dock = DockStyle.Fill;
        tabPageOpciones.Controls.Add(opcionesControl);

        // Pestaña Registro
        tabPageRegistro = new TabPage();
        tabPageRegistro.Text = "Registro";
        tabPageRegistro.BackColor = Color.White; // Azul oscuro
        registroControl = new RegistroControl();
        registroControl.Dock = DockStyle.Fill;
        tabPageRegistro.Controls.Add(registroControl);

        // Agregar pestañas al TabControl
        tabControlMain.TabPages.Add(tabPageMandos);
        tabControlMain.TabPages.Add(tabPageTester);
        tabControlMain.TabPages.Add(tabPagePerfiles);
        tabControlMain.TabPages.Add(tabPageHidHide);
        tabControlMain.TabPages.Add(tabPageOpciones);
        tabControlMain.TabPages.Add(tabPageRegistro);

        // Agregar TabControl al formulario
        Controls.Add(tabControlMain);
    }

    private Panel panelHeader;
    private PictureBox pictureBoxIcon;
    private Label lblTitle;
    private TabControl tabControlMain;
    private TabPage tabPageMandos;
    private TabPage tabPageTester;
    private TabPage tabPagePerfiles;
    private TabPage tabPageHidHide;
    private TabPage tabPageOpciones;
    private TabPage tabPageRegistro;
    private MandosControl mandosControl;
    private TesterControl testerControl;
    private PerfilesControl perfilesControl;
    private HidHideControl hidHideControl;
    private OpcionesControl opcionesControl;
    private RegistroControl registroControl;

    #endregion

    #region Windows API - Device Detection

    /// <summary>
    /// Constantes y estructuras de la API de Windows para detección de dispositivos
    /// Estructura: Definiciones de la API de Windows para mensajes WM_DEVICECHANGE
    /// Propósito: Permitir al formulario capturar eventos de conexión/desconexión de dispositivos
    /// Componentes:
    ///   - WM_DEVICECHANGE: Mensaje de Windows que indica cambios en dispositivos
    ///   - DBT_DEVICEARRIVAL: Indica que un dispositivo se conectó
    ///   - DBT_DEVICEREMOVECOMPLETE: Indica que un dispositivo se desconectó
    ///   - DEV_BROADCAST_DEVICEINTERFACE: Estructura con información del dispositivo
    /// Nota: Estas definiciones son necesarias para interceptar mensajes de Windows
    /// </summary>

    /// <summary>
    /// Mensaje de Windows para cambios en dispositivos
    /// </summary>
    public const int WM_DEVICECHANGE = 0x0219;

    /// <summary>
    /// Dispositivo conectado
    /// </summary>
    public const int DBT_DEVICEARRIVAL = 0x8000;

    /// <summary>
    /// Dispositivo desconectado completamente
    /// </summary>
    public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    /// <summary>
    /// Tipo de dispositivo: interfaz de dispositivo
    /// </summary>
    public const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;

    /// <summary>
    /// Cabecera de información de broadcast de dispositivo
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public class DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    /// <summary>
    /// Información detallada de la interfaz del dispositivo
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public class DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 255)]
        public string dbcc_name;
    }

    #endregion
}
