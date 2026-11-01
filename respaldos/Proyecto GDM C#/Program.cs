namespace Proyecto_GDM_C_;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ControllerForm());
    }
}
