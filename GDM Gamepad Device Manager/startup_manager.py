"""Windows Startup-folder shortcut management for GDM."""

from __future__ import annotations

import base64
import os
from pathlib import Path
import platform
import subprocess
import sys
from typing import Callable, Optional


class StartupShortcutError(RuntimeError):
    pass


def set_windows_startup(
    enabled: bool,
    start_minimized: bool,
    app_directory: Optional[Path] = None,
    runner: Callable[..., subprocess.CompletedProcess] = subprocess.run,
) -> Path:
    if platform.system() != "Windows":
        raise StartupShortcutError("El inicio automático solo está disponible en Windows.")

    app_directory = Path(app_directory or Path(__file__).resolve().parent)
    startup_folder = Path(os.environ.get("APPDATA", "")) / "Microsoft" / "Windows" / "Start Menu" / "Programs" / "Startup"
    if not startup_folder.is_absolute():
        raise StartupShortcutError("Windows no proporcionó una ruta válida a la carpeta de Inicio.")
    shortcut_path = startup_folder / "GDM.lnk"
    legacy_shortcut_path = startup_folder / "GDM (Gamepad Device Manager).lnk"

    target = Path(sys.executable).resolve()
    if target.name.casefold() != "pythonw.exe":
        target = target.with_name("pythonw.exe")
    script_path = app_directory / "gdm.pyw"
    if enabled and not target.is_file():
        raise StartupShortcutError(f"No se encontró Python sin consola: {target}")
    if enabled and not script_path.is_file():
        raise StartupShortcutError(f"No se encontró el punto de entrada de GDM: {script_path}")

    arguments = [str(script_path)]
    if start_minimized:
        arguments.append("--start-in-tray")

    script = r"""
$ErrorActionPreference = 'Stop'
$shortcutPath = $env:GDM_STARTUP_SHORTCUT
$legacyShortcutPath = $env:GDM_LEGACY_STARTUP_SHORTCUT
if (Test-Path -LiteralPath $legacyShortcutPath) {
    Remove-Item -LiteralPath $legacyShortcutPath -Force
}
if ($env:GDM_STARTUP_ENABLED -eq '1') {
    $startupFolder = Split-Path -Parent $shortcutPath
    New-Item -ItemType Directory -Path $startupFolder -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $env:GDM_STARTUP_TARGET
    $shortcut.Arguments = $env:GDM_STARTUP_ARGUMENTS
    $shortcut.WorkingDirectory = $env:GDM_STARTUP_WORKING_DIRECTORY
    $shortcut.Description = 'Inicia GDM con Windows'
    $shortcut.Save()
} elseif (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}
"""
    encoded_script = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
    environment = os.environ.copy()
    environment.update({
        "GDM_STARTUP_SHORTCUT": str(shortcut_path),
        "GDM_LEGACY_STARTUP_SHORTCUT": str(legacy_shortcut_path),
        "GDM_STARTUP_ENABLED": "1" if enabled else "0",
        "GDM_STARTUP_TARGET": str(target),
        "GDM_STARTUP_ARGUMENTS": subprocess.list2cmdline(arguments),
        "GDM_STARTUP_WORKING_DIRECTORY": str(app_directory),
    })
    try:
        result = runner(
            [
                "powershell.exe",
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-EncodedCommand",
                encoded_script,
            ],
            capture_output=True,
            text=True,
            timeout=15,
            check=False,
            env=environment,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except subprocess.TimeoutExpired as error:
        raise StartupShortcutError("Windows tardó demasiado en actualizar el acceso directo de Inicio.") from error
    except OSError as error:
        raise StartupShortcutError(f"No se pudo actualizar el acceso directo de Inicio: {error}") from error

    if result.returncode != 0:
        detail = (result.stderr or result.stdout or f"Código {result.returncode}").strip()
        if "access is denied" in detail.casefold() or "acceso denegado" in detail.casefold():
            detail = f"{detail} Comprueba los permisos de la carpeta de Inicio."
        raise StartupShortcutError(f"No se pudo actualizar el inicio automático de GDM: {detail}")
    return shortcut_path
