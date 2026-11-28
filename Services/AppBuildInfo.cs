using System.Reflection;

namespace GDM.Services;

/// <summary>
/// Número de build de la aplicación. Es el único identificador de versión:
/// aumenta en uno por cada compilación, igual que la carpeta BuildCheck.
/// </summary>
public static class AppBuildInfo
{
    private static readonly Version? AssemblyVersion =
        Assembly.GetEntryAssembly()?.GetName().Version;

    /// <summary>Número de build (p. ej. 264). 0 si no se pudo leer.</summary>
    public static int Number => AssemblyVersion?.Major ?? 0;

    /// <summary>Versión completa del ensamblado para comparaciones (p. ej. "264.0.0").</summary>
    public static string VersionText => AssemblyVersion?.ToString(3) ?? "0.0.0";

    /// <summary>Etiqueta corta del build (p. ej. "264", "local").</summary>
    public static string Display => Number > 0 ? Number.ToString() : "local";
}
