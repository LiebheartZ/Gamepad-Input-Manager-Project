using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GDM.Services;

/// <summary>
/// Reinicia un dispositivo HID a nivel PnP (deshabilitar/habilitar el nodo de
/// dispositivo) para cerrar todos los identificadores abiertos por otros
/// procesos. Pensado como medida de emergencia: un juego u otro programa puede
/// conservar un handle al mando físico oculto por HidHide y seguir recibiendo
/// entrada aunque el dispositivo esté en lista negra.
/// </summary>
public static class PhysicalDeviceRestartService
{
    private const int CR_SUCCESS = 0;
    private const int CrAccessDenied = 5;
    private const int CrDeviceNotThere = 17;
    private const int CrFailure = 25;

    /// <summary>
    /// Deshabilita y rehabilita el nodo de dispositivo HID correspondiente a la
    /// ruta SDL del mando. Devuelve true si el ciclo se completó.
    /// </summary>
    public static async Task<bool> RestartAsync(
        string? devicePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return false;
        }

        string instanceId;
        try
        {
            instanceId = HidHideService.ResolveDeviceInstanceId(devicePath);
        }
        catch (Exception)
        {
            return false;
        }

        return await Task.Run(() => RestartDevNode(instanceId, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool RestartDevNode(string instanceId, CancellationToken cancellationToken)
    {
        int result = NativeMethods.CM_Locate_DevNode(out int devInst, instanceId, 0);
        if (result != CR_SUCCESS)
        {
            return false;
        }

        result = NativeMethods.CM_Disable_DevNode(devInst, 0);
        if (result != CR_SUCCESS)
        {
            return false;
        }

        try
        {
            // Dar tiempo a Windows a cerrar los handles del dispositivo.
            cancellationToken.WaitHandle.WaitOne(400);
        }
        catch (OperationCanceledException)
        {
            // Aun cancelado, intentamos rehabilitar para no dejar el mando muerto.
        }

        result = NativeMethods.CM_Enable_DevNode(devInst, 0);
        return result == CR_SUCCESS;
    }

    private static class NativeMethods
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int CM_Locate_DevNode(
            out int devInst, string deviceID, int flags);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        internal static extern int CM_Disable_DevNode(int devInst, int flags);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        internal static extern int CM_Enable_DevNode(int devInst, int flags);
    }
}
