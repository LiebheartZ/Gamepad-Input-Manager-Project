using System.Runtime.InteropServices;
using System.Text;

namespace GDM.Services;

public enum BluetoothRadioState
{
    Unknown,
    On,
    Off
}

/// <summary>
/// Detecta si el radio Bluetooth de Windows está encendido enumerando los
/// devnodes "BTH\" del árbol PnP y consultando su estado.
/// </summary>
public static class BluetoothRadioService
{
    private const uint CmLocateDevNodeNormal = 0x00000000;
    private const uint CrSuccess = 0;
    private const uint DnStarted = 0x00000008;
    private const uint CmProbDisabled = 22;
    private const int MaxDepth = 12;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out IntPtr pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Child(out IntPtr pdnDevInst, IntPtr devInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Sibling(out IntPtr pdnDevInst, IntPtr devInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(IntPtr devInst, IntPtr buffer, int bufferLen, int ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, IntPtr devInst, uint ulFlags);

    /// <summary>
    /// Devuelve On si algún radio BTH\ está iniciado, Off si existen pero están
    /// deshabilitados, y Unknown si no hay hardware Bluetooth o hay error.
    /// </summary>
    public static BluetoothRadioState GetState()
    {
        try
        {
            if (CM_Locate_DevNodeW(out IntPtr root, @"HTREE\ROOT\0", CmLocateDevNodeNormal) != CrSuccess)
            {
                return BluetoothRadioState.Unknown;
            }

            var found = new List<(bool Started, bool Disabled)>();
            var visited = new HashSet<long>();
            Walk(root, 0, found, visited);

            if (found.Count == 0)
            {
                return BluetoothRadioState.Unknown;
            }

            return found.Any(d => d.Started && !d.Disabled)
                ? BluetoothRadioState.On
                : BluetoothRadioState.Off;
        }
        catch
        {
            return BluetoothRadioState.Unknown;
        }
    }

    private static void Walk(IntPtr devInst, int depth, List<(bool Started, bool Disabled)> found, HashSet<long> visited)
    {
        if (depth > MaxDepth || devInst == IntPtr.Zero || !visited.Add(devInst.ToInt64()))
        {
            return;
        }

        string? id = GetDeviceInstanceId(devInst);
        if (id is not null && id.StartsWith(@"BTH\", StringComparison.OrdinalIgnoreCase))
        {
            uint statusResult = CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0);
            if (statusResult == CrSuccess)
            {
                found.Add(((status & DnStarted) != 0, problem == CmProbDisabled));
            }
        }

        // Hijos
        if (CM_Get_Child(out IntPtr child, devInst, 0) == CrSuccess)
        {
            Walk(child, depth + 1, found, visited);
        }

        // Hermanos del nodo actual (iterativo para no recursar demasiado)
        if (CM_Get_Sibling(out IntPtr sibling, devInst, 0) == CrSuccess)
        {
            Walk(sibling, depth, found, visited);
        }
    }

    private static string? GetDeviceInstanceId(IntPtr devInst)
    {
        var buffer = new char[512];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            uint result = CM_Get_Device_IDW(devInst, handle.AddrOfPinnedObject(), buffer.Length, 0);
            return result == CrSuccess ? new string(buffer).TrimEnd('\0') : null;
        }
        finally
        {
            handle.Free();
        }
    }
}
