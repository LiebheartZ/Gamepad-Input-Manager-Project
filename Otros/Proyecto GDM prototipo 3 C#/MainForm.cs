using System.Drawing;
using System.IO;

namespace GDMInputManager;

public partial class MainForm : Form
{
    public MainForm()
    {
        InitializeComponent();

        // Cargar el icono si existe
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "icono.png");
        if (File.Exists(iconPath))
        {
            pictureBoxLogo.Image = Image.FromFile(iconPath);
        }

        // Personalizar el TabControl
        CustomizeTabControl();
    }

    private void CustomizeTabControl()
    {
        tabControlMain.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabControlMain.DrawItem += TabControlMain_DrawItem;
        tabControlMain.SelectedIndexChanged += TabControlMain_SelectedIndexChanged;
        tabControlMain.SizeMode = TabSizeMode.Fixed;
        tabControlMain.ItemSize = new Size(100, 30);
    }

    private void TabControlMain_SelectedIndexChanged(object? sender, EventArgs e)
    {
        tabControlMain.Invalidate(); // Redibujar el TabControl cuando cambie la selección
    }

    private void TabControlMain_DrawItem(object? sender, DrawItemEventArgs e)
    {
        TabPage tabPage = tabControlMain.TabPages[e.Index];
        Rectangle tabRect = tabControlMain.GetTabRect(e.Index);

        // Fondo de la pestaña
        Color backColor;
        if (e.Index == tabControlMain.SelectedIndex)
        {
            backColor = Color.FromArgb(100, 150, 200); // Azul claro para la pestaña seleccionada
        }
        else
        {
            backColor = Color.FromArgb(50, 70, 100); // Azul medio-oscuro para pestañas no seleccionadas
        }

        using (SolidBrush brush = new SolidBrush(backColor))
        {
            e.Graphics.FillRectangle(brush, tabRect);
        }

        // Texto de la pestaña
        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        Color textColor = Color.White;
        TextRenderer.DrawText(e.Graphics, tabPage.Text, tabControlMain.Font, tabRect, textColor, flags);
    }
}
