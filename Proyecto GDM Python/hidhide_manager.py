"""Optional HidHide CLI integration for managing physical gamepad visibility."""

from __future__ import annotations

import json
import ntpath
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
from typing import Any, Callable, Optional


class HidHideError(RuntimeError):
    pass


def virtual_gamepad_type(name: str, device: dict[str, Any]) -> Optional[str]:
    identifiers = [name]
    for key in (
        "deviceInstancePath",
        "baseContainerDeviceInstancePath",
        "parentDeviceInstancePath",
        "hardwareId",
        "hardwareIds",
    ):
        value = device.get(key)
        if isinstance(value, str):
            identifiers.append(value)
        elif isinstance(value, (list, tuple, set)):
            identifiers.extend(str(item) for item in value)
    searchable = " ".join(identifiers).casefold()
    if "vigem" in searchable:
        return "ViGEmBus"
    if "steamvirtual" in searchable or "steam virtual gamepad" in searchable:
        return "Steam Virtual Gamepad"
    if "vjoy" in searchable:
        return "vJoy"
    if any(marker in searchable for marker in ("virtualgamepad", "virtual gamepad", "virtualcontroller", "virtual controller")):
        return "Mando virtual"
    return None


class HidHideCLIService:
    def __init__(
        self,
        cli_path: Optional[Path] = None,
        app_path: Optional[Path] = None,
        runner: Callable[..., Any] = subprocess.run,
    ) -> None:
        self.cli_path = Path(cli_path) if cli_path is not None else self.find_cli()
        self.app_path = Path(app_path or sys.executable).resolve()
        self._runner = runner

    @staticmethod
    def find_cli() -> Optional[Path]:
        if platform.system() != "Windows":
            return None

        install_path = None
        try:
            import winreg

            registry_key = (
                r"SOFTWARE\Nefarius Software Solutions e.U."
                r"\Nefarius Software Solutions e.U. HidHide"
            )
            with winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, registry_key) as key:
                install_path, _ = winreg.QueryValueEx(key, "Path")
        except (ImportError, OSError):
            pass

        candidates: list[Path] = []
        if install_path:
            root = Path(install_path)
            candidates.extend((
                root if root.name.casefold() == "hidhidecli.exe" else root / "HidHideCLI.exe",
                root / "x64" / "HidHideCLI.exe",
            ))
        for variable in ("ProgramFiles", "ProgramFiles(x86)"):
            program_files = os.environ.get(variable)
            if program_files:
                candidates.extend((
                    Path(program_files) / "Nefarius Software Solutions" / "HidHide" / "x64" / "HidHideCLI.exe",
                    Path(program_files) / "Nefarius Software Solutions" / "HidHide" / "HidHideCLI.exe",
                ))
        return next((candidate for candidate in candidates if candidate.is_file()), None)

    def _run(self, command: str, *arguments: str) -> str:
        if self.cli_path is None or not self.cli_path.is_file():
            raise HidHideError(
                "No se encontró HidHideCLI.exe. Instala HidHide desde su sitio oficial "
                "y reinicia GDM."
            )
        try:
            result = self._runner(
                [str(self.cli_path), f"--{command}", *arguments],
                capture_output=True,
                text=True,
                timeout=15,
                check=False,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except subprocess.TimeoutExpired as error:
            raise HidHideError("HidHideCLI tardó demasiado en responder.") from error
        except OSError as error:
            raise HidHideError(f"No se pudo iniciar HidHideCLI: {error}") from error

        output = (result.stdout or "").strip()
        if result.returncode != 0:
            detail = (result.stderr or output or f"Código {result.returncode}").strip()
            if "access is denied" in detail.casefold() or "acceso denegado" in detail.casefold():
                detail = f"{detail} Ejecuta GDM como administrador para cambiar la configuración de HidHide."
            else:
                detail = f"{detail} Comprueba que HidHide esté instalado y, si el acceso fue denegado, ejecuta GDM como administrador."
            raise HidHideError(f"HidHideCLI --{command} falló: {detail}")
        return output

    def _read_gamepads(self) -> list[dict[str, Any]]:
        output = self._run("dev-gaming")
        json_start = output.find("[")
        if json_start < 0:
            raise HidHideError("HidHideCLI no devolvió una lista válida de mandos.")
        try:
            groups = json.loads(output[json_start:])
        except json.JSONDecodeError as error:
            raise HidHideError(f"No se pudo interpretar la lista de mandos de HidHide: {error}") from error
        if not isinstance(groups, list):
            raise HidHideError("El formato de lista devuelto por HidHide no es válido.")

        devices: dict[str, dict[str, Any]] = {}
        for group in groups:
            if not isinstance(group, dict) or not isinstance(group.get("devices"), list):
                continue
            friendly_name = str(group.get("friendlyName") or "Mando sin nombre")
            for device in group["devices"]:
                if not isinstance(device, dict) or not device.get("gamingDevice"):
                    continue
                instance_path = device.get("deviceInstancePath")
                if not isinstance(instance_path, str) or not instance_path.strip():
                    continue
                container_path = device.get("baseContainerDeviceInstancePath")
                key = str(container_path or instance_path).casefold()
                virtual_type = virtual_gamepad_type(friendly_name, device)
                device_type = "virtual" if virtual_type else "unknown"
                record = devices.setdefault(key, {
                    "id": key,
                    "name": friendly_name,
                    "present": False,
                    "virtual_type": virtual_type,
                    "device_type": device_type,
                    "paths": [],
                    "vendor": str(device.get("vendor") or ""),
                    "product": str(device.get("product") or ""),
                })
                record["present"] = record["present"] or bool(device.get("present"))
                if virtual_type and not record["virtual_type"]:
                    record["virtual_type"] = virtual_type
                if device_type == "virtual":
                    record["device_type"] = device_type
                if instance_path.casefold() not in {path.casefold() for path in record["paths"]}:
                    record["paths"].append(instance_path)

        return sorted(
            devices.values(),
            key=lambda device: (device["device_type"] == "virtual", device["name"].casefold(), device["id"]),
        )

    def _read_hidden_paths(self) -> set[str]:
        output = self._run("dev-list")
        return {
            match.group(1).casefold()
            for line in output.splitlines()
            if (match := re.match(r'^\s*--dev-hide\s+"(.*)"\s*$', line))
        }

    def _read_allowed_apps(self) -> set[str]:
        output = self._run("app-list")
        return {
            ntpath.normcase(ntpath.normpath(match.group(1)))
            for line in output.splitlines()
            if (match := re.match(r'^\s*--app-reg\s+"(.*)"\s*$', line))
        }

    def _read_cloak_state(self) -> bool:
        output = self._run("cloak-state").casefold()
        if "--cloak-on" in output:
            return True
        if "--cloak-off" in output:
            return False
        raise HidHideError("HidHideCLI devolvió un estado de ocultamiento desconocido.")

    def refresh(self) -> dict[str, Any]:
        hidden_paths = self._read_hidden_paths()
        devices = self._read_gamepads()
        for device in devices:
            device["hidden_count"] = sum(
                path.casefold() in hidden_paths for path in device["paths"]
            )
            device["hidden"] = device["hidden_count"] == len(device["paths"]) and bool(device["paths"])
        app_path = ntpath.normcase(ntpath.normpath(str(self.app_path)))
        return {
            "devices": devices,
            "hidden_paths": hidden_paths,
            "enabled": self._read_cloak_state(),
            "app_allowed": app_path in self._read_allowed_apps(),
            "app_path": str(self.app_path),
        }

    def set_device_hidden(self, paths: list[str], hidden: bool, cloak_active: bool) -> None:
        if hidden and cloak_active:
            app_path = ntpath.normcase(ntpath.normpath(str(self.app_path)))
            if app_path not in self._read_allowed_apps():
                raise HidHideError(
                    "Autoriza GDM antes de ocultar mandos con el ocultamiento activo; "
                    "de lo contrario GDM podría perder acceso al mando."
                )
        command = "dev-hide" if hidden else "dev-unhide"
        for path in paths:
            self._run(command, path)

    def allow_current_app(self) -> None:
        app_path = ntpath.normcase(ntpath.normpath(str(self.app_path)))
        if app_path not in self._read_allowed_apps():
            self._run("app-reg", str(self.app_path))

    def set_active(self, enabled: bool) -> None:
        if enabled:
            app_path = ntpath.normcase(ntpath.normpath(str(self.app_path)))
            if app_path not in self._read_allowed_apps():
                raise HidHideError("Autoriza GDM antes de activar el ocultamiento.")
        self._run("cloak-on" if enabled else "cloak-off")
