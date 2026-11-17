using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;
using GDM.Services.Abstractions;

namespace GDM.Services;

public sealed class WindowsStartupService : IWindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GDM";
    private const string TaskName = "GDM Gamepad Device Manager Elevated Startup";

    public async Task ConfigureAsync(
        bool startWithWindows,
        bool startMinimized,
        bool startWithWindowsAsTask)
    {
        if (startWithWindows && startWithWindowsAsTask)
        {
            await ConfigureScheduledTaskAsync(startMinimized);
            return;
        }

        RunEntrySnapshot previousRunEntry = ReadRunEntry();
        SetRunEntry(startWithWindows, startMinimized);
        try
        {
            await DeleteScheduledTaskIfPresentAsync();
        }
        catch (Exception exception) when (IsStartupException(exception))
        {
            RestoreRunEntry(previousRunEntry, exception);
            throw;
        }
    }

    private static async Task ConfigureScheduledTaskAsync(bool startMinimized)
    {
        RunEntrySnapshot previousRunEntry = ReadRunEntry();
        SetRunEntry(enabled: false, startMinimized: false);
        try
        {
            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "No se pudo determinar la ruta del ejecutable de GDM para crear la tarea.");
            string taskCommand = $"\"{executablePath}\"" +
                (startMinimized ? " --minimized" : string.Empty);
            await RunSchtasksAsync(
                [
                    "/Create",
                    "/SC", "ONLOGON",
                    "/TN", TaskName,
                    "/TR", taskCommand,
                    "/RL", "HIGHEST",
                    "/IT",
                    "/F"
                ],
                elevated: true);
        }
        catch (Exception exception) when (IsStartupException(exception))
        {
            RestoreRunEntry(previousRunEntry, exception);
            throw;
        }
    }

    private static async Task DeleteScheduledTaskIfPresentAsync()
    {
        (int exitCode, _) = await RunSchtasksAsync(
            ["/Query", "/TN", TaskName],
            elevated: false,
            throwOnFailure: false);
        if (exitCode == 0)
        {
            await RunSchtasksAsync(
                ["/Delete", "/TN", TaskName, "/F"],
                elevated: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunSchtasksAsync(
        IEnumerable<string> arguments,
        bool elevated,
        bool throwOnFailure = true)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string executable = Path.Combine(systemDirectory, "schtasks.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = elevated,
            CreateNoWindow = !elevated,
            WindowStyle = elevated ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            RedirectStandardOutput = !elevated,
            RedirectStandardError = !elevated
        };
        if (elevated)
        {
            startInfo.Verb = "runas";
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "No se pudo iniciar la herramienta de tareas programadas de Windows.");
            Task<string>? standardOutput = elevated
                ? null
                : process.StandardOutput.ReadToEndAsync();
            Task<string>? standardError = elevated
                ? null
                : process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string output = standardOutput is null || standardError is null
                ? string.Empty
                : $"{await standardOutput}{await standardError}".Trim();

            if (throwOnFailure && process.ExitCode != 0)
            {
                string details = string.IsNullOrWhiteSpace(output)
                    ? string.Empty
                    : $" {output}";
                throw new InvalidOperationException(
                    $"Windows no pudo actualizar la tarea de inicio de GDM (código {process.ExitCode}).{details}");
            }

            return (process.ExitCode, output);
        }
        catch (Win32Exception exception) when (elevated && exception.NativeErrorCode == 1223)
        {
            throw new UnauthorizedAccessException(
                "Se canceló la autorización de administrador para configurar el inicio de GDM.",
                exception);
        }
    }

    private static RunEntrySnapshot ReadRunEntry()
    {
        using RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (runKey is null || !runKey.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase))
        {
            return new RunEntrySnapshot(false, null, RegistryValueKind.String);
        }

        return new RunEntrySnapshot(
            true,
            runKey.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            runKey.GetValueKind(ValueName));
    }

    private static void SetRunEntry(bool enabled, bool startMinimized)
    {
        using RegistryKey? runKey = enabled
            ? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            : Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (runKey is null)
        {
            if (enabled)
            {
                throw new InvalidOperationException(
                    "No se pudo crear la configuración de inicio de GDM en Windows.");
            }

            return;
        }

        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "No se pudo determinar la ruta del ejecutable de GDM para iniciar con Windows.");
        string arguments = startMinimized ? " --minimized" : string.Empty;
        runKey.SetValue(ValueName, $"\"{executablePath}\"{arguments}", RegistryValueKind.String);
    }

    private static void RestoreRunEntry(RunEntrySnapshot snapshot, Exception operationException)
    {
        try
        {
            using RegistryKey? runKey = snapshot.Exists
                ? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                : Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (runKey is null)
            {
                if (snapshot.Exists)
                {
                    throw new InvalidOperationException(
                        "No se pudo restaurar la configuración previa de inicio de GDM.");
                }

                return;
            }

            if (snapshot.Exists)
            {
                runKey.SetValue(ValueName, snapshot.Value!, snapshot.Kind);
            }
            else
            {
                runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception rollbackException) when (IsStartupException(rollbackException))
        {
            throw new InvalidOperationException(
                "Falló la operación de inicio y tampoco se pudo restaurar la entrada anterior del registro.",
                new AggregateException(operationException, rollbackException));
        }
    }

    private static bool IsStartupException(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            SecurityException or
            InvalidOperationException or
            Win32Exception;

    private sealed record RunEntrySnapshot(
        bool Exists,
        object? Value,
        RegistryValueKind Kind);
}
