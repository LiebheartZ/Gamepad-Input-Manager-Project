using System.Diagnostics;
using System.IO;
using System.Text;

namespace GDM.Services;

public static class UpdateInstaller
{
    public static string PrepareApplyScript(
        string sourceDirectory,
        string targetDirectory,
        int ownerProcessId)
    {
        string scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"gdm-update-{Guid.NewGuid():N}.cmd");

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("setlocal");
        script.AppendLine("set \"LOCK=%TEMP%\\gdm-update.lock\"");
        script.AppendLine("mkdir \"%LOCK%\" 2>nul");
        script.AppendLine("if errorlevel 1 exit /b 0");
        script.AppendLine(":wait");
        script.AppendLine(
            $"tasklist /FI \"PID eq {ownerProcessId}\" /NH | find \"{ownerProcessId}\" >nul");
        script.AppendLine("if %errorlevel%==0 (timeout /t 1 /nobreak >nul & goto wait)");
        script.AppendLine("taskkill /F /IM GDM.exe >nul 2>&1");
        script.AppendLine("timeout /t 2 /nobreak >nul");
        script.AppendLine(
            $"robocopy \"{sourceDirectory}\" \"{targetDirectory}\" /E /NFL /NDL /NJH /NJS /NP /R:3 /W:2");
        script.AppendLine("if %errorlevel% GEQ 8 (");
        script.AppendLine(
            "  echo %date% %time% Error de robocopy: %errorlevel% origen=\"" + sourceDirectory +
            "\" destino=\"" + targetDirectory + "\" > \"%TEMP%\\gdm-update-error.txt\"");
        script.AppendLine("  rmdir \"%LOCK%\" 2>nul");
        script.AppendLine("  del \"%~f0\" 2>nul");
        script.AppendLine("  exit /b 1");
        script.AppendLine(")");
        script.AppendLine(
            $"start \"\" /D \"{targetDirectory}\" \"{targetDirectory}\\GDM.exe\"");
        script.AppendLine("rmdir \"%LOCK%\" 2>nul");
        script.AppendLine("(goto) 2>nul & del \"%~f0\"");
        File.WriteAllText(scriptPath, script.ToString());
        return scriptPath;
    }

    public static void LaunchDetached(string scriptPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(startInfo);
    }
}
