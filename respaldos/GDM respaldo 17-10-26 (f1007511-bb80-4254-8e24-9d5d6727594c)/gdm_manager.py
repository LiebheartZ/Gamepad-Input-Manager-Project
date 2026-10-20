"""GDM: live gamepad diagnostics and a minimal visualizer."""

from __future__ import annotations

import ctypes
from datetime import datetime
from functools import lru_cache
import json
import os
import platform
import queue
import subprocess
import threading
import time
from pathlib import Path
from typing import Optional

import pygame
from pygame._sdl2 import controller as sdl_controller
from pygame._sdl2.video import Window

from bluetooth_manager import BluetoothService, pairing_status_text
from dualsense_led import is_dualsense, set_lightbar_color
from hidhide_manager import HidHideCLIService, HidHideError
from startup_manager import StartupShortcutError, set_windows_startup
from virtual_gamepad import (
    BUTTONS as REMAP_BUTTONS,
    BUTTON_IDS as REMAP_BUTTON_IDS,
    OUTPUT_TYPES,
    PROFILE_NAMES,
    VirtualGamepadBridge,
    button_target_labels,
    default_profile_mappings,
    default_profile_outputs,
)


APP_NAME = "GDM Gamepad Device Manager"
APP_VERSION = "0.1.0"
APP_ICON_PATH = Path(__file__).with_name("gdm_icon.png")
PLAYSTATION_LED_COLORS = {
    "blue": ("Azul", (0, 80, 255)),
    "red": ("Rojo", (255, 0, 0)),
    "green": ("Verde", (0, 220, 80)),
    "purple": ("Morado", (170, 0, 255)),
    "cyan": ("Cian", (0, 220, 255)),
    "white": ("Blanco", (255, 255, 255)),
}
WIDTH, HEIGHT = 1180, 800
MIN_WINDOW_WIDTH, MIN_WINDOW_HEIGHT = 800, 600
BG = (21, 25, 34)
PANEL = (31, 37, 49)
PANEL_LIGHT = (43, 51, 66)
TEXT = (235, 239, 246)
MUTED = (151, 163, 181)
ACCENT = (74, 170, 255)
ACTIVE = (74, 220, 147)
PAIRED_COLOR = (113, 164, 255)
VIRTUAL_COLOR = (190, 140, 255)
WARN = (255, 190, 82)
ERROR_COLOR = (255, 91, 91)
SUCCESS_COLOR = (74, 220, 147)
PAD_BODY = (20, 34, 59)
PAD_EDGE = (105, 126, 157)
SDL_CONTROLLER_BUTTON_TOUCHPAD = 20
SDL_BUTTONS = (
    ("A", pygame.CONTROLLER_BUTTON_A),
    ("B", pygame.CONTROLLER_BUTTON_B),
    ("X", pygame.CONTROLLER_BUTTON_X),
    ("Y", pygame.CONTROLLER_BUTTON_Y),
    ("Select", pygame.CONTROLLER_BUTTON_BACK),
    ("PS", pygame.CONTROLLER_BUTTON_GUIDE),
    ("Touchpad", SDL_CONTROLLER_BUTTON_TOUCHPAD),
    ("Start", pygame.CONTROLLER_BUTTON_START),
    ("Stick izq.", pygame.CONTROLLER_BUTTON_LEFTSTICK),
    ("Stick der.", pygame.CONTROLLER_BUTTON_RIGHTSTICK),
    ("L1", pygame.CONTROLLER_BUTTON_LEFTSHOULDER),
    ("R1", pygame.CONTROLLER_BUTTON_RIGHTSHOULDER),
    ("Abajo", pygame.CONTROLLER_BUTTON_DPAD_DOWN),
    ("Izquierda", pygame.CONTROLLER_BUTTON_DPAD_LEFT),
    ("Derecha", pygame.CONTROLLER_BUTTON_DPAD_RIGHT),
    ("Arriba", pygame.CONTROLLER_BUTTON_DPAD_UP),
)
SDL_AXES = (
    ("Stick izq. X", pygame.CONTROLLER_AXIS_LEFTX, False),
    ("Stick izq. Y", pygame.CONTROLLER_AXIS_LEFTY, False),
    ("Stick der. X", pygame.CONTROLLER_AXIS_RIGHTX, False),
    ("Stick der. Y", pygame.CONTROLLER_AXIS_RIGHTY, False),
    ("LT", pygame.CONTROLLER_AXIS_TRIGGERLEFT, True),
    ("RT", pygame.CONTROLLER_AXIS_TRIGGERRIGHT, True),
)


@lru_cache(maxsize=1)
def sdl_power_api():
    if platform.system() != "Windows":
        return None
    try:
        library = ctypes.WinDLL(str(Path(pygame.__file__).parent / "SDL2.dll"))
        library.SDL_JoystickFromInstanceID.argtypes = [ctypes.c_int32]
        library.SDL_JoystickFromInstanceID.restype = ctypes.c_void_p
        library.SDL_JoystickCurrentPowerLevel.argtypes = [ctypes.c_void_p]
        library.SDL_JoystickCurrentPowerLevel.restype = ctypes.c_int
        return library
    except (AttributeError, OSError):
        return None


def detect_transport(vendor_id: int, product_id: int) -> str:
    """Best-effort Windows PnP lookup; SDL itself does not expose the transport."""
    if platform.system() != "Windows" or not vendor_id or not product_id:
        return "No disponible"

    script = r"""
$ErrorActionPreference = 'Stop'
$pattern = ('VID_{0:X4}&PID_{1:X4}' -f $env:PAD_VID, $env:PAD_PID)
$devices = @(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match $pattern })
$transports = @()
foreach ($device in $devices) {
    $id = $device.InstanceId
    for ($depth = 0; $depth -lt 10 -and $id; $depth++) {
        if ($id -match '^(USB)\\') { $transports += 'USB'; break }
        if ($id -match '^(BTHENUM|BTHLEDEVICE)\\') { $transports += 'Bluetooth'; break }
        $parent = Get-PnpDeviceProperty -InstanceId $id -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue
        if ($null -eq $parent) { break }
        $id = $parent.Data
    }
}
$unique = @($transports | Select-Object -Unique)
if ($unique.Count -eq 1) { $unique[0] }
elseif ($unique.Count -gt 1) { 'Ambiguo' }
else { 'No detectado' }
"""
    env = dict(__import__("os").environ)
    env["PAD_VID"] = f"{vendor_id:04X}"
    env["PAD_PID"] = f"{product_id:04X}"
    try:
        result = subprocess.run(
            [
                "powershell.exe",
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                script,
            ],
            capture_output=True,
            text=True,
            timeout=8,
            env=env,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        return "Error de detección"
    if result.returncode != 0:
        return "Error de detección"
    value = result.stdout.strip()
    return value if value in {"USB", "Bluetooth", "Ambiguo", "No detectado"} else "No disponible"


def copy_to_clipboard(text: str) -> None:
    if platform.system() != "Windows":
        pygame.scrap.init()
        pygame.scrap.put(pygame.SCRAP_TEXT, text.encode("utf-8"))
        return

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    user32.OpenClipboard.argtypes = [ctypes.c_void_p]
    user32.OpenClipboard.restype = ctypes.c_int
    user32.EmptyClipboard.restype = ctypes.c_int
    user32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
    user32.SetClipboardData.restype = ctypes.c_void_p
    user32.CloseClipboard.restype = ctypes.c_int
    kernel32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
    kernel32.GlobalAlloc.restype = ctypes.c_void_p
    kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    kernel32.GlobalUnlock.restype = ctypes.c_int
    kernel32.GlobalFree.argtypes = [ctypes.c_void_p]
    kernel32.GlobalFree.restype = ctypes.c_void_p

    if not user32.OpenClipboard(None):
        raise ctypes.WinError(ctypes.get_last_error())
    memory = None
    transferred = False
    try:
        if not user32.EmptyClipboard():
            raise ctypes.WinError(ctypes.get_last_error())
        payload = (text + "\0").encode("utf-16-le")
        memory = kernel32.GlobalAlloc(0x0002, len(payload))
        if not memory:
            raise ctypes.WinError(ctypes.get_last_error())
        address = kernel32.GlobalLock(memory)
        if not address:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            ctypes.memmove(address, payload, len(payload))
        finally:
            kernel32.GlobalUnlock(memory)
        if not user32.SetClipboardData(13, memory):
            raise ctypes.WinError(ctypes.get_last_error())
        transferred = True
    finally:
        user32.CloseClipboard()
        if memory and not transferred:
            kernel32.GlobalFree(memory)


def settings_file_path() -> Path:
    if platform.system() == "Windows":
        config_root = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming"))
    else:
        config_root = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
    return config_root / "GDM" / "settings.json"


def legacy_settings_file_path() -> Path:
    if platform.system() == "Windows":
        config_root = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming"))
    else:
        config_root = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
    return config_root / "JoyCore Gamepad Manager" / "settings.json"


def enable_background_controller_input() -> Optional[str]:
    if platform.system() != "Windows":
        return None
    try:
        sdl = ctypes.WinDLL(str(Path(pygame.__file__).parent / "SDL2.dll"))
        set_hint = sdl.SDL_SetHint
        set_hint.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
        set_hint.restype = ctypes.c_int
        if not set_hint(b"SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", b"1"):
            return "SDL no aceptó la opción de leer mandos en segundo plano."
    except (AttributeError, OSError) as error:
        return f"No se pudo habilitar la lectura de mandos en segundo plano: {error}"
    return None


class GDMGamepadManager:
    def __init__(self, start_in_tray: bool = False, restore_event_handle=None) -> None:
        background_input_error = enable_background_controller_input()
        pygame.init()
        pygame.joystick.init()
        sdl_controller.init()
        icon_error = None
        app_icon = None
        if APP_ICON_PATH.is_file():
            try:
                icon = pygame.image.load(str(APP_ICON_PATH))
                app_icon = pygame.transform.smoothscale(icon, (64, 64))
            except pygame.error as error:
                icon_error = f"No se pudo cargar el icono de GDM: {error}"
        else:
            icon_error = f"No se encontró el archivo de icono: {APP_ICON_PATH}"
        self.clock = pygame.time.Clock()
        self.font = pygame.font.SysFont("segoeui", 18)
        self.small = pygame.font.SysFont("segoeui", 15)
        self.title_font = pygame.font.SysFont("segoeui", 25, bold=True)
        self.console_font = pygame.font.SysFont("consolas", 12)
        self.ui_scale = 1.0
        self._responsive_font_sizes = None
        self.joysticks: list[pygame.joystick.JoystickType] = []
        self.joystick_device_indices: dict[int, int] = {}
        self.controllers: dict[int, sdl_controller.Controller] = {}
        self.selected = 0
        self.transport = "—"
        self.transport_key: Optional[tuple[int, int]] = None
        self.transport_check_key: Optional[tuple[int, int]] = None
        self.transport_lock = threading.Lock()
        self.transport_thread: Optional[threading.Thread] = None
        self.dashboard_transport_values: dict[tuple[int, int], str] = {}
        self.dashboard_transport_thread: Optional[threading.Thread] = None
        self.dashboard_transport_generation = 0
        self._device_refresh_deadline: Optional[float] = None
        self.bluetooth_auto_pair = True
        self.bluetooth_gamepads_only = True
        self.launch_at_startup = False
        self.start_minimized_in_tray = False
        self.escape_prompt_enabled = True
        self.remember_escape_choice = False
        self.escape_action = "close"
        self.window_close_action = "minimize"
        self.escape_dialog_open = False
        self.escape_dialog_remember = False
        self.escape_dialog_choice = "close"
        self.escape_dialog_rects: dict[str, pygame.Rect] = {}
        self.tray_icon_mode = "auto"
        self.tray_icon_color = "white"
        self._tray_theme_warning_logged = False
        self.startup_busy = False
        self.startup_events: queue.Queue[tuple[bool, str, bool, str]] = queue.Queue()
        self.disconnect_on_home_hold = True
        self.home_hold_action = "disconnect"
        self.pending_home_action: Optional[str] = None
        self.pending_home_joystick = None
        self.pending_home_deadline = 0.0
        self.bluetooth_selected_id: Optional[str] = None
        self.bluetooth_scroll = 0
        self.active_tab = "devices"
        self.startup_tab = "devices"
        self.options_section = "general"
        self.nav_rects: dict[str, pygame.Rect] = {}
        self.devices_rects: dict[str, pygame.Rect] = {}
        self.devices_scroll = 0
        self.bluetooth_rects: dict[str, pygame.Rect] = {}
        self.options_rects: dict[str, pygame.Rect] = {}
        self.bluetooth_row_rects: list[tuple[pygame.Rect, str]] = []
        self.console_lines: list[tuple[str, str, str]] = []
        self.console_visible = False
        self.console_expanded = False
        self.show_console_button = True
        self.console_rects: dict[str, pygame.Rect] = {}
        self.console_scroll = 0
        self.home_hold_started: Optional[int] = None
        self.home_hold_triggered = False
        self.window_size = (WIDTH, HEIGHT)
        self._window_resize_deadline: Optional[int] = None
        self.tray_events: queue.Queue[object] = queue.Queue()
        self.restore_event_handle = restore_event_handle
        self.restore_signal_stop = threading.Event()
        self.restore_signal_thread: Optional[threading.Thread] = None
        self.tray_icon = None
        self.window_in_tray = False
        self.remap_rects: dict[str, pygame.Rect] = {}
        self.remap_profile = "Genérico"
        self._remap_profile_user_selected = False
        self.virtual_output_enabled = False
        self.virtual_output_source_id: Optional[int] = None
        self.virtual_output_auto_start = False
        self.playstation_led_color: Optional[str] = None
        self.playstation_led_status = "Selecciona un color para el mando físico."
        self._playstation_led_last_attempt: Optional[tuple[int, str]] = None
        self.virtual_profile_outputs = default_profile_outputs()
        self.virtual_profile_mappings = default_profile_mappings()
        self.hidhide_service = HidHideCLIService()
        self.hidhide_events: queue.Queue = queue.Queue()
        self.hidhide_busy = False
        self.hidhide_status = "Pulsa «Actualizar» para consultar HidHide."
        self.hidhide_devices: list[dict] = []
        self.hidhide_devices_loaded = False
        self.hidhide_type_overrides: dict[str, str] = {}
        self.hidhide_enabled = False
        self.hidhide_app_allowed = False
        self.hidhide_app_path = str(self.hidhide_service.app_path)
        self.hidhide_selected_id: Optional[str] = None
        self.hidhide_scroll = 0
        self.hidhide_rects: dict[str, pygame.Rect] = {}
        self.settings_path = settings_file_path()
        self.load_settings()
        self.screen = pygame.display.set_mode(self.window_size, pygame.RESIZABLE)
        self.window = Window.from_display_module()
        window_constraint_error = None
        try:
            self.window.minimum_size = (MIN_WINDOW_WIDTH, MIN_WINDOW_HEIGHT)
        except (pygame.error, AttributeError) as error:
            window_constraint_error = f"No se pudo establecer el tamaño mínimo de la ventana: {error}"
        if app_icon is not None:
            try:
                pygame.display.set_icon(app_icon)
            except pygame.error as error:
                icon_error = f"No se pudo aplicar el icono de GDM: {error}"
        pygame.display.set_caption(APP_NAME)
        self.active_tab = self.startup_tab
        self.bluetooth = BluetoothService()
        self.bluetooth.set_auto_pair(self.bluetooth_auto_pair)
        self.bluetooth_devices: dict[str, dict] = {}
        self.bluetooth_status = "Iniciando búsqueda Bluetooth…"
        self.bluetooth_radio_state = "unknown"
        self.virtual_bridge = VirtualGamepadBridge()
        self.virtual_output_status = self.virtual_bridge.status
        self.running = True
        if window_constraint_error:
            self.write_console("ERROR", window_constraint_error)
        if icon_error:
            self.write_console("ERROR", icon_error)
        if background_input_error:
            self.write_console("ERROR", background_input_error)
        elif platform.system() == "Windows":
            self.write_console("INFO", "Lectura de mandos habilitada también con GDM en segundo plano.")
        self.write_console("SUCCESS", "GDM iniciado. Consola de diagnóstico activa.")
        self.start_system_tray()
        if self.restore_event_handle is not None:
            self.restore_signal_thread = threading.Thread(
                target=self.wait_for_restore_signal,
                name="GDMRestoreSignal",
                daemon=True,
            )
            self.restore_signal_thread.start()
        if start_in_tray:
            if self.tray_icon is not None:
                self.minimize_to_tray()
            else:
                self.write_console("ERROR", "No se pudo iniciar minimizado porque el icono de bandeja no está disponible.")
        self.refresh_devices()
        if self.active_tab == "hidhide" or (
            self.active_tab == "devices" and self.hidhide_service.cli_path is not None
        ):
            self.start_hidhide_task("refresh")

    def load_settings(self) -> None:
        source_path = self.settings_path
        if not source_path.exists():
            legacy_path = legacy_settings_file_path()
            if legacy_path.exists():
                source_path = legacy_path
        if not source_path.exists():
            return
        try:
            with source_path.open("r", encoding="utf-8") as settings_file:
                settings = json.load(settings_file)
        except (OSError, json.JSONDecodeError) as error:
            self.write_console("ERROR", f"No se pudo leer la configuración: {error}")
            return
        if not isinstance(settings, dict):
            self.write_console("ERROR", "El archivo de configuración debe contener un objeto JSON.")
            return

        setting_attributes = {
            "auto_pair": "bluetooth_auto_pair",
            "gamepads_only": "bluetooth_gamepads_only",
            "launch_at_startup": "launch_at_startup",
            "start_minimized_in_tray": "start_minimized_in_tray",
            "escape_prompt_enabled": "escape_prompt_enabled",
            "remember_escape_choice": "remember_escape_choice",
            "disconnect_on_home_hold": "disconnect_on_home_hold",
            "console_visible": "console_visible",
            "console_expanded": "console_expanded",
            "show_console_button": "show_console_button",
        }
        for key, attribute in setting_attributes.items():
            value = settings.get(key)
            if isinstance(value, bool):
                setattr(self, attribute, value)
        tray_icon_mode = settings.get("tray_icon_mode")
        if isinstance(tray_icon_mode, str) and tray_icon_mode in {"auto", "white", "black"}:
            self.tray_icon_mode = tray_icon_mode
        home_hold_action = settings.get("home_hold_action")
        if isinstance(home_hold_action, str) and home_hold_action in {"disconnect", "unpair"}:
            self.home_hold_action = home_hold_action
        escape_action = settings.get("escape_action")
        if isinstance(escape_action, str) and escape_action in {"close", "minimize"}:
            self.escape_action = escape_action
        window_close_action = settings.get("window_close_action")
        if isinstance(window_close_action, str) and window_close_action in {"close", "minimize"}:
            self.window_close_action = window_close_action
        startup_tab = settings.get("startup_tab")
        if isinstance(startup_tab, str) and startup_tab in {"devices", "tester", "bluetooth", "remap", "hidhide", "options", "about"}:
            self.startup_tab = startup_tab
        virtual_output_enabled = settings.get("virtual_output_enabled")
        if isinstance(virtual_output_enabled, bool):
            self.virtual_output_enabled = virtual_output_enabled
        virtual_output_auto_start = settings.get("virtual_output_auto_start")
        if isinstance(virtual_output_auto_start, bool):
            self.virtual_output_auto_start = virtual_output_auto_start
        playstation_led_color = settings.get("playstation_led_color")
        if playstation_led_color is None or (
            isinstance(playstation_led_color, str)
            and playstation_led_color in PLAYSTATION_LED_COLORS
        ):
            self.playstation_led_color = playstation_led_color
        profile_outputs = settings.get("virtual_profile_outputs")
        if isinstance(profile_outputs, dict):
            for profile in PROFILE_NAMES:
                value = profile_outputs.get(profile)
                if isinstance(value, str) and value in OUTPUT_TYPES:
                    self.virtual_profile_outputs[profile] = value
        profile_mappings = settings.get("virtual_profile_mappings")
        if isinstance(profile_mappings, dict):
            valid_targets = set(REMAP_BUTTON_IDS)
            for profile in PROFILE_NAMES:
                profile_mapping = profile_mappings.get(profile)
                if not isinstance(profile_mapping, dict):
                    continue
                for button_id in REMAP_BUTTON_IDS:
                    target = profile_mapping.get(button_id)
                    if target is None or (isinstance(target, str) and target in valid_targets):
                        self.virtual_profile_mappings[profile][button_id] = target
        device_type_overrides = settings.get("hidhide_device_type_overrides")
        if isinstance(device_type_overrides, dict):
            self.hidhide_type_overrides = {
                str(identifier).casefold(): device_type
                for identifier, device_type in device_type_overrides.items()
                if isinstance(identifier, str)
                and isinstance(device_type, str)
                and device_type in {"physical", "virtual"}
            }
        window_width = settings.get("window_width")
        window_height = settings.get("window_height")
        if isinstance(window_width, int) and not isinstance(window_width, bool):
            self.window_size = (max(MIN_WINDOW_WIDTH, window_width), self.window_size[1])
        if isinstance(window_height, int) and not isinstance(window_height, bool):
            self.window_size = (self.window_size[0], max(MIN_WINDOW_HEIGHT, window_height))

    def save_settings(self) -> bool:
        settings = {
            "version": 1,
            "auto_pair": self.bluetooth_auto_pair,
            "gamepads_only": self.bluetooth_gamepads_only,
            "launch_at_startup": self.launch_at_startup,
            "start_minimized_in_tray": self.start_minimized_in_tray,
            "escape_prompt_enabled": self.escape_prompt_enabled,
            "remember_escape_choice": self.remember_escape_choice,
            "escape_action": self.escape_action,
            "window_close_action": self.window_close_action,
            "tray_icon_mode": self.tray_icon_mode,
            "disconnect_on_home_hold": self.disconnect_on_home_hold,
            "home_hold_action": self.home_hold_action,
            "startup_tab": self.startup_tab,
            "console_visible": self.console_visible,
            "console_expanded": self.console_expanded,
            "show_console_button": self.show_console_button,
            "window_width": self.window_size[0],
            "window_height": self.window_size[1],
            "virtual_output_enabled": self.virtual_output_enabled,
            "virtual_output_auto_start": self.virtual_output_auto_start,
            "playstation_led_color": self.playstation_led_color,
            "virtual_profile_outputs": self.virtual_profile_outputs,
            "virtual_profile_mappings": self.virtual_profile_mappings,
            "hidhide_device_type_overrides": self.hidhide_type_overrides,
        }
        temporary_path = self.settings_path.with_suffix(".tmp")
        try:
            self.settings_path.parent.mkdir(parents=True, exist_ok=True)
            with temporary_path.open("w", encoding="utf-8") as settings_file:
                json.dump(settings, settings_file, indent=2)
                settings_file.write("\n")
            temporary_path.replace(self.settings_path)
            return True
        except OSError as error:
            self.write_console("ERROR", f"No se pudo guardar la configuración: {error}")
            return False

    def save_window_size_if_due(self) -> None:
        if self._window_resize_deadline is None or time.monotonic() < self._window_resize_deadline:
            return
        self._window_resize_deadline = None
        self.save_settings()

    def refresh_devices(self) -> None:
        self.home_hold_started = None
        self.home_hold_triggered = False
        with self.transport_lock:
            self.dashboard_transport_generation += 1
            self.dashboard_transport_values.clear()
        self.devices_scroll = 0
        previous_id = None
        source_id = self.virtual_output_source_id
        previous_device_ids = set()
        for old_joystick in self.joysticks:
            try:
                previous_device_ids.add(old_joystick.get_instance_id())
            except pygame.error:
                continue
        self._playstation_led_last_attempt = None
        if self.joysticks and self.selected < len(self.joysticks):
            try:
                previous_id = self.joysticks[self.selected].get_instance_id()
            except pygame.error:
                pass
        if source_id is None:
            source_id = previous_id
        for controller in self.controllers.values():
            try:
                controller.quit()
            except pygame.error as error:
                self.write_console("WARN", f"No se pudo cerrar el acceso anterior al mando: {error}")
        for joystick in self.joysticks:
            try:
                joystick.quit()
            except pygame.error as error:
                self.write_console("WARN", f"No se pudo cerrar el dispositivo anterior: {error}")
        self.controllers.clear()
        self.joysticks = []
        self.joystick_device_indices.clear()
        try:
            device_count = pygame.joystick.get_count()
        except pygame.error as error:
            self.write_console("ERROR", f"No se pudo enumerar los mandos SDL: {error}")
            self._device_refresh_deadline = None
            return
        for index in range(device_count):
            try:
                joystick = pygame.joystick.Joystick(index)
                if not joystick.get_init():
                    joystick.init()
                instance_id = joystick.get_instance_id()
                self.joysticks.append(joystick)
                self.joystick_device_indices[instance_id] = index
                if sdl_controller.is_controller(index):
                    try:
                        self.controllers[instance_id] = sdl_controller.Controller(index)
                    except pygame.error as error:
                        self.write_console("WARN", f"No se pudo abrir el mando SDL normalizado: {error}")
            except pygame.error as error:
                self.write_console("WARN", f"No se pudo abrir el dispositivo SDL {index}: {error}")
        self.selected = 0
        if previous_id is not None:
            for index, joystick in enumerate(self.joysticks):
                try:
                    if joystick.get_instance_id() == previous_id:
                        self.selected = index
                        break
                except pygame.error:
                    continue
        source_disconnected = (
            source_id is not None
            and not any(
                joystick.get_instance_id() == source_id
                for joystick in self.joysticks
                if joystick.get_init()
            )
        )
        if source_disconnected and self.virtual_output_enabled:
            self.virtual_output_enabled = False
            self.virtual_output_source_id = None
            self.virtual_bridge.close()
            self.virtual_output_status = "El mando físico se desconectó; salida virtual detenida."
            self.save_settings()
            self.write_console(
                "INFO",
                "El mando de origen se desconectó; se detuvo la salida virtual para evitar reusar otro dispositivo.",
            )
        elif self.virtual_output_auto_start and not self.virtual_output_enabled and self.joysticks and (
            not previous_device_ids or any(
                joystick.get_instance_id() not in previous_device_ids
                for joystick in self.joysticks
                if joystick.get_init()
            )
        ):
            new_joysticks = [
                joystick
                for joystick in self.joysticks
                if not previous_device_ids
                or joystick.get_instance_id() not in previous_device_ids
            ]
            source = self.find_physical_gamepad(new_joysticks)
            if source is not None:
                try:
                    self.selected = self.joysticks.index(source)
                    self.virtual_output_source_id = source.get_instance_id()
                    self.virtual_output_enabled = True
                    self.save_settings()
                    self.write_console("INFO", "Mando conectado; iniciando automáticamente la salida virtual.")
                except (ValueError, pygame.error) as error:
                    self.write_console("WARN", f"No se pudo iniciar automáticamente la salida virtual: {error}")
        if not self.joysticks:
            self.transport = "—"
            self.transport_key = None
        elif not self._remap_profile_user_selected:
            joystick = self.joysticks[self.selected]
            try:
                self.remap_profile = self.model_name(
                    joystick.get_name(),
                    joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0,
                )
            except pygame.error as error:
                self.write_console("WARN", f"No se pudo identificar el mando SDL conectado: {error}")
        self._device_refresh_deadline = None

    def find_physical_gamepad(self, candidates=None):
        devices = candidates if candidates is not None else self.joysticks
        for joystick in devices:
            try:
                if not joystick.get_init() or self.is_virtual_gamepad(joystick):
                    continue
            except pygame.error:
                continue
            return joystick
        return None

    def is_virtual_gamepad(self, joystick) -> bool:
        try:
            name = joystick.get_name() or ""
            vendor_id = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
            product_id = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
        except pygame.error:
            return True
        normalized_name = self.normalize_device_name(name)
        return (
            self.hidhide_type_for_gamepad(vendor_id, product_id, name) == "virtual"
            or any(marker in normalized_name for marker in ("virtual", "vigem"))
        )

    @property
    def joystick(self) -> Optional[pygame.joystick.JoystickType]:
        if not self.joysticks:
            return None
        return self.joysticks[self.selected]

    def start_transport_detection(self, joystick: pygame.joystick.JoystickType) -> None:
        vendor = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
        product = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
        key = (vendor, product)
        if self.transport_key != key:
            self.transport_key = key
            self.transport = "Consultando…" if platform.system() == "Windows" else "No disponible"
            self.transport_check_key = None
        if (
            self.transport_check_key != key
            and (self.transport_thread is None or not self.transport_thread.is_alive())
        ):
            self.transport_check_key = key

            def worker() -> None:
                value = detect_transport(*key)
                with self.transport_lock:
                    if self.transport_key == key:
                        self.transport = value

            self.transport_thread = threading.Thread(target=worker, daemon=True)
            self.transport_thread.start()

    def model_name(self, name: str, vendor_id: int = 0) -> str:
        lowered = name.lower()
        if vendor_id == 0x054C or any(part in lowered for part in ("playstation", "dualshock", "dualsense", "ps3", "ps4", "ps5")):
            return "PlayStation"
        if vendor_id == 0x045E or any(part in lowered for part in ("xbox", "x-box")):
            return "Xbox"
        return "Genérico"

    def start_dashboard_transport_detection(self) -> None:
        device_keys = set()
        for joystick in self.joysticks:
            try:
                vendor = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
                product = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
            except pygame.error:
                continue
            if vendor and product:
                device_keys.add((vendor, product))

        if platform.system() != "Windows":
            with self.transport_lock:
                for key in device_keys:
                    self.dashboard_transport_values[key] = "No disponible"
            return

        with self.transport_lock:
            pending = device_keys - self.dashboard_transport_values.keys()
            if not pending or (
                self.dashboard_transport_thread is not None
                and self.dashboard_transport_thread.is_alive()
            ):
                return
            generation = self.dashboard_transport_generation
            for key in pending:
                self.dashboard_transport_values[key] = "Consultando…"

        def worker(keys: set[tuple[int, int]], current_generation: int) -> None:
            results = {key: detect_transport(*key) for key in keys}
            with self.transport_lock:
                if current_generation == self.dashboard_transport_generation:
                    self.dashboard_transport_values.update(results)

        self.dashboard_transport_thread = threading.Thread(
            target=worker,
            args=(pending, generation),
            daemon=True,
        )
        self.dashboard_transport_thread.start()

    def controller_for(self, joystick):
        if joystick is None:
            return None
        return self.controllers.get(joystick.get_instance_id())

    def playstation_led_target(self):
        if self.virtual_output_enabled:
            source_id = self.virtual_output_source_id
            if source_id is None:
                return None
            joystick = None
            for device in self.joysticks:
                try:
                    if device.get_init() and device.get_instance_id() == source_id:
                        joystick = device
                        break
                except pygame.error:
                    continue
        else:
            joystick = self.joystick
        if joystick is None or self.is_virtual_gamepad(joystick):
            return None
        return joystick

    def playstation_led_availability(self, joystick) -> tuple[bool, str]:
        if joystick is None:
            if self.virtual_output_enabled and self.virtual_output_source_id is not None:
                return False, "No disponible: origen virtual desconectado."
            return False, "Solo disponible en mandos PlayStation físicos."
        try:
            model = self.model_name(
                joystick.get_name() or "",
                joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0,
            )
        except pygame.error:
            return False, "No se pudo identificar el mando."
        if model == "Xbox":
            return False, "No disponible en mandos Xbox."
        if model != "PlayStation":
            return False, "No disponible en mandos genéricos."
        try:
            name = joystick.get_name() or ""
            vendor_id = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
            product_id = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
            if is_dualsense(name, product_id):
                if platform.system() != "Windows":
                    return False, "Control directo del LED DualSense disponible solo en Windows."
                if joystick.get_instance_id() not in self.joystick_device_indices:
                    return False, "No se pudo identificar la interfaz física del DualSense."
                try:
                    import hid  # noqa: F401
                except ImportError:
                    return False, "Falta hidapi; instala las dependencias actualizadas de GDM."
                return True, ""
            if product_id not in {0x05C4, 0x09CC} and not any(
                marker in name.casefold() for marker in ("dualshock 4", "dualsense")
            ):
                return False, "El modelo PlayStation no es compatible con el cambio de color."
            controller = self.controller_for(joystick)
        except pygame.error:
            return False, "No se pudo identificar el mando."
        if controller is None or not hasattr(controller, "set_led"):
            return False, "El mando no expone control LED por SDL."
        return True, ""

    def update_playstation_led(self) -> None:
        if self.playstation_led_color not in PLAYSTATION_LED_COLORS:
            return
        joystick = self.playstation_led_target()
        available, message = self.playstation_led_availability(joystick)
        if not available:
            self.playstation_led_status = message
            return
        try:
            instance_id = joystick.get_instance_id()
            color_key = self.playstation_led_color
            attempt = (instance_id, color_key)
            if self._playstation_led_last_attempt == attempt:
                return
            self._playstation_led_last_attempt = attempt
            _label, rgb = PLAYSTATION_LED_COLORS[color_key]
            name = joystick.get_name() or ""
            product_id = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
            if is_dualsense(name, product_id):
                device_index = self.joystick_device_indices.get(instance_id)
                if device_index is None:
                    raise RuntimeError("No se pudo identificar la interfaz física del DualSense.")
                vendor_id = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
                written, report_size = set_lightbar_color(device_index, vendor_id, product_id, rgb)
                applied = True
                if written < report_size:
                    self.playstation_led_status = f"LED cambiado; informe aceptado parcialmente ({written} bytes)."
                    self.write_console(
                        "WARN",
                        f"El mando aplicó el color; HID aceptó {written} de {report_size} bytes.",
                    )
                    return
            else:
                controller = self.controller_for(joystick)
                applied = bool(controller and controller.set_led(pygame.Color(*rgb)))
            if applied:
                self.playstation_led_status = f"LED cambiado a {_label}."
                self.write_console("SUCCESS", f"LED del mando PlayStation físico cambiado a {_label}.")
            else:
                self.playstation_led_status = "El mando no permite controlar sus LED."
                self.write_console("WARN", self.playstation_led_status)
        except (pygame.error, AttributeError, ImportError, OSError, RuntimeError) as error:
            self.playstation_led_status = f"No se pudo cambiar el LED: {error}"
            self.write_console("ERROR", self.playstation_led_status)

    @staticmethod
    def mapped_axis(controller, axis: int, trigger: bool = False) -> float:
        if controller is None:
            return 0.0
        value = controller.get_axis(axis)
        if isinstance(value, float) and -1.0 <= value <= 1.0:
            normalized = value
        else:
            normalized = float(value) / 32767.0
        return max(0.0, min(1.0, normalized)) if trigger else max(-1.0, min(1.0, normalized))

    @staticmethod
    def mapped_button(controller, button: int) -> bool:
        return controller is not None and bool(controller.get_button(button))

    def draw_text(self, text: str, x: int, y: int, color=TEXT, font=None) -> None:
        self.screen.blit((font or self.font).render(text, True, color), (x, y))

    def update_ui_scale(self, width: int, height: int) -> None:
        self.ui_scale = max(0.75, min(1.25, width / WIDTH, height / HEIGHT))
        font_sizes = (
            max(14, round(18 * self.ui_scale)),
            max(12, round(15 * self.ui_scale)),
            max(20, round(25 * self.ui_scale)),
            max(10, round(12 * self.ui_scale)),
        )
        if font_sizes == self._responsive_font_sizes:
            return
        self.font = pygame.font.SysFont("segoeui", font_sizes[0])
        self.small = pygame.font.SysFont("segoeui", font_sizes[1])
        self.title_font = pygame.font.SysFont("segoeui", font_sizes[2], bold=True)
        self.console_font = pygame.font.SysFont("consolas", font_sizes[3])
        self._responsive_font_sizes = font_sizes

    def draw_text_fitted(self, text: str, x: int, y: int, max_width: int, color=TEXT, font=None) -> None:
        selected_font = font or self.font
        rendered = selected_font.render(text, True, color)
        if rendered.get_width() <= max_width:
            self.screen.blit(rendered, (x, y))
            return
        ellipsis = "…"
        while text and selected_font.size(text + ellipsis)[0] > max_width:
            text = text[:-1]
        self.draw_text(text + ellipsis if text else ellipsis, x, y, color, selected_font)

    def draw_panel(self, rect: pygame.Rect, heading: str) -> None:
        pygame.draw.rect(self.screen, PANEL, rect, border_radius=14)
        self.draw_text(heading, rect.x + 20, rect.y + 15, TEXT, self.title_font)

    def draw_navigation(self, width: int) -> None:
        tabs = (
            ("devices", "Mandos"),
            ("tester", "Gamepad Tester"),
            ("bluetooth", "Bluetooth"),
            ("remap", "Remapeo"),
            ("hidhide", "HidHide"),
            ("options", "Configuración"),
            ("about", "Acerca de"),
        )
        keys = tuple(key for key, _ in tabs)
        margin = 22
        gap = max(6, round(10 * self.ui_scale))
        tabs_width = width - margin * 2
        button_width = (tabs_width - gap * (len(keys) - 1)) // len(keys)
        button_height = round(42 * self.ui_scale)
        rail = pygame.Rect(margin, 53, tabs_width, 52)
        pygame.draw.rect(self.screen, PANEL, rail, border_radius=10)
        pygame.draw.line(
            self.screen,
            PANEL_LIGHT,
            (rail.x + 5, rail.bottom - 1),
            (rail.right - 5, rail.bottom - 1),
            1,
        )
        self.nav_rects = {}
        for index, (key, _label) in enumerate(tabs):
            selected = self.active_tab == key
            button = pygame.Rect(
                margin + index * (button_width + gap),
                rail.y if selected else rail.y + 6,
                button_width,
                rail.height if selected else rail.height - 6,
            )
            self.nav_rects[key] = button
            fill = PANEL if selected else BG
            border = ACCENT if selected else PANEL_LIGHT
            radius = 9
            pygame.draw.rect(self.screen, fill, button, border_radius=radius)
            pygame.draw.rect(self.screen, border, button, 1, border_radius=radius)
            pygame.draw.rect(
                self.screen,
                fill,
                (button.x + 1, button.bottom - radius, button.width - 2, radius - 1),
            )
            pygame.draw.line(self.screen, border, (button.left, button.y + radius), (button.left, button.bottom - 1), 1)
            pygame.draw.line(self.screen, border, (button.right - 1, button.y + radius), (button.right - 1, button.bottom - 1), 1)

        for key, label in tabs:
            rect = self.nav_rects[key]
            selected = self.active_tab == key
            text = self.small.render(label, True, ACCENT if selected else MUTED)
            self.screen.blit(text, text.get_rect(center=rect.center))

    def draw_devices_page(self, rect: pygame.Rect) -> None:
        self.draw_panel(rect, "Mandos conectados")
        visible_joysticks = self.dashboard_joysticks()
        count = len(visible_joysticks)
        self.draw_text(
            f"{count} {'mando conectado' if count == 1 else 'mandos conectados'}",
            rect.x + 22,
            rect.y + 57,
            MUTED,
            self.small,
        )

        button_width = min(round(220 * self.ui_scale), rect.width - 44)
        button = pygame.Rect(
            rect.right - 22 - button_width,
            rect.y + 47,
            button_width,
            round(36 * self.ui_scale),
        )
        self.devices_rects = {"windows_panel": button}
        self.draw_action_button(
            button,
            "Probar con Windows",
            platform.system() == "Windows",
        )

        list_top = rect.y + 100
        row_height = max(68, round(78 * self.ui_scale))
        visible_rows = max(1, (rect.bottom - list_top - 44) // row_height)
        max_scroll = max(0, count - visible_rows)
        self.devices_scroll = min(self.devices_scroll, max_scroll)
        if not self.joysticks:
            self.draw_text(
                "Conecta un mando por USB o Bluetooth para verlo aquí.",
                rect.x + 22,
                list_top + 12,
                MUTED,
                self.font,
            )
            return

        self.start_dashboard_transport_detection()
        with self.transport_lock:
            transport_values = dict(self.dashboard_transport_values)
        self.devices_rects["list"] = pygame.Rect(
            rect.x + 18,
            list_top,
            rect.width - 36,
            visible_rows * row_height,
        )
        for row_index, joystick in enumerate(
            visible_joysticks[self.devices_scroll:self.devices_scroll + visible_rows],
            start=self.devices_scroll,
        ):
            row = pygame.Rect(
                rect.x + 18,
                list_top + (row_index - self.devices_scroll) * row_height,
                rect.width - 36,
                row_height - 8,
            )
            pygame.draw.rect(self.screen, PANEL_LIGHT, row, border_radius=10)
            try:
                name = joystick.get_name() or "Mando sin nombre"
                vendor_id = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
                product_id = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
                model = self.model_name(name, vendor_id)
                battery = self.battery_text(joystick)
                transport = transport_values.get((vendor_id, product_id), "No disponible")
                device_type = self.gamepad_type_for_display(
                    vendor_id,
                    product_id,
                    name,
                    joystick.get_instance_id(),
                )
            except pygame.error as error:
                self.write_console("WARN", f"No se pudieron leer los datos de un mando conectado: {error}")
                name, model, battery, transport, device_type = (
                    "Mando no disponible",
                    "Genérico",
                    "no disponible",
                    "No disponible",
                    "unknown",
                )

            type_labels = {
                "virtual": ("MANDO VIRTUAL", VIRTUAL_COLOR),
                "physical": ("MANDO FÍSICO", PAIRED_COLOR),
                "unknown": ("TIPO NO IDENTIFICADO", MUTED),
            }
            device_label, device_color = type_labels[device_type]
            self.draw_text_fitted(
                name,
                row.x + 16,
                row.y + 10,
                row.width - 32,
                TEXT,
                self.font,
            )
            detail_y = row.y + round(40 * self.ui_scale)
            self.draw_text_fitted(
                device_label.title(),
                row.x + 16,
                detail_y,
                row.width // 3 - 16,
                device_color,
                self.small,
            )
            model_surface = self.small.render(model, True, ACCENT)
            model_rect = model_surface.get_rect(
                right=row.right - 16,
                centery=detail_y + self.small.get_height() // 2,
            )
            self.screen.blit(model_surface, model_rect)
            detail_left = row.x + 16 + row.width // 3
            detail_right = model_rect.left - 12
            detail_width = max(0, detail_right - detail_left)
            self.draw_text_fitted(
                f"Conexión: {transport}",
                detail_left,
                detail_y,
                detail_width // 2,
                MUTED,
                self.small,
            )
            self.draw_text_fitted(
                f"Batería: {battery}",
                detail_left + detail_width // 2,
                detail_y,
                detail_width - detail_width // 2,
                MUTED,
                self.small,
            )

        if max_scroll:
            self.draw_text(
                f"Usa la rueda del ratón para ver los demás mandos ({self.devices_scroll + 1}-{min(count, self.devices_scroll + visible_rows)} de {count}).",
                rect.x + 22,
                rect.bottom - 25,
                MUTED,
                self.small,
            )

    def gamepad_type_for_display(
        self,
        vendor_id: int,
        product_id: int,
        name: str,
        instance_id: Optional[int] = None,
    ) -> str:
        device_type = self.hidhide_type_for_gamepad(vendor_id, product_id, name)
        if device_type != "unknown":
            return device_type

        normalized_name = self.normalize_device_name(name)
        if self.name_indicates_virtual_gamepad(normalized_name):
            return "virtual"
        if self.is_active_virtual_gamepad_output(instance_id):
            return "virtual"
        if self.is_xbox_360_name(normalized_name) and self.has_xbox_360_windows_alias(vendor_id, product_id):
            return "virtual"
        return "physical"

    def is_active_virtual_gamepad_output(self, instance_id: Optional[int]) -> bool:
        if (
            not self.virtual_output_enabled
            or self.virtual_output_source_id is None
            or instance_id is None
            or instance_id == self.virtual_output_source_id
        ):
            return False
        source = next(
            (
                joystick
                for joystick in self.joysticks
                if joystick.get_instance_id() == self.virtual_output_source_id
            ),
            None,
        )
        candidate = next(
            (
                joystick
                for joystick in self.joysticks
                if joystick.get_instance_id() == instance_id
            ),
            None,
        )
        if source is None or candidate is None:
            return False
        source_model = self.model_name(
            source.get_name() or "",
            source.get_vendor_id() if hasattr(source, "get_vendor_id") else 0,
        )
        output_type = self.virtual_profile_outputs.get(source_model)
        if output_type == "Xbox 360":
            return self.is_xbox_360_name(self.normalize_device_name(candidate.get_name() or ""))
        if output_type == "DualShock 4":
            if self.model_name(
                candidate.get_name() or "",
                candidate.get_vendor_id() if hasattr(candidate, "get_vendor_id") else 0,
            ) != "PlayStation":
                return False
            other_candidates = []
            for joystick in self.joysticks:
                try:
                    if joystick.get_instance_id() == self.virtual_output_source_id:
                        continue
                    if self.model_name(
                        joystick.get_name() or "",
                        joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0,
                    ) == "PlayStation":
                        other_candidates.append(joystick)
                except pygame.error:
                    continue
            return len(other_candidates) == 1 and other_candidates[0] is candidate
        return False

    @staticmethod
    def name_indicates_virtual_gamepad(normalized_name: str) -> bool:
        return (
            any(marker in normalized_name for marker in ("virtual", "vigem", "vjoy", "steamvirtual"))
            or (
                "xbox360" in normalized_name
                and ("windows" in normalized_name or "parawindows" in normalized_name)
            )
        )

    @staticmethod
    def is_xbox_360_name(normalized_name: str) -> bool:
        return "xbox360" in normalized_name and (
            "controller" in normalized_name or "controlador" in normalized_name
        )

    def has_xbox_360_windows_alias(self, vendor_id: int, product_id: int) -> bool:
        for joystick in self.joysticks:
            try:
                name = self.normalize_device_name(joystick.get_name() or "")
                alias_vendor = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
                alias_product = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
            except pygame.error:
                continue
            if not self.is_xbox_360_name(name) or not ("windows" in name or "parawindows" in name):
                continue
            if vendor_id and product_id and alias_vendor and alias_product:
                if (vendor_id, product_id) == (alias_vendor, alias_product):
                    return True
            elif not vendor_id or not product_id or not alias_vendor or not alias_product:
                return True
        return False

    def dashboard_joysticks(self):
        xbox_primaries: list[tuple[int, int, int]] = []
        xbox_windows_aliases: list[tuple[int, int, int]] = []
        for joystick in self.joysticks:
            try:
                name = self.normalize_device_name(joystick.get_name() or "")
                vendor = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
                product = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
                instance_id = joystick.get_instance_id()
            except pygame.error:
                continue
            if self.is_xbox_360_name(name):
                if "windows" in name or "parawindows" in name:
                    xbox_windows_aliases.append((vendor, product, instance_id))
                else:
                    xbox_primaries.append((vendor, product, instance_id))

        duplicate_alias_ids = {
            alias_id
            for alias_vendor, alias_product, alias_id in xbox_windows_aliases
            if any(
                (not alias_vendor or not primary_vendor or alias_vendor == primary_vendor)
                and (not alias_product or not primary_product or alias_product == primary_product)
                for primary_vendor, primary_product, _ in xbox_primaries
            )
        }
        visible = []
        for joystick in self.joysticks:
            try:
                if joystick.get_instance_id() not in duplicate_alias_ids:
                    visible.append(joystick)
            except pygame.error:
                continue
        return visible

    def hidhide_type_for_gamepad(self, vendor_id: int, product_id: int, name: str = "") -> str:
        normalized_name = self.normalize_device_name(name)
        id_matches = []
        if vendor_id and product_id:
            vendor_token = f"vid_{vendor_id:04x}"
            product_token = f"pid_{product_id:04x}"
            id_matches = [
                device
                for device in self.hidhide_devices
                if any(
                    vendor_token in path.casefold() and product_token in path.casefold()
                    for path in device.get("paths", [])
                    if isinstance(path, str)
                )
            ]

        name_matches = [
            device
            for device in self.hidhide_devices
            if normalized_name
            and (
                self.normalize_device_name(device.get("name", "")) == normalized_name
                or (
                    min(len(normalized_name), len(self.normalize_device_name(device.get("name", "")))) >= 8
                    and (
                        normalized_name in self.normalize_device_name(device.get("name", ""))
                        or self.normalize_device_name(device.get("name", "")) in normalized_name
                    )
                )
            )
        ]

        exact_name_matches = [
            device
            for device in name_matches
            if self.normalize_device_name(device.get("name", "")) == normalized_name
        ]
        matches = exact_name_matches or name_matches or id_matches
        matching_types = {device.get("device_type", "unknown") for device in matches}
        if len(matching_types) == 1:
            device_type = matching_types.pop()
            if device_type in {"virtual", "physical"}:
                return device_type
        return "unknown"

    @staticmethod
    def normalize_device_name(value: str) -> str:
        return "".join(character for character in value.casefold() if character.isalnum())

    def draw_bluetooth_page(self, rect: pygame.Rect) -> None:
        self.draw_panel(rect, "Dispositivos Bluetooth")
        self.draw_text("Actualizar para ver cambios de conexión o emparejamiento.", rect.x + 22, rect.y + 57, MUTED, self.small)
        status_lower = self.bluetooth_status.casefold()
        status_color = WARN if any(
            word in status_lower
            for word in ("desactivado", "no se detectó", "no pudo", "falló", "denegó", "no está listo", "error", "rechazó")
        ) else ACCENT
        self.draw_text_fitted(self.bluetooth_status, rect.x + 22, rect.y + 82, rect.width - 44, status_color, self.small)

        self.bluetooth_rects = {}
        list_rect = pygame.Rect(rect.x + 20, rect.y + 118, rect.width - 40, rect.height - 216)
        pygame.draw.rect(self.screen, (25, 30, 40), list_rect, border_radius=10)
        self.bluetooth_row_rects = []
        devices = [
            device for device in self.bluetooth_devices.values()
            if not self.bluetooth_gamepads_only or device["gamepad"]
        ]
        devices.sort(
            key=lambda d: (
                not (d.get("connected", False) or self.bluetooth_device_has_live_joystick(d)),
                not d["paired"],
                d["name"].casefold(),
            )
        )
        visible_rows = max(1, list_rect.height // 54)
        self.bluetooth_scroll = max(0, min(self.bluetooth_scroll, max(0, len(devices) - visible_rows)))
        visible = devices[self.bluetooth_scroll:self.bluetooth_scroll + visible_rows]
        if not visible:
            message = "No se encontraron mandos Bluetooth." if self.bluetooth_gamepads_only else "No se han encontrado dispositivos Bluetooth todavía."
            self.draw_text(message, list_rect.x + 16, list_rect.y + 16, MUTED, self.small)
            self.draw_text("Pon el mando en modo de emparejamiento o desactiva el filtro para ver todos.", list_rect.x + 16, list_rect.y + 39, MUTED, self.small)
        for index, device in enumerate(visible):
            row = pygame.Rect(list_rect.x + 6, list_rect.y + 5 + index * 54, list_rect.width - 12, 48)
            selected = device["id"] == self.bluetooth_selected_id
            fill = (52, 67, 88) if selected else PANEL
            pygame.draw.rect(self.screen, fill, row, border_radius=8)
            if selected:
                pygame.draw.rect(self.screen, ACCENT, row, 1, border_radius=8)
            self.bluetooth_row_rects.append((row, device["id"]))
            label = device["name"][:54]
            connected = device.get("connected", False) or self.bluetooth_device_has_live_joystick(device)
            paired = device["paired"]
            if connected or paired:
                icon_color = ACTIVE if connected else PAIRED_COLOR
                icon_center = (row.x + 22, row.y + 17)
                pygame.draw.circle(self.screen, icon_color, icon_center, 10)
                pygame.draw.line(self.screen, BG, (icon_center[0] - 5, icon_center[1]), (icon_center[0] - 1, icon_center[1] + 4), 2)
                pygame.draw.line(self.screen, BG, (icon_center[0] - 1, icon_center[1] + 4), (icon_center[0] + 6, icon_center[1] - 5), 2)
            self.draw_text(label, row.x + (39 if connected or paired else 13), row.y + 5, TEXT, self.font)
            if connected:
                pairing, state_color = "Conectado", ACTIVE
            elif paired:
                pairing, state_color = "Emparejado · desconectado", PAIRED_COLOR
            else:
                pairing = "Disponible para emparejar" if device["can_pair"] else "Detectado · activa modo emparejamiento"
                state_color = MUTED
            reason = f" · {device.get('match_reason', '')}" if device["gamepad"] else ""
            self.draw_text(f"{pairing} · {device['transport']}{reason}", row.x + 13, row.y + 27, state_color, self.small)

        selected_device = self.bluetooth_devices.get(self.bluetooth_selected_id or "")
        button_y = rect.bottom - 72
        pair_rect = pygame.Rect(rect.x + 20, button_y, 150, 42)
        remove_rect = pygame.Rect(pair_rect.right + 12, button_y, 170, 42)
        refresh_rect = pygame.Rect(rect.right - 190, button_y, 170, 42)
        self.bluetooth_rects.update({"pair": pair_rect, "remove": remove_rect, "refresh": refresh_rect})
        self.draw_action_button(pair_rect, "Añadir / emparejar", bool(selected_device and not selected_device["paired"]))
        self.draw_action_button(remove_rect, "Eliminar emparejamiento", bool(selected_device and selected_device["paired"]))
        self.draw_action_button(refresh_rect, "Reiniciar búsqueda", True)
        self.draw_text("Si Windows indica 'no listo', pon el mando en modo de emparejamiento. El resultado aparece en el estado superior.", rect.x + 22, rect.bottom - 24, MUTED, self.small)

    def draw_hidhide_page(self, rect: pygame.Rect) -> None:
        self.draw_panel(rect, "HidHide · ocultar mandos")
        self.draw_text_fitted(
            "Selecciona un mando y marca si es virtual o físico para guardar su ID. HidHide debe estar instalado por separado.",
            rect.x + 22, rect.y + 55, rect.width - 44, MUTED, self.small,
        )
        status_color = WARN if any(
            marker in self.hidhide_status.casefold()
            for marker in ("no se", "error", "falta", "ejecuta", "instala", "requiere", "fall", "autoriza", "deneg")
        ) else ACTIVE if self.hidhide_enabled else MUTED
        self.draw_text_fitted(
            self.hidhide_status,
            rect.x + 22, rect.y + 78, rect.width - 44, status_color, self.small,
        )
        filter_state = "activo" if self.hidhide_enabled else "desactivado"
        app_state = "autorizada" if self.hidhide_app_allowed else "no autorizada"
        state_color = ACTIVE if self.hidhide_app_allowed else WARN
        self.draw_text_fitted(
            f"Filtro {filter_state} · Aplicación de GDM {app_state}",
            rect.x + 22, rect.y + 101, rect.width - 44, state_color, self.small,
        )
        if Path(self.hidhide_app_path).name.casefold() in {"python.exe", "pythonw.exe"}:
            self.draw_text_fitted(
                f"Modo código fuente: se autoriza {self.hidhide_app_path}, no solo GDM.",
                rect.x + 22, rect.y + 122, rect.width - 44, WARN, self.small,
            )
        else:
            self.draw_text_fitted(
                f"Ejecutable de GDM: {Path(self.hidhide_app_path).name}",
                rect.x + 22, rect.y + 122, rect.width - 44, MUTED, self.small,
            )

        list_y = rect.y + 148
        button_y = rect.bottom - 52
        list_rect = pygame.Rect(rect.x + 20, list_y, rect.width - 40, max(64, button_y - list_y - 12))
        self.hidhide_rects = {"list": list_rect}
        pygame.draw.rect(self.screen, (25, 30, 40), list_rect, border_radius=10)
        if platform.system() != "Windows":
            self.draw_text("La administración de HidHide solo está disponible en Windows.", list_rect.x + 14, list_rect.y + 14, MUTED, self.small)
        elif not self.hidhide_devices:
            message = "Actualizando…" if self.hidhide_busy else "No hay mandos para mostrar. Comprueba HidHide y pulsa «Actualizar»."
            self.draw_text_fitted(message, list_rect.x + 14, list_rect.y + 14, list_rect.width - 28, MUTED, self.small)
        else:
            row_height = 46
            visible_rows = max(1, list_rect.height // row_height)
            self.hidhide_scroll = max(
                0, min(self.hidhide_scroll, max(0, len(self.hidhide_devices) - visible_rows)),
            )
            visible_devices = self.hidhide_devices[
                self.hidhide_scroll:self.hidhide_scroll + visible_rows
            ]
            for index, device in enumerate(visible_devices):
                row = pygame.Rect(
                    list_rect.x + 6,
                    list_rect.y + 5 + index * row_height,
                    list_rect.width - 12,
                    row_height - 3,
                )
                self.hidhide_rects[f"device:{device['id']}"] = row
                selected = device["id"] == self.hidhide_selected_id
                fill = (52, 67, 88) if selected else PANEL_LIGHT
                pygame.draw.rect(self.screen, fill, row, border_radius=6)
                name = str(device["name"])
                if sum(item["name"] == name for item in self.hidhide_devices) > 1:
                    name = f"{name} · {device['id'].rsplit(chr(92), 1)[-1][:10]}"
                self.draw_text_fitted(name, row.x + 9, row.y + 4, row.width - 150, TEXT, self.small)
                connection_state = "Conectado" if device["present"] else "No conectado"
                if device.get("device_type") == "virtual":
                    details = f"MANDO VIRTUAL · {device['virtual_type']} · {connection_state}"
                    details_color = VIRTUAL_COLOR
                elif device.get("device_type") == "physical":
                    details = f"MANDO FÍSICO · {connection_state}"
                    details_color = PAIRED_COLOR
                else:
                    details = f"TIPO NO IDENTIFICADO · {connection_state}"
                    details_color = MUTED
                if device["vendor"] or device["product"]:
                    details += f" · {device['vendor']} {device['product']}".strip()
                self.draw_text_fitted(details, row.x + 9, row.y + 23, row.width - 150, details_color, self.small)
                if device["hidden_count"] == len(device["paths"]):
                    visibility, color = "Oculto", WARN
                elif device["hidden_count"]:
                    visibility, color = "Parcial", WARN
                else:
                    visibility, color = "Visible", ACTIVE
                label = self.small.render(visibility, True, color)
                self.screen.blit(label, label.get_rect(right=row.right - 9, centery=row.centery))

        available = platform.system() == "Windows" and not self.hidhide_busy
        selected_device = next(
            (device for device in self.hidhide_devices if device["id"] == self.hidhide_selected_id),
            None,
        )
        buttons = (
            ("refresh", "Actualizar", 96, True),
            (
                "allow",
                "GDM autorizado" if self.hidhide_app_allowed else "Autorizar GDM",
                126,
                not self.hidhide_app_allowed,
            ),
            (
                "visibility",
                (
                    "Mostrar mando"
                    if selected_device and selected_device["hidden_count"]
                    else "Mando virtual"
                    if selected_device and selected_device.get("device_type") == "virtual"
                    else "Ocultar mando"
                ),
                130,
                selected_device is not None and (
                    bool(selected_device["hidden_count"])
                    or selected_device.get("device_type") != "virtual"
                ),
            ),
            (
                "device_type",
                "Marcar físico" if selected_device and selected_device.get("device_type") == "virtual" else "Marcar virtual",
                112,
                selected_device is not None,
            ),
            (
                "cloak",
                "Desactivar ocultamiento" if self.hidhide_enabled else "Activar ocultamiento",
                176,
                True,
            ),
        )
        x = rect.x + 20
        gap = 6
        for key, label, button_width, enabled in buttons:
            button = pygame.Rect(x, button_y, button_width, 34)
            self.hidhide_rects[key] = button
            self.draw_action_button(button, label, available and enabled)
            x = button.right + gap

    def draw_remap_page(self, rect: pygame.Rect) -> None:
        self.draw_panel(rect, "Remapeo y mando virtual")
        self.draw_text_fitted(
            "El perfil activo se detecta automáticamente; selecciona uno para editarlo.",
            rect.x + 22, rect.y + 55, rect.width - 44, MUTED, self.small,
        )
        self.remap_rects = {}
        inner_width = rect.width - 44
        gap = max(8, round(12 * self.ui_scale))
        profile_width = (inner_width - gap * (len(PROFILE_NAMES) - 1)) // len(PROFILE_NAMES)
        profile_y = rect.y + 86
        for index, profile in enumerate(PROFILE_NAMES):
            button = pygame.Rect(
                rect.x + 22 + index * (profile_width + gap),
                profile_y,
                profile_width,
                round(34 * self.ui_scale),
            )
            self.remap_rects[f"profile:{profile}"] = button
            selected = self.remap_profile == profile
            pygame.draw.rect(self.screen, ACCENT if selected else PANEL_LIGHT, button, border_radius=8)
            pygame.draw.rect(self.screen, ACCENT if selected else MUTED, button, 1, border_radius=8)
            label = self.small.render(profile, True, BG if selected else TEXT)
            self.screen.blit(label, label.get_rect(center=button.center))

        output_y = rect.y + 132
        self.draw_text("Dispositivo virtual para este perfil:", rect.x + 22, output_y, TEXT, self.small)
        choice_y = output_y + 25
        choice_gap = max(8, round(10 * self.ui_scale))
        choice_width = min(190, (inner_width - choice_gap - 160) // 2)
        for index, output_type in enumerate(OUTPUT_TYPES):
            button = pygame.Rect(
                rect.x + 22 + index * (choice_width + choice_gap),
                choice_y,
                choice_width,
                round(32 * self.ui_scale),
            )
            self.remap_rects[f"output:{output_type}"] = button
            selected = self.virtual_profile_outputs[self.remap_profile] == output_type
            pygame.draw.rect(self.screen, ACCENT if selected else PANEL_LIGHT, button, border_radius=8)
            pygame.draw.rect(self.screen, ACCENT if selected else MUTED, button, 1, border_radius=8)
            label = self.small.render(output_type, True, BG if selected else TEXT)
            self.screen.blit(label, label.get_rect(center=button.center))

        toggle = pygame.Rect(rect.right - 178, choice_y, 156, round(32 * self.ui_scale))
        self.remap_rects["toggle"] = toggle
        self.draw_action_button(
            toggle,
            "Detener salida" if self.virtual_output_enabled else "Iniciar salida virtual",
            True,
        )
        status_color = (
            ERROR_COLOR if "No se pudo" in self.virtual_output_status or "Falta" in self.virtual_output_status
            else ACTIVE if "activo" in self.virtual_output_status.casefold()
            else WARN if "conecta" in self.virtual_output_status.casefold()
            else MUTED
        )
        self.draw_text_fitted(
            self.virtual_output_status,
            rect.x + 22,
            choice_y + 39,
            inner_width,
            status_color,
            self.small,
        )
        warning = "ViGEmBus está retirado; GDM no instala drivers. Los juegos pueden detectar también el mando físico."
        self.draw_text_fitted(warning, rect.x + 22, choice_y + 61, inner_width, WARN, self.small)

        auto_start = pygame.Rect(rect.x + 22, choice_y + 82, 22, 22)
        auto_start_width = inner_width // 2 - 8
        self.remap_rects["auto_start"] = pygame.Rect(
            auto_start.x,
            auto_start.y,
            auto_start_width,
            26,
        )
        pygame.draw.rect(self.screen, PANEL_LIGHT, auto_start, border_radius=4)
        pygame.draw.rect(
            self.screen,
            ACCENT if self.virtual_output_auto_start else MUTED,
            auto_start,
            2,
            border_radius=4,
        )
        if self.virtual_output_auto_start:
            pygame.draw.line(
                self.screen,
                ACTIVE,
                (auto_start.x + 4, auto_start.y + 11),
                (auto_start.x + 9, auto_start.y + 16),
                3,
            )
            pygame.draw.line(
                self.screen,
                ACTIVE,
                (auto_start.x + 9, auto_start.y + 16),
                (auto_start.x + 18, auto_start.y + 5),
                3,
            )
        self.draw_text_fitted(
            "Inicio virtual automático al conectar",
            auto_start.right + 10,
            auto_start.y + 1,
            auto_start_width - 32,
            TEXT,
            self.small,
        )

        led_column_x = rect.x + 22 + inner_width // 2
        led_target = self.playstation_led_target()
        led_available, led_message = self.playstation_led_availability(led_target)
        led_label = (
            f"LED PS: {PLAYSTATION_LED_COLORS[self.playstation_led_color][0]}"
            if self.playstation_led_color in PLAYSTATION_LED_COLORS
            else "LED PS:"
        )
        if not led_available:
            led_label = led_message
        self.draw_text_fitted(
            led_label,
            led_column_x,
            auto_start.y + 3,
            72 if led_available else inner_width - inner_width // 2,
            ACTIVE if led_available and self.playstation_led_color else MUTED,
            self.small,
        )
        if led_available:
            swatch_x = led_column_x + 76
            swatch_y = auto_start.y
            for color_key, (_label, rgb) in PLAYSTATION_LED_COLORS.items():
                swatch = pygame.Rect(swatch_x, swatch_y, 25, 25)
                self.remap_rects[f"playstation_led:{color_key}"] = swatch
                pygame.draw.rect(self.screen, rgb, swatch, border_radius=6)
                pygame.draw.rect(
                    self.screen,
                    TEXT if self.playstation_led_color == color_key else PANEL_LIGHT,
                    swatch,
                    2 if self.playstation_led_color == color_key else 1,
                    border_radius=6,
                )
                swatch_x += 30
        led_status = (
            self.playstation_led_status
            if led_available and self.playstation_led_color
            else "Solo afecta al mando físico; no cambia la salida virtual."
            if led_available
            else led_message
        )
        self.draw_text_fitted(
            led_status,
            led_column_x,
            auto_start.y + 24,
            inner_width - inner_width // 2,
            MUTED if led_available else WARN,
            self.small,
        )

        heading_y = rect.y + 300
        self.draw_text(
            "Clic en una fila para cambiar destino · ejes y gatillos directos",
            rect.x + 22, heading_y, ACCENT, self.small,
        )
        mapping = self.virtual_profile_mappings[self.remap_profile]
        target_labels = button_target_labels(self.virtual_profile_outputs[self.remap_profile])
        rows_y = heading_y + 26
        column_gap = max(12, round(20 * self.ui_scale))
        column_width = (inner_width - column_gap) // 2
        row_height = min(25, max(17, (rect.bottom - rows_y - 12) // 8))
        for index, (button_id, label) in enumerate(REMAP_BUTTONS):
            column = index // 8
            row = index % 8
            item_rect = pygame.Rect(
                rect.x + 22 + column * (column_width + column_gap),
                rows_y + row * row_height,
                column_width,
                row_height - 2,
            )
            self.remap_rects[f"mapping:{button_id}"] = item_rect
            pygame.draw.rect(self.screen, PANEL_LIGHT, item_rect, border_radius=5)
            source_surface = self.small.render(label, True, TEXT)
            self.screen.blit(source_surface, (item_rect.x + 7, item_rect.y + 3))
            target = mapping.get(button_id)
            target_name = target_labels.get(target, "— Desactivado") if target is not None else "— Desactivado"
            rendered = self.small.render(target_name, True, ACCENT)
            target_rect = rendered.get_rect(right=item_rect.right - 7, centery=item_rect.centery)
            self.screen.blit(rendered, target_rect)

    def draw_options_page(self, rect: pygame.Rect) -> None:
        self.draw_panel(rect, "Configuración del programa")
        self.draw_text("Preferencias guardadas automáticamente en este equipo.", rect.x + 22, rect.y + 57, MUTED, self.small)

        self.options_rects = {}
        section_tabs = (("general", "General"), ("controllers", "Mandos"))
        section_gap = max(6, round(8 * self.ui_scale))
        section_width = round(148 * self.ui_scale)
        section_height = round(42 * self.ui_scale)
        section_rail = pygame.Rect(
            rect.x + 22,
            rect.y + 91,
            section_width * len(section_tabs) + section_gap * (len(section_tabs) - 1),
            section_height,
        )
        pygame.draw.rect(self.screen, PANEL, section_rail, border_radius=9)
        pygame.draw.line(
            self.screen,
            PANEL_LIGHT,
            (section_rail.x + 4, section_rail.bottom - 1),
            (section_rail.right - 4, section_rail.bottom - 1),
            1,
        )
        for index, (key, label) in enumerate(section_tabs):
            selected = self.options_section == key
            button = pygame.Rect(
                section_rail.x + index * (section_width + section_gap),
                section_rail.y if selected else section_rail.y + round(6 * self.ui_scale),
                section_width,
                section_height if selected else section_height - round(6 * self.ui_scale),
            )
            self.options_rects[f"section_{key}"] = button
            fill = PANEL if selected else BG
            border = ACCENT if selected else PANEL_LIGHT
            pygame.draw.rect(self.screen, fill, button, border_radius=9)
            pygame.draw.rect(self.screen, border, button, 1, border_radius=9)
            pygame.draw.rect(
                self.screen,
                fill,
                (button.x + 1, button.bottom - 9, button.width - 2, 8),
            )
            pygame.draw.line(self.screen, border, (button.left, button.y + 9), (button.left, button.bottom - 1), 1)
            pygame.draw.line(self.screen, border, (button.right - 1, button.y + 9), (button.right - 1, button.bottom - 1), 1)
            text = self.small.render(label, True, ACCENT if selected else MUTED)
            self.screen.blit(text, text.get_rect(center=button.center))

        checkbox_x = rect.x + 22
        if self.options_section == "general":
            self.draw_text("Pestaña que se abrirá al iniciar", rect.x + 22, rect.y + 151, TEXT, self.font)
            startup_tabs = (
                ("devices", "Mandos conectados"),
                ("tester", "Gamepad Tester"),
                ("bluetooth", "Bluetooth"),
                ("remap", "Remapeo"),
                ("hidhide", "HidHide"),
                ("options", "Configuración"),
                ("about", "Acerca de"),
            )
            startup_gap = max(8, round(12 * self.ui_scale))
            startup_width = min(172, (rect.width - 64 - startup_gap * (len(startup_tabs) - 1)) // len(startup_tabs))
            for index, (key, label) in enumerate(startup_tabs):
                button = pygame.Rect(
                    rect.x + 22 + index * (startup_width + startup_gap),
                    rect.y + 186,
                    startup_width,
                    round(38 * self.ui_scale),
                )
                self.options_rects[f"startup_{key}"] = button
                selected = self.startup_tab == key
                pygame.draw.rect(self.screen, ACCENT if selected else PANEL_LIGHT, button, border_radius=round(8 * self.ui_scale))
                pygame.draw.rect(self.screen, ACCENT if selected else MUTED, button, 1, border_radius=round(8 * self.ui_scale))
                text = self.small.render(label, True, BG if selected else TEXT)
                self.screen.blit(text, text.get_rect(center=button.center))

            startup_checkboxes = (
                ("launch_at_startup", "Iniciar GDM automáticamente al iniciar Windows", self.launch_at_startup, rect.y + 258),
                ("start_minimized_in_tray", "Iniciar minimizado en la bandeja del sistema", self.start_minimized_in_tray, rect.y + 290),
            )
            self.draw_text("Color del icono de la bandeja", rect.x + 22, rect.y + 341, TEXT, self.font)
            tray_icon_modes = (
                ("auto", "Automático"),
                ("white", "Blanco"),
                ("black", "Negro"),
            )
            mode_gap = max(6, round(8 * self.ui_scale))
            tray_column_width = max(0, rect.width // 2 - 42)
            mode_width = max(
                70,
                min(
                    150,
                    (tray_column_width - mode_gap * (len(tray_icon_modes) - 1)) // len(tray_icon_modes),
                ),
            )
            for index, (mode, label) in enumerate(tray_icon_modes):
                button = pygame.Rect(
                    rect.x + 22 + index * (mode_width + mode_gap),
                    rect.y + 375,
                    mode_width,
                    round(34 * self.ui_scale),
                )
                self.options_rects[f"tray_icon_mode:{mode}"] = button
                selected = self.tray_icon_mode == mode
                pygame.draw.rect(self.screen, ACCENT if selected else PANEL_LIGHT, button, border_radius=round(8 * self.ui_scale))
                pygame.draw.rect(self.screen, ACCENT if selected else MUTED, button, 1, border_radius=round(8 * self.ui_scale))
                text = self.small.render(label, True, BG if selected else TEXT)
                self.screen.blit(text, text.get_rect(center=button.center))
            console_checkbox = pygame.Rect(rect.x + 22, rect.y + 418, 22, 22)
            self.options_rects["show_console_button"] = pygame.Rect(
                console_checkbox.x,
                console_checkbox.y,
                rect.width // 2 - 30,
                26,
            )
            pygame.draw.rect(self.screen, PANEL_LIGHT, console_checkbox, border_radius=4)
            pygame.draw.rect(
                self.screen,
                ACCENT if self.show_console_button else MUTED,
                console_checkbox,
                2,
                border_radius=4,
            )
            if self.show_console_button:
                pygame.draw.line(self.screen, ACTIVE, (console_checkbox.x + 4, console_checkbox.y + 11), (console_checkbox.x + 9, console_checkbox.y + 16), 3)
                pygame.draw.line(self.screen, ACTIVE, (console_checkbox.x + 9, console_checkbox.y + 16), (console_checkbox.x + 18, console_checkbox.y + 5), 3)
            self.draw_text_fitted(
                "Mostrar botón «Abrir consola»",
                console_checkbox.right + 10,
                console_checkbox.y + 1,
                rect.width // 2 - 64,
                TEXT,
                self.small,
            )
            exit_column_x = rect.x + rect.width // 2 + 10
            exit_column_width = rect.right - exit_column_x - 22
            self.draw_text("Al presionar Escape", exit_column_x, rect.y + 320, TEXT, self.font)
            escape_prompt = pygame.Rect(exit_column_x, rect.y + 350, 22, 22)
            self.options_rects["escape_prompt_enabled"] = pygame.Rect(
                escape_prompt.x,
                escape_prompt.y,
                exit_column_width,
                26,
            )
            pygame.draw.rect(self.screen, PANEL_LIGHT, escape_prompt, border_radius=4)
            pygame.draw.rect(
                self.screen,
                ACCENT if self.escape_prompt_enabled else MUTED,
                escape_prompt,
                2,
                border_radius=4,
            )
            if self.escape_prompt_enabled:
                pygame.draw.line(self.screen, ACTIVE, (escape_prompt.x + 4, escape_prompt.y + 11), (escape_prompt.x + 9, escape_prompt.y + 16), 3)
                pygame.draw.line(self.screen, ACTIVE, (escape_prompt.x + 9, escape_prompt.y + 16), (escape_prompt.x + 18, escape_prompt.y + 5), 3)
            self.draw_text_fitted(
                "Preguntar si cerrar GDM o minimizarlo a la bandeja",
                escape_prompt.right + 10,
                escape_prompt.y + 1,
                exit_column_width - 32,
                TEXT,
                self.small,
            )
            remember_choice = pygame.Rect(exit_column_x, rect.y + 384, 22, 22)
            self.options_rects["remember_escape_choice"] = pygame.Rect(
                remember_choice.x,
                remember_choice.y,
                exit_column_width,
                26,
            )
            pygame.draw.rect(self.screen, PANEL_LIGHT, remember_choice, border_radius=4)
            remember_enabled = self.remember_escape_choice and self.escape_prompt_enabled
            pygame.draw.rect(
                self.screen,
                ACCENT if remember_enabled else MUTED,
                remember_choice,
                2,
                border_radius=4,
            )
            if remember_enabled:
                pygame.draw.line(self.screen, ACTIVE, (remember_choice.x + 4, remember_choice.y + 11), (remember_choice.x + 9, remember_choice.y + 16), 3)
                pygame.draw.line(self.screen, ACTIVE, (remember_choice.x + 9, remember_choice.y + 16), (remember_choice.x + 18, remember_choice.y + 5), 3)
            self.draw_text_fitted(
                "Recordar la última elección y omitir la pregunta",
                remember_choice.right + 10,
                remember_choice.y + 1,
                exit_column_width - 32,
                TEXT if self.escape_prompt_enabled else MUTED,
                self.small,
            )
            self.draw_text("Al cerrar la ventana con X", exit_column_x, rect.y + 413, TEXT, self.font)
            close_actions = (
                ("close", "Cerrar el programa"),
                ("minimize", "Minimizar a la bandeja"),
            )
            close_gap = max(6, round(8 * self.ui_scale))
            close_width = min(
                (exit_column_width - close_gap) // 2,
                round(220 * self.ui_scale),
            )
            for index, (action, label) in enumerate(close_actions):
                button = pygame.Rect(
                    exit_column_x + index * (close_width + close_gap),
                    rect.y + 439,
                    close_width,
                    round(30 * self.ui_scale),
                )
                self.options_rects[f"window_close:{action}"] = button
                selected = self.window_close_action == action
                pygame.draw.rect(self.screen, PANEL_LIGHT if selected else PANEL, button, border_radius=9)
                pygame.draw.rect(
                    self.screen,
                    ACCENT if selected else MUTED,
                    button,
                    2 if selected else 1,
                    border_radius=9,
                )
                text = self.small.render(label, True, TEXT if selected else MUTED)
                self.screen.blit(text, text.get_rect(center=button.center))
            footer_text = ""
        else:
            startup_checkboxes = (
                ("auto", "Emparejar automáticamente los mandos compatibles al detectarlos", self.bluetooth_auto_pair, rect.y + 145),
                ("gamepads", "Mostrar solo mandos en la lista Bluetooth", self.bluetooth_gamepads_only, rect.y + 177),
                ("home_disconnect", "Activar acción al mantener Home/PS durante 2 segundos", self.disconnect_on_home_hold, rect.y + 221),
            )
            self.draw_text("Acción para Home/PS", rect.x + 22, rect.y + 267, TEXT, self.font)
            card_gap = max(12, round(20 * self.ui_scale))
            card_width = (rect.width - 44 - card_gap) // 2
            actions = (
                ("disconnect", "Desconectar (mantener emparejado)", "Corta la conexión y conserva el vínculo Bluetooth."),
                ("unpair", "Desemparejar el mando", "Quita el vínculo; habrá que emparejarlo otra vez."),
            )
            for index, (key, label, detail) in enumerate(actions):
                card = pygame.Rect(
                    rect.x + 22 + index * (card_width + card_gap),
                    rect.y + 302,
                    card_width,
                    round(68 * self.ui_scale),
                )
                self.options_rects[f"home_action_{key}"] = card
                selected = self.home_hold_action == key
                pygame.draw.rect(self.screen, PANEL_LIGHT if selected else PANEL, card, border_radius=round(9 * self.ui_scale))
                pygame.draw.rect(self.screen, ACCENT if selected else MUTED, card, 2 if selected else 1, border_radius=round(9 * self.ui_scale))
                self.draw_text_fitted(label, card.x + 12, card.y + 10, card.width - 24, TEXT, self.small)
                self.draw_text_fitted(detail, card.x + 12, card.y + 34, card.width - 24, MUTED, self.small)
            footer_text = "La desconexión directa conserva el vínculo Bluetooth."

        for key, label, enabled, y in startup_checkboxes:
            checkbox = pygame.Rect(checkbox_x, y, 22, 22)
            self.options_rects[key] = pygame.Rect(checkbox.x, checkbox.y, rect.right - checkbox.x - 22, 26)
            pygame.draw.rect(self.screen, PANEL_LIGHT, checkbox, border_radius=4)
            enabled_look = enabled and (
                key != "start_minimized_in_tray" or self.launch_at_startup
            )
            if key in {"launch_at_startup", "start_minimized_in_tray"} and self.startup_busy:
                enabled_look = False
            pygame.draw.rect(self.screen, ACCENT if enabled_look else MUTED, checkbox, 2, border_radius=4)
            if enabled:
                mark_color = ACTIVE if enabled_look else MUTED
                pygame.draw.line(self.screen, mark_color, (checkbox.x + 4, checkbox.y + 11), (checkbox.x + 9, checkbox.y + 16), 3)
                pygame.draw.line(self.screen, mark_color, (checkbox.x + 9, checkbox.y + 16), (checkbox.x + 18, checkbox.y + 5), 3)
            label_color = MUTED if (
                (key == "start_minimized_in_tray" and not self.launch_at_startup)
                or (key in {"launch_at_startup", "start_minimized_in_tray"} and self.startup_busy)
            ) else TEXT
            self.draw_text_fitted(label, checkbox.right + 10, checkbox.y + 1, rect.right - checkbox.right - 32, label_color, self.small)

        if footer_text:
            self.draw_text_fitted(
                footer_text,
                rect.x + 22,
                rect.bottom - max(22, round(22 * self.ui_scale)),
                rect.width - 44,
                WARN if self.options_section == "general" else MUTED,
                self.small,
            )

    def draw_about_page(self, rect: pygame.Rect) -> None:
        self.draw_panel(rect, "Acerca de")
        center_x = rect.centerx
        if APP_ICON_PATH.is_file():
            try:
                icon = pygame.image.load(str(APP_ICON_PATH))
                icon = pygame.transform.smoothscale(icon, (144, 144))
                self.screen.blit(icon, icon.get_rect(center=(center_x, rect.y + 204)))
            except pygame.error as error:
                self.write_console("ERROR", f"No se pudo mostrar el icono en Acerca de: {error}")

        title = self.title_font.render(APP_NAME, True, TEXT)
        self.screen.blit(title, title.get_rect(center=(center_x, rect.y + 310)))
        details = (
            ("Creador", "Johan Boscan"),
            ("Año de creación", "2026"),
            ("Versión", APP_VERSION),
        )
        for index, (label, value) in enumerate(details):
            y = rect.y + 365 + index * 42
            label_surface = self.font.render(f"{label}:", True, MUTED)
            value_surface = self.font.render(value, True, TEXT)
            self.screen.blit(label_surface, label_surface.get_rect(right=center_x - 8, y=y))
            self.screen.blit(value_surface, value_surface.get_rect(left=center_x + 8, y=y))

    def draw_action_button(self, rect: pygame.Rect, label: str, enabled: bool) -> None:
        color = PANEL_LIGHT if enabled else (35, 40, 50)
        pygame.draw.rect(self.screen, color, rect, border_radius=9)
        pygame.draw.rect(self.screen, ACCENT if enabled else PANEL_LIGHT, rect, 1, border_radius=9)
        text = self.small.render(label, True, TEXT if enabled else MUTED)
        self.screen.blit(text, text.get_rect(center=rect.center))

    def process_bluetooth_events(self) -> None:
        for _ in range(100):
            try:
                event, value = self.bluetooth.events.get_nowait()
            except queue.Empty:
                return
            if event == "radio":
                self.bluetooth_radio_state = value
            elif event == "device":
                self.bluetooth_devices[value["id"]] = value
                if self.pending_home_action:
                    self.try_pending_home_action()
            elif event == "removed":
                self.bluetooth_devices.pop(value, None)
                if self.bluetooth_selected_id == value:
                    self.bluetooth_selected_id = None
            elif event == "status":
                self.bluetooth_status = value
            elif event == "operation":
                device_id, action, status = value
                self.bluetooth_status = pairing_status_text(status, action)
                device = self.bluetooth_devices.get(device_id)
                if device is not None:
                    if action == "pair" and status in {"PAIRED", "ALREADY_PAIRED"}:
                        device["paired"] = True
                    elif action == "unpair" and status in {"UNPAIRED", "ALREADY_UNPAIRED"}:
                        device["paired"] = False
            elif event == "error":
                self.bluetooth_status = value
            elif event == "log":
                level, message = value
                self.write_console(level, message)

    def start_windows_startup_update(
        self,
        enabled: bool,
        rollback_setting: str,
        rollback_value: bool,
    ) -> None:
        if self.startup_busy:
            return
        self.startup_busy = True
        start_minimized = self.start_minimized_in_tray

        def worker() -> None:
            try:
                shortcut_path = set_windows_startup(enabled, start_minimized)
            except StartupShortcutError as error:
                self.startup_events.put((False, rollback_setting, rollback_value, str(error)))
                return
            action = "registrado" if enabled else "quitado"
            self.startup_events.put((True, rollback_setting, rollback_value, f"Acceso directo de inicio {action}: {shortcut_path}"))

        threading.Thread(target=worker, name="GDMStartupShortcut", daemon=True).start()

    def process_startup_events(self) -> None:
        while True:
            try:
                succeeded, setting, old_value, message = self.startup_events.get_nowait()
            except queue.Empty:
                return
            self.startup_busy = False
            if succeeded:
                self.write_console("SUCCESS", message)
                continue
            setattr(self, setting, old_value)
            self.save_settings()
            self.write_console("ERROR", message)

    def start_hidhide_task(self, operation: str, device: Optional[dict] = None) -> None:
        if self.hidhide_busy:
            return
        self.hidhide_busy = True
        self.hidhide_status = {
            "refresh": "Consultando dispositivos y configuración de HidHide…",
            "visibility": "Actualizando visibilidad del mando…",
            "allow": "Autorizando la aplicación de GDM…",
            "cloak": "Actualizando el estado de ocultamiento…",
        }[operation]
        paths = list(device["paths"]) if device is not None else []
        hide_device = bool(device is not None and not device["hidden_count"])
        cloak_active = self.hidhide_enabled

        def worker() -> None:
            try:
                if operation == "visibility":
                    self.hidhide_service.set_device_hidden(paths, hide_device, cloak_active)
                elif operation == "allow":
                    self.hidhide_service.allow_current_app()
                elif operation == "cloak":
                    self.hidhide_service.set_active(not cloak_active)
                snapshot = self.hidhide_service.refresh()
            except HidHideError as error:
                self.hidhide_events.put(("error", str(error), None))
                return
            self.hidhide_events.put(("success", operation, snapshot))

        threading.Thread(target=worker, daemon=True).start()

    @staticmethod
    def hidhide_device_identifiers(device: dict) -> list[str]:
        identifiers = [f"id:{device['id']}"]
        identifiers.extend(f"path:{path}" for path in device.get("paths", []))
        return [identifier.casefold() for identifier in identifiers]

    def apply_hidhide_type_overrides(self, devices: list[dict]) -> None:
        for device in devices:
            override = next(
                (
                    self.hidhide_type_overrides[identifier]
                    for identifier in self.hidhide_device_identifiers(device)
                    if identifier in self.hidhide_type_overrides
                ),
                None,
            )
            if override is not None:
                device["device_type"] = override
                device["virtual_type"] = "Marcado manualmente" if override == "virtual" else None

    def process_hidhide_events(self) -> None:
        while True:
            try:
                event, value, snapshot = self.hidhide_events.get_nowait()
            except queue.Empty:
                break
            self.hidhide_busy = False
            if event == "error":
                self.hidhide_status = value
                self.write_console("ERROR", value)
                continue

            self.hidhide_devices = snapshot["devices"]
            self.hidhide_devices_loaded = True
            self.apply_hidhide_type_overrides(self.hidhide_devices)
            self.hidhide_enabled = snapshot["enabled"]
            self.hidhide_app_allowed = snapshot["app_allowed"]
            self.hidhide_app_path = snapshot["app_path"]
            if not any(device["id"] == self.hidhide_selected_id for device in self.hidhide_devices):
                self.hidhide_selected_id = None
            status_messages = {
                "refresh": "Configuración de HidHide actualizada.",
                "visibility": "Visibilidad del mando actualizada.",
                "allow": "GDM está autorizado para acceder a los mandos.",
                "cloak": "Ocultamiento activado." if self.hidhide_enabled else "Ocultamiento desactivado.",
            }
            self.hidhide_status = status_messages[value]
            self.write_console("SUCCESS", self.hidhide_status)

    def handle_hidhide_click(self, position) -> bool:
        for device in self.hidhide_devices:
            row = self.hidhide_rects.get(f"device:{device['id']}")
            if row and row.collidepoint(position):
                self.hidhide_selected_id = device["id"]
                return True
        button = self.hidhide_rects.get("device_type")
        if button and button.collidepoint(position):
            device = next(
                (item for item in self.hidhide_devices if item["id"] == self.hidhide_selected_id),
                None,
            )
            if device is not None:
                target_type = "physical" if device.get("device_type") == "virtual" else "virtual"
                identifiers = self.hidhide_device_identifiers(device)
                old_overrides = {
                    identifier: self.hidhide_type_overrides.get(identifier)
                    for identifier in identifiers
                }
                old_device_type = device.get("device_type")
                old_virtual_type = device.get("virtual_type")
                for identifier in identifiers:
                    self.hidhide_type_overrides[identifier] = target_type
                self.apply_hidhide_type_overrides([device])
                if not self.save_settings():
                    for identifier, previous_type in old_overrides.items():
                        if previous_type is None:
                            self.hidhide_type_overrides.pop(identifier, None)
                        else:
                            self.hidhide_type_overrides[identifier] = previous_type
                    device["device_type"] = old_device_type
                    device["virtual_type"] = old_virtual_type
                    self.hidhide_status = "No se pudo guardar el tipo de mando; revisa la consola."
                    return True
                label = "virtual" if target_type == "virtual" else "físico"
                self.hidhide_status = f"Dispositivo guardado como mando {label} por su ID."
                self.write_console("SUCCESS", self.hidhide_status)
            return True
        for key, operation in (
            ("refresh", "refresh"),
            ("allow", "allow"),
            ("cloak", "cloak"),
        ):
            button = self.hidhide_rects.get(key)
            if button and button.collidepoint(position):
                self.start_hidhide_task(operation)
                return True
        button = self.hidhide_rects.get("visibility")
        if button and button.collidepoint(position):
            device = next(
                (item for item in self.hidhide_devices if item["id"] == self.hidhide_selected_id),
                None,
            )
            if device is not None:
                self.start_hidhide_task("visibility", device)
            return True
        return False

    def write_console(self, level: str, message: str) -> None:
        timestamp = datetime.now().strftime("%H:%M:%S")
        for line in message.rstrip().splitlines() or [""]:
            self.console_lines.append((timestamp, level, line))
        if len(self.console_lines) > 600:
            del self.console_lines[:-600]
        self.console_scroll = 0

    def start_system_tray(self) -> None:
        if not APP_ICON_PATH.is_file():
            self.write_console("ERROR", f"No se encontró el icono para la bandeja del sistema: {APP_ICON_PATH}")
            return
        try:
            import pystray
            self.tray_icon_color = self.effective_tray_icon_color()
            image = self.make_tray_image()
            menu = pystray.Menu(
                pystray.MenuItem(
                    "Abrir GDM",
                    lambda _icon, _item: self.tray_events.put("show"),
                    default=True,
                ),
                pystray.MenuItem(
                    "Cerrar programa",
                    lambda _icon, _item: self.tray_events.put("exit"),
                ),
            )
            self.tray_icon = pystray.Icon("gdm-gamepad-manager", image, APP_NAME, menu)
            self.tray_icon.run_detached()
            self._tray_icon_theme_check_at = time.monotonic() + 5
        except (ImportError, OSError, RuntimeError) as error:
            self.tray_icon = None
            self.write_console("ERROR", f"No se pudo iniciar el icono de la bandeja del sistema: {error}")

    def effective_tray_icon_color(self) -> str:
        if self.tray_icon_mode in {"white", "black"}:
            return self.tray_icon_mode
        if platform.system() != "Windows":
            return "white"
        try:
            import winreg

            key_path = r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path) as key:
                uses_light_theme, _ = winreg.QueryValueEx(key, "SystemUsesLightTheme")
            return "black" if uses_light_theme else "white"
        except (ImportError, OSError) as error:
            if not self._tray_theme_warning_logged:
                self.write_console("WARN", f"No se pudo detectar el tema de Windows para el icono de bandeja; se usará blanco: {error}")
                self._tray_theme_warning_logged = True
            return "white"

    def make_tray_image(self):
        from PIL import Image

        with Image.open(APP_ICON_PATH) as source:
            logo = source.convert("RGBA")
        alpha = logo.getchannel("A")
        color = (255, 255, 255) if self.tray_icon_color == "white" else (0, 0, 0)
        icon = Image.new("RGBA", logo.size, (*color, 0))
        icon.putalpha(alpha)
        return icon

    def update_tray_icon(self, force: bool = False) -> None:
        now = time.monotonic()
        if not force and now < getattr(self, "_tray_icon_theme_check_at", 0):
            return
        self._tray_icon_theme_check_at = now + 5
        color = self.effective_tray_icon_color()
        if color == self.tray_icon_color and not force:
            return
        self.tray_icon_color = color
        if self.tray_icon is None:
            return
        try:
            self.tray_icon.icon = self.make_tray_image()
        except (OSError, RuntimeError) as error:
            self.write_console("ERROR", f"No se pudo actualizar el color del icono de bandeja: {error}")

    def process_tray_events(self) -> None:
        while True:
            try:
                event = self.tray_events.get_nowait()
            except queue.Empty:
                break
            if event == "show":
                try:
                    self.window.show()
                    self.window.restore()
                    self.window.focus()
                except pygame.error as error:
                    self.write_console("ERROR", f"No se pudo restaurar la ventana de GDM: {error}")
                else:
                    self.window_in_tray = False
                    self.write_console("INFO", "Ventana de GDM restaurada desde la bandeja.")
            elif event == "exit":
                self.running = False
            elif isinstance(event, tuple) and len(event) == 2 and event[0] == "error":
                self.write_console("ERROR", event[1])

    def wait_for_restore_signal(self) -> None:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.WaitForSingleObject.argtypes = (ctypes.c_void_p, ctypes.c_ulong)
        kernel32.WaitForSingleObject.restype = ctypes.c_ulong
        while not self.restore_signal_stop.is_set():
            result = kernel32.WaitForSingleObject(self.restore_event_handle, 200)
            if result == 0:
                self.tray_events.put("show")
            elif result != 258:
                error = ctypes.get_last_error()
                self.tray_events.put(("error", f"No se pudo esperar la señal para restaurar GDM: {ctypes.WinError(error)}"))
                return

    def minimize_to_tray(self) -> None:
        if self.tray_icon is None:
            self.running = False
            return
        try:
            self.window.hide()
        except pygame.error as error:
            self.write_console("ERROR", f"No se pudo minimizar GDM a la bandeja: {error}")
            self.running = False
            return
        self.window_in_tray = True
        self.write_console("INFO", "GDM continúa ejecutándose en la bandeja del sistema.")

    def close_or_minimize(self, action: str) -> None:
        self.escape_dialog_open = False
        if action == "minimize":
            self.minimize_to_tray()
        else:
            self.running = False

    def request_escape_action(self) -> None:
        if not self.escape_prompt_enabled:
            self.close_or_minimize("close")
            return
        if self.remember_escape_choice:
            self.close_or_minimize(self.escape_action)
            return
        self.escape_dialog_choice = self.escape_action
        self.escape_dialog_remember = False
        self.escape_dialog_open = True

    def handle_window_close(self) -> None:
        self.close_or_minimize(self.window_close_action)

    def draw_escape_dialog(self) -> None:
        screen_width, screen_height = self.screen.get_size()
        overlay = pygame.Surface((screen_width, screen_height), pygame.SRCALPHA)
        overlay.fill((0, 0, 0, 165))
        self.screen.blit(overlay, (0, 0))

        width = min(round(500 * self.ui_scale), screen_width - 32)
        height = round(236 * self.ui_scale)
        dialog = pygame.Rect(0, 0, width, height)
        dialog.center = (screen_width // 2, screen_height // 2)
        pygame.draw.rect(self.screen, PANEL, dialog, border_radius=14)
        pygame.draw.rect(self.screen, PANEL_LIGHT, dialog, 1, border_radius=14)
        self.draw_text("¿Qué quieres hacer?", dialog.x + 24, dialog.y + 20, TEXT, self.title_font)
        self.draw_text_fitted(
            "Puedes cerrar GDM o dejarlo ejecutándose en la bandeja.",
            dialog.x + 24,
            dialog.y + 62,
            dialog.width - 48,
            MUTED,
            self.small,
        )

        checkbox = pygame.Rect(dialog.x + 24, dialog.y + 106, 22, 22)
        self.escape_dialog_rects = {
            "remember": pygame.Rect(checkbox.x, checkbox.y, dialog.width - 48, 28),
        }
        pygame.draw.rect(self.screen, PANEL_LIGHT, checkbox, border_radius=4)
        pygame.draw.rect(
            self.screen,
            ACCENT if self.escape_dialog_remember else MUTED,
            checkbox,
            2,
            border_radius=4,
        )
        if self.escape_dialog_remember:
            pygame.draw.line(self.screen, ACTIVE, (checkbox.x + 4, checkbox.y + 11), (checkbox.x + 9, checkbox.y + 16), 3)
            pygame.draw.line(self.screen, ACTIVE, (checkbox.x + 9, checkbox.y + 16), (checkbox.x + 18, checkbox.y + 5), 3)
        self.draw_text("Recordar mi elección para las próximas veces", checkbox.right + 10, checkbox.y + 1, TEXT, self.small)

        gap = 12
        button_y = dialog.bottom - round(58 * self.ui_scale)
        button_width = (dialog.width - 48 - gap) // 2
        for index, (action, label) in enumerate((
            ("close", "Cerrar programa"),
            ("minimize", "Minimizar a la bandeja"),
        )):
            button = pygame.Rect(
                dialog.x + 24 + index * (button_width + gap),
                button_y,
                button_width,
                round(38 * self.ui_scale),
            )
            self.escape_dialog_rects[action] = button
            selected = self.escape_dialog_choice == action
            pygame.draw.rect(self.screen, PANEL_LIGHT if selected else PANEL, button, border_radius=8)
            pygame.draw.rect(
                self.screen,
                ACCENT if selected else MUTED,
                button,
                2 if selected else 1,
                border_radius=8,
            )
            label_surface = self.small.render(label, True, TEXT if selected else MUTED)
            self.screen.blit(label_surface, label_surface.get_rect(center=button.center))

    def handle_escape_dialog_click(self, position) -> None:
        remember = self.escape_dialog_rects.get("remember")
        if remember and remember.collidepoint(position):
            self.escape_dialog_remember = not self.escape_dialog_remember
            return
        for action in ("close", "minimize"):
            button = self.escape_dialog_rects.get(action)
            if button and button.collidepoint(position):
                self.escape_dialog_choice = action
                self.apply_escape_dialog_choice()
                return

    def apply_escape_dialog_choice(self) -> None:
        action = self.escape_dialog_choice
        self.remember_escape_choice = self.escape_dialog_remember
        if self.escape_dialog_remember:
            self.escape_action = action
        self.save_settings()
        self.close_or_minimize(action)

    def open_windows_game_controllers(self) -> None:
        if platform.system() != "Windows":
            self.write_console("ERROR", "El panel Dispositivos de juego solo está disponible en Windows.")
            return
        try:
            subprocess.Popen(["control.exe", "joy.cpl"])
        except OSError as error:
            self.write_console("ERROR", f"No se pudo abrir el panel Dispositivos de juego: {error}")
            return
        self.write_console("INFO", "Abriendo el panel Dispositivos de juego de Windows.")

    def draw_console(self, screen_width: int, screen_height: int) -> None:
        self.console_rects = {}
        if not self.console_visible:
            if not self.show_console_button:
                return
            toggle = pygame.Rect(screen_width - 148, screen_height - 42, 126, 32)
            self.console_rects["show"] = toggle
            self.draw_action_button(toggle, "Abrir consola", True)
            return

        width = min(screen_width - 36, 680 if self.console_expanded else 540)
        height = min(screen_height - 150, 360 if self.console_expanded else 205)
        panel = pygame.Rect(screen_width - width - 18, screen_height - height - 16, width, height)
        pygame.draw.rect(self.screen, (13, 17, 24), panel, border_radius=10)
        pygame.draw.rect(self.screen, (76, 91, 112), panel, 1, border_radius=10)
        title_height = 32
        header = pygame.Rect(panel.x, panel.y, panel.width, title_height)
        pygame.draw.rect(self.screen, (35, 43, 56), header, border_top_left_radius=10, border_top_right_radius=10)
        self.draw_text("Consola de diagnóstico", panel.x + 12, panel.y + 7, TEXT, self.small)

        buttons = (
            ("copy", "Copiar", 72),
            ("expand", "Reducir" if self.console_expanded else "Expandir", 82),
            ("hide", "Minimizar", 90),
        )
        right = panel.right - 8
        for key, label, button_width in buttons:
            button = pygame.Rect(right - button_width, panel.y + 4, button_width - 5, 24)
            self.console_rects[key] = button
            pygame.draw.rect(self.screen, PANEL_LIGHT, button, border_radius=5)
            text = self.small.render(label, True, TEXT)
            self.screen.blit(text, text.get_rect(center=button.center))
            right = button.left

        log_rect = pygame.Rect(panel.x + 10, panel.y + title_height + 5, panel.width - 20, panel.height - title_height - 12)
        self.console_rects["log"] = log_rect
        pygame.draw.rect(self.screen, (9, 12, 18), log_rect, border_radius=5)
        line_height = self.console_font.get_linesize()
        visible_lines = max(1, (log_rect.height - 8) // line_height)
        start = max(0, len(self.console_lines) - visible_lines - self.console_scroll)
        entries = self.console_lines[start:start + visible_lines]
        for index, (timestamp, level, message) in enumerate(entries):
            y = log_rect.y + 4 + index * line_height
            color = ERROR_COLOR if level == "ERROR" else SUCCESS_COLOR if level == "SUCCESS" else MUTED if level == "INFO" else TEXT
            prefix = f"{timestamp} [{level}] "
            available = max(20, log_rect.width - 16)
            prefix_surface = self.console_font.render(prefix, True, color)
            self.screen.blit(prefix_surface, (log_rect.x + 7, y))
            text_x = log_rect.x + 7 + prefix_surface.get_width()
            remaining_width = max(10, available - prefix_surface.get_width())
            clipped = self.console_font.render(message, True, color)
            if clipped.get_width() > remaining_width:
                message = message[:max(1, int(len(message) * remaining_width / clipped.get_width()) - 1)] + "…"
                clipped = self.console_font.render(message, True, color)
            self.screen.blit(clipped, (text_x, y))

    def handle_console_click(self, position) -> bool:
        for key, rect in self.console_rects.items():
            if not rect.collidepoint(position):
                continue
            if key == "show":
                self.console_visible = True
                self.save_settings()
            elif key == "hide":
                self.console_visible = False
                self.save_settings()
            elif key == "expand":
                self.console_expanded = not self.console_expanded
                self.save_settings()
            elif key == "copy":
                text = "\n".join(f"{timestamp} [{level}] {message}" for timestamp, level, message in self.console_lines)
                try:
                    copy_to_clipboard(text)
                    self.write_console("SUCCESS", "Registro copiado al portapapeles.")
                except (OSError, pygame.error) as error:
                    self.write_console("ERROR", f"No se pudo copiar el registro: {error}")
            return True
        return False

    def handle_bluetooth_click(self, position) -> bool:
        for rect, device_id in self.bluetooth_row_rects:
            if rect.collidepoint(position):
                self.bluetooth_selected_id = device_id
                return True
        selected = self.bluetooth_devices.get(self.bluetooth_selected_id or "")
        pair_rect = self.bluetooth_rects.get("pair")
        remove_rect = self.bluetooth_rects.get("remove")
        refresh_rect = self.bluetooth_rects.get("refresh")
        if pair_rect and pair_rect.collidepoint(position) and selected and not selected["paired"]:
            self.bluetooth_status = f"Emparejando {selected['name']}…"
            self.bluetooth.pair(selected["id"])
            return True
        if remove_rect and remove_rect.collidepoint(position) and selected and selected["paired"]:
            self.bluetooth_status = f"Eliminando emparejamiento de {selected['name']}…"
            self.bluetooth.unpair(selected["id"])
            return True
        if refresh_rect and refresh_rect.collidepoint(position):
            self.bluetooth_status = "Reiniciando búsqueda Bluetooth…"
            self.bluetooth.refresh()
            return True
        return False

    def handle_options_click(self, position) -> bool:
        for section in ("general", "controllers"):
            rect = self.options_rects.get(f"section_{section}")
            if rect and rect.collidepoint(position):
                self.options_section = section
                return True

        for tab in ("devices", "tester", "bluetooth", "remap", "hidhide", "options", "about"):
            rect = self.options_rects.get(f"startup_{tab}")
            if rect and rect.collidepoint(position):
                self.startup_tab = tab
                self.save_settings()
                return True

        for mode in ("auto", "white", "black"):
            rect = self.options_rects.get(f"tray_icon_mode:{mode}")
            if rect and rect.collidepoint(position):
                self.tray_icon_mode = mode
                self.save_settings()
                self.update_tray_icon(force=True)
                return True

        if self.options_rects.get("escape_prompt_enabled", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            self.escape_prompt_enabled = not self.escape_prompt_enabled
            if not self.escape_prompt_enabled:
                self.remember_escape_choice = False
            self.save_settings()
            return True
        if self.options_rects.get("remember_escape_choice", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            if self.escape_prompt_enabled:
                self.remember_escape_choice = not self.remember_escape_choice
                self.save_settings()
            return True
        if self.options_rects.get("show_console_button", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            self.show_console_button = not self.show_console_button
            self.save_settings()
            return True
        for action in ("close", "minimize"):
            rect = self.options_rects.get(f"window_close:{action}")
            if rect and rect.collidepoint(position):
                self.window_close_action = action
                self.save_settings()
                return True

        if self.options_rects.get("auto", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            self.bluetooth_auto_pair = not self.bluetooth_auto_pair
            self.bluetooth.set_auto_pair(self.bluetooth_auto_pair)
            self.save_settings()
            if self.bluetooth_auto_pair:
                self.bluetooth.refresh()
            return True
        if self.options_rects.get("gamepads", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            self.bluetooth_gamepads_only = not self.bluetooth_gamepads_only
            self.save_settings()
            if self.bluetooth_selected_id:
                selected = self.bluetooth_devices.get(self.bluetooth_selected_id)
                if selected is None or (self.bluetooth_gamepads_only and not selected["gamepad"]):
                    self.bluetooth_selected_id = None
            return True
        if self.options_rects.get("launch_at_startup", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            if self.startup_busy:
                return True
            previous_value = self.launch_at_startup
            self.launch_at_startup = not previous_value
            self.save_settings()
            self.start_windows_startup_update(
                self.launch_at_startup,
                "launch_at_startup",
                previous_value,
            )
            return True
        if self.options_rects.get("start_minimized_in_tray", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            if self.startup_busy:
                return True
            previous_value = self.start_minimized_in_tray
            self.start_minimized_in_tray = not previous_value
            self.save_settings()
            if self.launch_at_startup:
                self.start_windows_startup_update(
                    self.launch_at_startup,
                    "start_minimized_in_tray",
                    previous_value,
                )
            return True
        if self.options_rects.get("home_disconnect", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            self.disconnect_on_home_hold = not self.disconnect_on_home_hold
            self.home_hold_started = None
            self.home_hold_triggered = False
            self.save_settings()
            return True
        for action in ("disconnect", "unpair"):
            rect = self.options_rects.get(f"home_action_{action}")
            if rect and rect.collidepoint(position):
                self.home_hold_action = action
                self.save_settings()
                return True
        return False

    def handle_remap_click(self, position) -> bool:
        for profile in PROFILE_NAMES:
            rect = self.remap_rects.get(f"profile:{profile}")
            if rect and rect.collidepoint(position):
                self.remap_profile = profile
                self._remap_profile_user_selected = True
                return True

        for output_type in OUTPUT_TYPES:
            rect = self.remap_rects.get(f"output:{output_type}")
            if rect and rect.collidepoint(position):
                self.virtual_profile_outputs[self.remap_profile] = output_type
                self.save_settings()
                return True

        toggle = self.remap_rects.get("toggle")
        if toggle and toggle.collidepoint(position):
            self.virtual_output_enabled = not self.virtual_output_enabled
            if self.virtual_output_enabled:
                source = self.joystick if self.joystick is not None and not self.is_virtual_gamepad(self.joystick) else None
                source = source or self.find_physical_gamepad()
                try:
                    self.virtual_output_source_id = source.get_instance_id() if source is not None else None
                except pygame.error as error:
                    self.virtual_output_source_id = None
                    self.write_console("WARN", f"No se pudo identificar el mando de origen de la salida virtual: {error}")
            else:
                self.virtual_output_source_id = None
            self.save_settings()
            return True
        auto_start = self.remap_rects.get("auto_start")
        if auto_start and auto_start.collidepoint(position):
            self.virtual_output_auto_start = not self.virtual_output_auto_start
            self.save_settings()
            return True
        for color_key in PLAYSTATION_LED_COLORS:
            rect = self.remap_rects.get(f"playstation_led:{color_key}")
            if rect and rect.collidepoint(position):
                joystick = self.playstation_led_target()
                available, message = self.playstation_led_availability(joystick)
                if not available:
                    self.playstation_led_status = message
                    return True
                self.playstation_led_color = color_key
                self._playstation_led_last_attempt = None
                self.playstation_led_status = "Aplicando color al mando PlayStation físico…"
                self.save_settings()
                return True

        target_labels = button_target_labels(self.virtual_profile_outputs[self.remap_profile])
        targets: tuple[Optional[str], ...] = tuple(target_labels) + (None,)
        for button_id in REMAP_BUTTON_IDS:
            rect = self.remap_rects.get(f"mapping:{button_id}")
            if not rect or not rect.collidepoint(position):
                continue
            mapping = self.virtual_profile_mappings[self.remap_profile]
            current = mapping.get(button_id)
            try:
                next_index = (targets.index(current) + 1) % len(targets)
            except ValueError:
                next_index = 0
            mapping[button_id] = targets[next_index]
            self.save_settings()
            return True
        return False

    def update_virtual_gamepad(self) -> None:
        if not self.virtual_output_enabled:
            self.virtual_output_source_id = None
            joystick = self.joystick
        else:
            if self.virtual_output_source_id is None:
                source = self.find_physical_gamepad()
                if source is None and self.joystick is not None and not self.is_virtual_gamepad(self.joystick):
                    source = self.joystick
                if source is not None:
                    try:
                        self.virtual_output_source_id = source.get_instance_id()
                    except pygame.error as error:
                        self.write_console("WARN", f"No se pudo identificar el mando de origen de la salida virtual: {error}")

            joystick = None
            if self.virtual_output_source_id is not None:
                for device in self.joysticks:
                    try:
                        if device.get_init() and device.get_instance_id() == self.virtual_output_source_id:
                            joystick = device
                            break
                    except pygame.error:
                        continue

        profile = self.remap_profile
        if joystick is not None:
            profile = self.model_name(
                joystick.get_name(),
                joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0,
            )
        status = self.virtual_bridge.update(
            self.virtual_output_enabled,
            self.virtual_profile_outputs[profile],
            joystick,
            self.controller_for(joystick),
            self.virtual_profile_mappings[profile],
        )
        if status != self.virtual_output_status:
            self.virtual_output_status = status
            level = "ERROR" if "No se pudo" in status or "Falta" in status else "INFO"
            self.write_console(level, status)

    def check_home_hold(self) -> None:
        if self.pending_home_action:
            if time.monotonic() >= self.pending_home_deadline:
                self.pending_home_action = None
                self.pending_home_joystick = None
                self.bluetooth_status = "No se encontró el mando Bluetooth para ejecutar la acción solicitada."
                self.write_console("ERROR", self.bluetooth_status)
            return
        joystick = self.home_action_joystick()
        if not self.disconnect_on_home_hold or joystick is None:
            self.home_hold_started = None
            self.home_hold_triggered = False
            return
        pressed = self.home_button_pressed(joystick)

        if not pressed:
            self.home_hold_started = None
            self.home_hold_triggered = False
            return
        if self.home_hold_started is None:
            self.home_hold_started = pygame.time.get_ticks()
            self.write_console("INFO", "Home/PS presionado; mantenlo 2 segundos para solicitar la desconexión.")
            return
        if self.home_hold_triggered or pygame.time.get_ticks() - self.home_hold_started < 2000:
            return

        self.home_hold_triggered = True
        match = self.find_bluetooth_device(joystick)
        if match is None:
            self.pending_home_action = self.home_hold_action
            self.pending_home_joystick = joystick
            self.pending_home_deadline = time.monotonic() + 10
            self.bluetooth_status = "Actualizando Bluetooth para encontrar el mando…"
            self.write_console("INFO", self.bluetooth_status)
            self.bluetooth.refresh(retry_auto_pair=False)
            return

        self.execute_home_action(self.home_hold_action, match)

    def home_action_joystick(self):
        if self.virtual_output_enabled and self.virtual_output_source_id is not None:
            for joystick in self.joysticks:
                try:
                    if joystick.get_init() and joystick.get_instance_id() == self.virtual_output_source_id:
                        return joystick
                except pygame.error:
                    continue
            return None

        joystick = self.joystick
        if joystick is not None and not self.is_virtual_gamepad(joystick):
            return joystick
        return self.find_physical_gamepad()

    def home_button_pressed(self, joystick) -> bool:
        controller = self.controller_for(joystick)
        if controller is not None:
            try:
                if controller.get_button(pygame.CONTROLLER_BUTTON_GUIDE):
                    return True
            except pygame.error as error:
                self.write_console("WARN", f"No se pudo leer Home/PS mediante SDL: {error}")

        try:
            model = self.model_name(
                joystick.get_name(),
                joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0,
            )
            return model in {"Xbox", "PlayStation"} and self.pressed(joystick, 5)
        except pygame.error as error:
            self.write_console("ERROR", f"No se pudo leer el botón Home/PS del mando: {error}")
            return False

    def execute_home_action(self, action: str, match: dict) -> None:
        if action == "unpair":
            self.bluetooth_status = f"Desemparejando {match['name']} de Windows…"
            self.write_console("WARN", self.bluetooth_status)
            self.bluetooth.unpair(match["id"], retry_auto_pair=True)
        else:
            self.bluetooth_status = f"Solicitando desconexión de {match['name']} en Windows…"
            self.write_console("INFO", self.bluetooth_status)
            self.bluetooth.disconnect(match["id"])

    def try_pending_home_action(self) -> None:
        joystick = self.pending_home_joystick
        if joystick is None or self.pending_home_action is None:
            return
        match = self.find_bluetooth_device(joystick)
        if match is None:
            return
        action = self.pending_home_action
        self.pending_home_action = None
        self.pending_home_joystick = None
        self.pending_home_deadline = 0.0
        self.execute_home_action(action, match)

    def find_bluetooth_device(self, joystick) -> Optional[dict]:
        gamepads = [
            device for device in self.bluetooth_devices.values()
            if device.get("gamepad")
        ]
        joystick_name = joystick.get_name().strip().casefold()
        exact_name_matches = [
            device for device in gamepads
            if device["name"].strip().casefold() == joystick_name
        ]
        if exact_name_matches:
            return self.preferred_bluetooth_match(exact_name_matches)

        vendor = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
        product = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
        if vendor and product:
            hardware_token = f"vid_{vendor:04x}&pid_{product:04x}"
            id_matches = [
                device for device in gamepads
                if hardware_token in " ".join(
                    str(value) for value in (
                        device.get("hardware_ids", []),
                        device.get("device_instance_id", ""),
                    )
                ).casefold()
            ]
            if id_matches:
                return self.preferred_bluetooth_match(id_matches)

        model = self.model_name(joystick_name, vendor)
        if model in {"Xbox", "PlayStation"}:
            family_matches = [
                device for device in gamepads
                if self.model_name(device.get("name", "")) == model
            ]
            connected_matches = [
                device for device in family_matches if device.get("connected")
            ]
            if len(connected_matches) == 1:
                return connected_matches[0]
            if len(family_matches) == 1:
                return family_matches[0]
        return None

    @staticmethod
    def preferred_bluetooth_match(devices: list[dict]) -> Optional[dict]:
        return max(
            devices,
            key=lambda device: (
                bool(device.get("connected")),
                bool(device.get("paired")),
                device.get("transport") == "Bluetooth LE",
            ),
            default=None,
        )

    def bluetooth_device_has_live_joystick(self, device: dict) -> bool:
        device_name = device.get("name", "").strip().casefold()
        for joystick in self.joysticks:
            try:
                if not joystick.get_init():
                    continue
                joystick_name = joystick.get_name().strip().casefold()
            except pygame.error:
                continue
            if joystick_name and (
                joystick_name == device_name
                or joystick_name in device_name
                or device_name in joystick_name
            ):
                return True
        return False

    def draw_left_panel(self, rect: pygame.Rect, joystick) -> None:
        self.draw_panel(rect, "Lecturas en vivo")
        x, y = rect.x + 20, rect.y + 58
        inner_width = rect.width - 40
        column_width = inner_width // 2
        line_height = max(16, round(22 * self.ui_scale))
        section_gap = max(8, round(12 * self.ui_scale))
        if joystick is None:
            self.draw_text_fitted("Conecta un mando para empezar.", x, y, inner_width, MUTED)
            self.draw_text_fitted("Pulsa R para volver a buscar dispositivos.", x, y + 30, inner_width, MUTED, self.small)
            return

        name = joystick.get_name()
        vendor = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
        product = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
        self.start_transport_detection(joystick)
        with self.transport_lock:
            transport = self.transport
        device_count = len(self.joysticks)
        device_label = f"Mando {self.selected + 1}/{device_count}: {name[:33]}"
        self.draw_text_fitted(device_label, x, y, inner_width)
        y += line_height + 3
        self.draw_text_fitted(f"Modelo: {self.model_name(name, vendor)}", x, y, inner_width, MUTED, self.small)
        if inner_width < 390:
            y += line_height
            self.draw_text_fitted(
                f"Transporte: {transport}", x, y, inner_width,
                WARN if transport in ("Ambiguo", "Error de detección") else MUTED, self.small,
            )
        else:
            self.draw_text_fitted(
                f"Transporte: {transport}", x + column_width, y, column_width,
                WARN if transport in ("Ambiguo", "Error de detección") else MUTED, self.small,
            )
        y += line_height
        self.draw_text_fitted(
            f"ID SDL: {joystick.get_instance_id()}    GUID: {joystick.get_guid()}",
            x, y, inner_width, MUTED, self.small,
        )
        y += line_height
        sdl_version = ".".join(str(part) for part in pygame.get_sdl_version())
        self.draw_text_fitted(
            f"VID:PID {vendor:04X}:{product:04X}    SDL: {sdl_version}",
            x, y, inner_width, MUTED, self.small,
        )
        y += line_height
        self.draw_text_fitted("Firmware: no expuesto por SDL", x, y, inner_width, MUTED, self.small)
        y += line_height
        self.draw_text_fitted(f"Batería: {self.battery_text(joystick)}", x, y, inner_width, MUTED, self.small)
        y += line_height + section_gap
        pygame.draw.line(self.screen, PANEL_LIGHT, (x, y), (rect.right - 20, y), 1)
        y += section_gap

        controller = self.controller_for(joystick)
        if controller:
            self.draw_text_fitted("Ejes SDL normalizados", x, y, inner_width, ACCENT, self.small)
            y += line_height + 3
            for index, (label, axis, trigger) in enumerate(SDL_AXES):
                value = self.mapped_axis(controller, axis, trigger)
                shown = f"{value:+.3f}" if not trigger else f"{value:.3f}"
                self.draw_text_fitted(
                    f"{label}: {shown}", x + (index % 2) * column_width,
                    y + (index // 2) * line_height, column_width, TEXT, self.small,
                )
            y += 3 * line_height + section_gap

            self.draw_text_fitted("Botones SDL normalizados", x, y, inner_width, ACCENT, self.small)
            y += line_height + 3
            for index, (label, button) in enumerate(SDL_BUTTONS):
                pressed = self.mapped_button(controller, button)
                state = ("Pulsado" if self.ui_scale < 0.9 else "PRESIONADO") if pressed else "libre"
                self.draw_text_fitted(
                    f"{label}: {state}", x + (index // 8) * column_width,
                    y + (index % 8) * line_height, column_width,
                    ACTIVE if pressed else MUTED, self.small,
                )
        else:
            axis_count = joystick.get_numaxes()
            self.draw_text_fitted(f"Ejes directos ({axis_count})", x, y, inner_width, ACCENT, self.small)
            y += line_height + 3
            for axis in range(axis_count):
                value = joystick.get_axis(axis)
                self.draw_text_fitted(
                    f"Eje {axis}: {value:+.3f}", x + (axis % 2) * column_width,
                    y + (axis // 2) * line_height, column_width, TEXT, self.small,
                )
            y += ((axis_count + 1) // 2) * line_height + section_gap

            self.draw_text_fitted(f"Botones directos ({joystick.get_numbuttons()})", x, y, inner_width, ACCENT, self.small)
            y += line_height + 3
            max_rows = max(1, (rect.bottom - y - 24) // line_height)
            button_count = joystick.get_numbuttons()
            rows = min(max_rows, (button_count + 1) // 2)
            for button in range(button_count):
                column = button // rows if rows else 0
                row = button % rows if rows else button
                if column > 1:
                    break
                pressed = joystick.get_button(button)
                color = ACTIVE if pressed else MUTED
                state = ("Pulsado" if self.ui_scale < 0.9 else "PRESIONADO") if pressed else "libre"
                self.draw_text_fitted(
                    f"Botón {button}: {state}", x + column * column_width,
                    y + row * line_height, column_width, color, self.small,
                )
            if button_count > rows * 2:
                self.draw_text("Los índices directos dependen del modelo.", x, rect.bottom - 26, MUTED, self.small)

    @staticmethod
    def battery_text(joystick) -> str:
        names = {
            -1: "desconocida",
            0: "vacía",
            1: "baja",
            2: "media",
            3: "llena",
            4: "con alimentación",
        }
        if hasattr(joystick, "get_power_level"):
            try:
                return names.get(joystick.get_power_level(), "no disponible")
            except pygame.error:
                return "no disponible"
        library = sdl_power_api()
        if library is None:
            return "no disponible"
        handle = library.SDL_JoystickFromInstanceID(joystick.get_instance_id())
        if not handle:
            return "no disponible"
        level = library.SDL_JoystickCurrentPowerLevel(handle)
        return names.get(level, "no disponible")

    @staticmethod
    def pressed(joystick, index: int) -> bool:
        return joystick is not None and index < joystick.get_numbuttons() and bool(joystick.get_button(index))

    @staticmethod
    def axis_value(joystick, index: int) -> float:
        return joystick.get_axis(index) if joystick is not None and index < joystick.get_numaxes() else 0.0

    def draw_button(self, center, radius: int, label: str, active: bool, font=None, scale: float = 1.0) -> None:
        color = ACTIVE if active else TEXT
        pygame.draw.circle(self.screen, color, center, radius)
        pygame.draw.circle(self.screen, ACTIVE if active else PAD_EDGE, center, radius, max(1, round(2 * scale)))
        text = (font or self.small).render(label, True, BG if active else PAD_BODY)
        self.screen.blit(text, text.get_rect(center=center))

    def draw_face_button(self, center, radius: int, button: int, label: str, active: bool, model: str, scale: float) -> None:
        fill = ACTIVE if active else TEXT
        outline = BG if active else PAD_BODY
        pygame.draw.circle(self.screen, fill, center, radius)
        pygame.draw.circle(self.screen, ACTIVE if active else PAD_EDGE, center, radius, max(1, round(2 * scale)))
        if model != "PlayStation":
            xbox_colors = {
                pygame.CONTROLLER_BUTTON_A: (132, 211, 105),
                pygame.CONTROLLER_BUTTON_B: (225, 83, 78),
                pygame.CONTROLLER_BUTTON_X: (55, 177, 226),
                pygame.CONTROLLER_BUTTON_Y: (242, 196, 58),
            }
            glyph_color = outline if active else xbox_colors.get(button, outline)
            glyph = self.small.render(label, True, glyph_color)
            self.screen.blit(glyph, glyph.get_rect(center=center))
            return

        half = max(2, round(radius * 0.43))
        stroke = max(1, round(radius * 0.11))
        color = outline if active else {
            pygame.CONTROLLER_BUTTON_Y: (77, 199, 151),
            pygame.CONTROLLER_BUTTON_B: (231, 105, 115),
            pygame.CONTROLLER_BUTTON_A: (116, 164, 255),
            pygame.CONTROLLER_BUTTON_X: (207, 137, 205),
        }[button]
        cx, cy = center
        if button == pygame.CONTROLLER_BUTTON_Y:
            pygame.draw.polygon(
                self.screen,
                color,
                ((cx, cy - half), (cx - half, cy + half), (cx + half, cy + half)),
                stroke,
            )
        elif button == pygame.CONTROLLER_BUTTON_X:
            square = pygame.Rect(cx - half, cy - half, half * 2, half * 2)
            pygame.draw.rect(self.screen, color, square, stroke)
        elif button == pygame.CONTROLLER_BUTTON_B:
            pygame.draw.circle(self.screen, color, center, half, stroke)
        else:
            pygame.draw.line(self.screen, color, (cx - half, cy - half), (cx + half, cy + half), stroke)
            pygame.draw.line(self.screen, color, (cx - half, cy + half), (cx + half, cy - half), stroke)

    def draw_stick(self, center, x_value: float, y_value: float, label: str, scale: float = 1.0) -> None:
        cx, cy = center
        pygame.draw.circle(self.screen, (151, 160, 176), center, max(1, round(28 * scale)))
        pygame.draw.circle(self.screen, PAD_EDGE, center, max(1, round(28 * scale)), max(1, round(2 * scale)))
        knob = (int(cx + x_value * 15 * scale), int(cy + y_value * 15 * scale))
        pygame.draw.circle(self.screen, ACTIVE if abs(x_value) + abs(y_value) > 0.08 else (171, 180, 195), knob, max(1, round(20 * scale)))
        pygame.draw.circle(self.screen, PAD_BODY, knob, max(1, round(20 * scale)), max(1, round(2 * scale)))
        label_surface = self.small.render(label, True, TEXT)
        self.screen.blit(label_surface, label_surface.get_rect(center=(cx, cy + round(48 * scale))))

    def draw_pad(self, rect: pygame.Rect, joystick) -> None:
        self.draw_panel(rect, "Vista del mando")
        if joystick is None:
            self.draw_text("El dibujo se activará al conectar un mando.", rect.x + 22, rect.y + 65, MUTED, self.small)
            return

        controller = self.controller_for(joystick)
        model = self.model_name(joystick.get_name(), joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0)
        available_width = max(1, rect.width - 44)
        available_height = max(1, rect.height - 112)
        design_width, design_height = (440, 310) if model == "Xbox" else (394, 310)
        pad_scale = min(available_width / design_width, available_height / design_height)
        body = pygame.Rect(0, 0, round(design_width * pad_scale), round(design_height * pad_scale))
        body.center = (rect.centerx, rect.y + 65 + available_height // 2)
        w, h = body.size
        point = lambda x, y: (body.x + round(x * w), body.y + round(y * h))
        size = lambda value: max(1, round(value * pad_scale))
        silhouette = []

        def curve_to(c1, c2, end) -> None:
            start = silhouette[-1]
            p1, p2, p3 = point(*c1), point(*c2), point(*end)
            for step in range(1, 13):
                t = step / 12
                inverse = 1 - t
                x = inverse**3 * start[0] + 3 * inverse**2 * t * p1[0] + 3 * inverse * t**2 * p2[0] + t**3 * p3[0]
                y = inverse**3 * start[1] + 3 * inverse**2 * t * p1[1] + 3 * inverse * t**2 * p2[1] + t**3 * p3[1]
                silhouette.append((round(x), round(y)))

        if model == "Xbox":
            silhouette.append(point(0.06, 0.31))
            shell_curves = (
                ((0.09, 0.15), (0.15, 0.04), (0.28, 0.08)),
                ((0.35, 0.10), (0.40, 0.15), (0.50, 0.15)),
                ((0.60, 0.15), (0.65, 0.10), (0.72, 0.08)),
                ((0.85, 0.04), (0.91, 0.15), (0.94, 0.31)),
                ((0.99, 0.48), (1.00, 0.73), (0.98, 0.84)),
                ((0.96, 0.98), (0.86, 1.02), (0.77, 0.91)),
                ((0.68, 0.84), (0.63, 0.78), (0.56, 0.78)),
                ((0.53, 0.78), (0.47, 0.78), (0.44, 0.78)),
                ((0.37, 0.78), (0.32, 0.84), (0.23, 0.91)),
                ((0.14, 1.02), (0.04, 0.98), (0.02, 0.84)),
                ((0.00, 0.73), (0.01, 0.48), (0.06, 0.31)),
            )
        elif model == "PlayStation":
            silhouette.append(point(0.18, 0.09))
            shell_curves = (
                ((0.27, 0.01), (0.37, 0.10), (0.47, 0.12)),
                ((0.49, 0.12), (0.49, 0.09), (0.50, 0.08)),
                ((0.51, 0.09), (0.51, 0.12), (0.53, 0.12)),
                ((0.63, 0.10), (0.73, 0.01), (0.82, 0.08)),
                ((0.93, 0.15), (0.94, 0.38), (0.98, 0.72)),
                ((1.01, 0.94), (0.87, 1.02), (0.77, 0.86)),
                ((0.69, 0.73), (0.64, 0.64), (0.58, 0.65)),
                ((0.54, 0.67), (0.46, 0.67), (0.42, 0.65)),
                ((0.36, 0.64), (0.31, 0.73), (0.23, 0.86)),
                ((0.13, 1.02), (-0.01, 0.94), (0.02, 0.72)),
                ((0.06, 0.38), (0.07, 0.15), (0.18, 0.09)),
            )
        else:
            silhouette.append(point(0.22, 0.05))
            shell_curves = (
                ((0.31, 0.00), (0.38, 0.12), (0.47, 0.12)),
                ((0.49, 0.12), (0.49, 0.12), (0.50, 0.08)),
                ((0.51, 0.12), (0.51, 0.12), (0.53, 0.12)),
                ((0.62, 0.12), (0.69, 0.00), (0.78, 0.05)),
                ((0.91, 0.09), (0.93, 0.31), (0.98, 0.70)),
                ((1.02, 0.94), (0.86, 1.04), (0.77, 0.86)),
                ((0.68, 0.69), (0.65, 0.63), (0.59, 0.65)),
                ((0.55, 0.69), (0.45, 0.69), (0.41, 0.65)),
                ((0.35, 0.63), (0.32, 0.70), (0.23, 0.86)),
                ((0.14, 1.04), (-0.02, 0.94), (0.02, 0.70)),
                ((0.07, 0.31), (0.09, 0.09), (0.22, 0.05)),
            )
        for control_1, control_2, end in shell_curves:
            curve_to(control_1, control_2, end)

        body_color = (34, 39, 44) if model == "Xbox" else PAD_BODY
        pygame.draw.polygon(self.screen, body_color, silhouette)
        pygame.draw.lines(self.screen, PAD_EDGE, True, silhouette, size(3))
        if model != "Xbox":
            pygame.draw.lines(
                self.screen,
                (57, 76, 105),
                False,
                [point(0.12, 0.25), point(0.08, 0.49), point(0.07, 0.72)],
                size(2),
            )
            pygame.draw.lines(
                self.screen,
                (57, 76, 105),
                False,
                [point(0.88, 0.25), point(0.92, 0.49), point(0.93, 0.72)],
                size(2),
            )

        def button_active(button: int, raw_index: int) -> bool:
            return self.mapped_button(controller, button) if controller else self.pressed(joystick, raw_index)

        def axis_value(axis: int, raw_index: int, trigger: bool = False) -> float:
            return self.mapped_axis(controller, axis, trigger) if controller else (
                max(0.0, (self.axis_value(joystick, raw_index) + 1) / 2)
                if trigger else self.axis_value(joystick, raw_index)
            )

        # Shoulder and trigger controls sit along the upper edge of the body.
        for trigger_label, shoulder_label, shoulder_button, raw_shoulder, axis, raw_axis, px in (
            ("L2", "L1", pygame.CONTROLLER_BUTTON_LEFTSHOULDER, 9, pygame.CONTROLLER_AXIS_TRIGGERLEFT, 4, 0.25),
            ("R2", "R1", pygame.CONTROLLER_BUTTON_RIGHTSHOULDER, 10, pygame.CONTROLLER_AXIS_TRIGGERRIGHT, 5, 0.75),
        ):
            value = axis_value(axis, raw_axis, True)
            bar = pygame.Rect(*point(px - 0.12, 0.04), size(95), size(14))
            pygame.draw.rect(self.screen, PANEL_LIGHT, bar, border_radius=size(7))
            pygame.draw.rect(
                self.screen,
                ACTIVE if value > 0.08 else TEXT,
                (bar.x, bar.y, round(bar.width * value), bar.height),
                border_radius=size(7),
            )
            self.draw_text(f"{trigger_label} {value:.2f}", bar.x, bar.y - size(21), TEXT, self.small)
            shoulder = pygame.Rect(*point(px - 0.10, 0.12), size(79), size(19))
            active = button_active(shoulder_button, raw_shoulder)
            pygame.draw.rect(self.screen, ACTIVE if active else TEXT, shoulder, border_radius=size(9))
            pygame.draw.rect(self.screen, ACTIVE if active else PAD_EDGE, shoulder, size(1), border_radius=size(9))
            text = self.small.render(shoulder_label, True, BG if active else PAD_BODY)
            self.screen.blit(text, text.get_rect(center=shoulder.center))

        face_labels = ("×", "○", "□", "△") if model == "PlayStation" else ("A", "B", "X", "Y")
        # SDL face positions are standardized: A south, B east, X west, Y north.
        face_positions = {
            "PlayStation": ((0.75, 0.30), (0.68, 0.40), (0.82, 0.40), (0.75, 0.50)),
            "Xbox": ((0.77, 0.31), (0.70, 0.42), (0.84, 0.42), (0.77, 0.53)),
            "Genérico": ((0.75, 0.30), (0.68, 0.40), (0.82, 0.40), (0.75, 0.50)),
        }
        face = (
            (pygame.CONTROLLER_BUTTON_Y, 3, face_labels[3], face_positions[model][0]),
            (pygame.CONTROLLER_BUTTON_X, 2, face_labels[2], face_positions[model][1]),
            (pygame.CONTROLLER_BUTTON_B, 1, face_labels[1], face_positions[model][2]),
            (pygame.CONTROLLER_BUTTON_A, 0, face_labels[0], face_positions[model][3]),
        )
        for mapped, raw, label, location in face:
            self.draw_face_button(
                point(*location), size(20), mapped, label, button_active(mapped, raw), model, pad_scale,
            )

        # A cross-shaped D-pad like the reference icon.
        dpad_center = point(0.38, 0.64) if model == "Xbox" else point(0.25, 0.40)
        dpad_rect = pygame.Rect(dpad_center[0] - size(17), dpad_center[1] - size(43), size(34), size(86))
        pygame.draw.rect(self.screen, TEXT, dpad_rect, border_radius=size(10))
        pygame.draw.rect(self.screen, TEXT, (dpad_center[0] - size(43), dpad_center[1] - size(17), size(86), size(34)), border_radius=size(10))
        for mapped, raw, center, direction in (
            (pygame.CONTROLLER_BUTTON_DPAD_UP, 11, (dpad_center[0], dpad_center[1] - size(27)), "↑"),
            (pygame.CONTROLLER_BUTTON_DPAD_DOWN, 12, (dpad_center[0], dpad_center[1] + size(27)), "↓"),
            (pygame.CONTROLLER_BUTTON_DPAD_LEFT, 13, (dpad_center[0] - size(27), dpad_center[1]), "←"),
            (pygame.CONTROLLER_BUTTON_DPAD_RIGHT, 14, (dpad_center[0] + size(27), dpad_center[1]), "→"),
        ):
            if button_active(mapped, raw):
                pygame.draw.circle(self.screen, ACTIVE, center, size(14))
            text = self.small.render(direction, True, PAD_BODY)
            self.screen.blit(text, text.get_rect(center=center))

        system_buttons = (
            (("SELECT", pygame.CONTROLLER_BUTTON_BACK, 4, (0.43, 0.34)),
             ("START", pygame.CONTROLLER_BUTTON_START, 6, (0.58, 0.34)))
            if model == "Xbox" else
            (("SELECT", pygame.CONTROLLER_BUTTON_BACK, 4, (0.42, 0.40)),
             ("START", pygame.CONTROLLER_BUTTON_START, 6, (0.58, 0.40)))
        )
        if model == "PlayStation":
            touchpad = pygame.Rect(*point(0.425, 0.22), size(59), size(32))
            touchpad_pressed = button_active(SDL_CONTROLLER_BUTTON_TOUCHPAD, 17)
            touchpad_fill = (45, 96, 91) if touchpad_pressed else (35, 49, 70)
            pygame.draw.rect(self.screen, touchpad_fill, touchpad, border_radius=size(7))
            pygame.draw.rect(
                self.screen,
                ACTIVE if touchpad_pressed else PAD_EDGE,
                touchpad,
                size(2),
                border_radius=size(7),
            )
            pygame.draw.line(self.screen, (87, 108, 140), point(0.44, 0.24), point(0.56, 0.24), size(1))
        elif model == "Xbox":
            center_button = point(0.50, 0.19)
            home_active = button_active(pygame.CONTROLLER_BUTTON_GUIDE, 5)
            pygame.draw.circle(self.screen, ACTIVE if home_active else PANEL_LIGHT, center_button, size(12))
            pygame.draw.circle(self.screen, PAD_EDGE, center_button, size(12), size(2))
            pygame.draw.line(
                self.screen, BG, (center_button[0] - size(5), center_button[1]),
                (center_button[0] + size(5), center_button[1]), size(2),
            )
        for label, mapped, raw, location in system_buttons:
            center = point(*location)
            pill = pygame.Rect(center[0] - size(28), center[1] - size(10), size(56), size(20))
            active = button_active(mapped, raw)
            pygame.draw.rect(self.screen, ACTIVE if active else (48, 63, 88), pill, border_radius=size(10))
            pygame.draw.rect(self.screen, PAD_EDGE, pill, size(1), border_radius=size(10))
            text = self.small.render(label, True, BG if active else TEXT)
            self.screen.blit(text, text.get_rect(center=center))

        left_stick = (pygame.CONTROLLER_AXIS_LEFTX, pygame.CONTROLLER_AXIS_LEFTY, 0, 1)
        right_stick = (pygame.CONTROLLER_AXIS_RIGHTX, pygame.CONTROLLER_AXIS_RIGHTY, 2, 3)
        stick_positions = (
            ((0.25, 0.35), (0.63, 0.58))
            if model == "Xbox" else
            ((0.39, 0.70), (0.61, 0.70))
        )
        self.draw_stick(point(*stick_positions[0]), axis_value(left_stick[0], left_stick[2]), axis_value(left_stick[1], left_stick[3]), "L3", pad_scale)
        self.draw_stick(point(*stick_positions[1]), axis_value(right_stick[0], right_stick[2]), axis_value(right_stick[1], right_stick[3]), "R3", pad_scale)

        mapping_label = "Sincronizado con mapeo SDL" if controller else "Modo directo: índices según modelo"
        self.draw_text(mapping_label, rect.x + 22, rect.bottom - 26, MUTED, self.small)

    def handle_events(self) -> None:
        self.process_bluetooth_events()
        self.process_hidhide_events()
        self.process_startup_events()
        self.process_tray_events()
        for event in pygame.event.get():
            if self.escape_dialog_open and event.type == pygame.MOUSEBUTTONDOWN and event.button == 1:
                self.handle_escape_dialog_click(event.pos)
                continue
            if self.escape_dialog_open and event.type == pygame.KEYDOWN:
                if event.key == pygame.K_ESCAPE:
                    self.escape_dialog_open = False
                    continue
                if event.key == pygame.K_LEFT:
                    self.escape_dialog_choice = "close"
                    continue
                if event.key == pygame.K_RIGHT:
                    self.escape_dialog_choice = "minimize"
                    continue
                if event.key in (pygame.K_RETURN, pygame.K_KP_ENTER):
                    self.apply_escape_dialog_choice()
                    continue
            if event.type in (pygame.QUIT, pygame.WINDOWCLOSE):
                self.handle_window_close()
            elif event.type in (pygame.VIDEORESIZE, pygame.WINDOWRESIZED, pygame.WINDOWSIZECHANGED):
                width = getattr(event, "w", getattr(event, "x", self.window_size[0]))
                height = getattr(event, "h", getattr(event, "y", self.window_size[1]))
                self.window_size = (
                    max(MIN_WINDOW_WIDTH, int(width)),
                    max(MIN_WINDOW_HEIGHT, int(height)),
                )
                self._window_resize_deadline = time.monotonic() + 0.4
            elif event.type in (
                pygame.JOYDEVICEADDED,
                pygame.JOYDEVICEREMOVED,
                pygame.CONTROLLERDEVICEADDED,
                pygame.CONTROLLERDEVICEREMOVED,
            ):
                self._device_refresh_deadline = time.monotonic() + 0.3
            elif event.type == pygame.MOUSEBUTTONDOWN and event.button == 1:
                for tab, rect in self.nav_rects.items():
                    if rect.collidepoint(event.pos):
                        self.active_tab = tab
                        if tab == "hidhide":
                            self.start_hidhide_task("refresh")
                        elif (
                            tab == "devices"
                            and not self.hidhide_devices_loaded
                            and self.hidhide_service.cli_path is not None
                        ):
                            self.start_hidhide_task("refresh")
                        break
                else:
                    console_clicked = self.handle_console_click(event.pos)
                    if not console_clicked:
                        if self.active_tab == "devices":
                            if self.devices_rects.get("windows_panel", pygame.Rect(0, 0, 0, 0)).collidepoint(event.pos):
                                self.open_windows_game_controllers()
                        elif self.active_tab == "bluetooth":
                            self.handle_bluetooth_click(event.pos)
                        elif self.active_tab == "remap":
                            self.handle_remap_click(event.pos)
                        elif self.active_tab == "hidhide":
                            self.handle_hidhide_click(event.pos)
                        elif self.active_tab == "options":
                            self.handle_options_click(event.pos)
            elif event.type == pygame.MOUSEWHEEL:
                if self.console_visible and self.console_rects.get("log", pygame.Rect(0, 0, 0, 0)).collidepoint(pygame.mouse.get_pos()):
                    self.console_scroll = max(0, min(max(0, len(self.console_lines)), self.console_scroll + event.y * 3))
                elif self.active_tab == "bluetooth":
                    self.bluetooth_scroll = max(0, self.bluetooth_scroll - event.y)
                elif self.active_tab == "hidhide":
                    self.hidhide_scroll = max(0, self.hidhide_scroll - event.y)
                elif self.active_tab == "devices":
                    device_count = len(self.dashboard_joysticks())
                    visible_rows = max(
                        1,
                        (self.screen.get_height() - 216 - 66) // max(68, round(78 * self.ui_scale)),
                    )
                    self.devices_scroll = max(
                        0,
                        min(
                            max(0, device_count - visible_rows),
                            self.devices_scroll - event.y,
                        ),
                    )
            elif event.type == pygame.KEYDOWN:
                if event.key == pygame.K_ESCAPE:
                    self.request_escape_action()
                elif event.key == pygame.K_r and self.active_tab == "tester":
                    self.refresh_devices()
                    self.write_console("INFO", "Actualizando lista local de mandos SDL.")
                elif event.key == pygame.K_r and self.active_tab == "bluetooth":
                    self.bluetooth.refresh()
                    self.write_console("INFO", "Solicitando reinicio de búsqueda Bluetooth.")
                elif self.active_tab == "tester" and event.key in (pygame.K_LEFT, pygame.K_RIGHT) and self.joysticks:
                    change = -1 if event.key == pygame.K_LEFT else 1
                    self.selected = (self.selected + change) % len(self.joysticks)
                    self.transport_key = None
                    self.transport_check_key = None
                    self.home_hold_started = None
                    self.home_hold_triggered = False
        self.check_home_hold()
        if (
            self._device_refresh_deadline is not None
            and time.monotonic() >= self._device_refresh_deadline
        ):
            self.write_console("INFO", "Cambio de dispositivos SDL detectado; actualizando mandos.")
            self.refresh_devices()
        self.save_window_size_if_due()

    def run(self) -> None:
        while self.running:
            self.handle_events()
            self.update_virtual_gamepad()
            self.update_playstation_led()
            self.update_tray_icon()
            if self.window_in_tray:
                self.clock.tick(30)
                continue
            self.screen.fill(BG)
            width, height = self.screen.get_size()
            self.update_ui_scale(width, height)
            margin, gap, top = 22, 18, 116
            panel_height = max(360, height - top - 22)
            self.draw_text(APP_NAME, margin, 19, TEXT, self.title_font)
            radio_indicators = {
                "on": ("Bluetooth activo", SUCCESS_COLOR),
                "off": ("Bluetooth desactivado", WARN),
                "unavailable": ("Bluetooth no disponible", WARN),
                "unknown": ("Estado Bluetooth desconocido", MUTED),
            }
            radio_label, radio_color = radio_indicators.get(
                self.bluetooth_radio_state, radio_indicators["unknown"],
            )
            self.draw_text_fitted(radio_label, width - 340, 25, 190, radio_color, self.small)
            self.draw_text_fitted("ESC para cerrar", width - 140, 26, 130, MUTED, self.small)
            self.draw_navigation(width)
            panel_rect = pygame.Rect(margin, top, width - margin * 2, panel_height)
            if self.active_tab == "devices":
                self.draw_devices_page(panel_rect)
            elif self.active_tab == "tester":
                left_width = int((width - margin * 2 - gap) * 0.48)
                left_rect = pygame.Rect(margin, top, left_width, panel_height)
                right_rect = pygame.Rect(margin + left_width + gap, top, width - margin * 2 - gap - left_width, panel_height)
                self.draw_left_panel(left_rect, self.joystick)
                self.draw_pad(right_rect, self.joystick)
            elif self.active_tab == "bluetooth":
                self.draw_bluetooth_page(panel_rect)
            elif self.active_tab == "remap":
                self.draw_remap_page(panel_rect)
            elif self.active_tab == "hidhide":
                self.draw_hidhide_page(panel_rect)
            elif self.active_tab == "options":
                self.draw_options_page(panel_rect)
            else:
                self.draw_about_page(panel_rect)
            self.draw_console(width, height)
            if self.escape_dialog_open:
                self.draw_escape_dialog()
            pygame.display.flip()
            self.clock.tick(60)
        if self._window_resize_deadline is not None:
            self.save_settings()
        self.restore_signal_stop.set()
        if self.restore_signal_thread is not None:
            self.restore_signal_thread.join(timeout=1)
        self.virtual_bridge.close()
        self.bluetooth.close()
        if self.tray_icon is not None:
            try:
                self.tray_icon.stop()
            except (OSError, RuntimeError) as error:
                self.write_console("ERROR", f"No se pudo cerrar el icono de la bandeja: {error}")
        pygame.quit()


def acquire_single_instance_mutex():
    if platform.system() != "Windows":
        return None
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateMutexW.argtypes = (ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p)
    kernel32.CreateMutexW.restype = ctypes.c_void_p
    kernel32.CloseHandle.argtypes = (ctypes.c_void_p,)
    ctypes.set_last_error(0)
    handle = kernel32.CreateMutexW(None, True, "Local\\GDMGamepadManager")
    error = ctypes.get_last_error()
    if not handle:
        raise ctypes.WinError(error)
    if error == 183:
        kernel32.CloseHandle(handle)
        return None
    return kernel32, handle


def signal_running_instance() -> bool:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenEventW.argtypes = (ctypes.c_ulong, ctypes.c_bool, ctypes.c_wchar_p)
    kernel32.OpenEventW.restype = ctypes.c_void_p
    kernel32.SetEvent.argtypes = (ctypes.c_void_p,)
    kernel32.SetEvent.restype = ctypes.c_bool
    kernel32.CloseHandle.argtypes = (ctypes.c_void_p,)
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        handle = kernel32.OpenEventW(0x0002, False, "Local\\GDMGamepadManager.Restore")
        if handle:
            try:
                if not kernel32.SetEvent(handle):
                    raise ctypes.WinError(ctypes.get_last_error())
                return True
            finally:
                kernel32.CloseHandle(handle)
        error = ctypes.get_last_error()
        if error != 2:
            raise ctypes.WinError(error)
        time.sleep(0.05)
    return False


def run_application(start_in_tray: bool = False) -> None:
    mutex = acquire_single_instance_mutex()
    if platform.system() == "Windows" and mutex is None:
        if not signal_running_instance():
            raise RuntimeError("GDM ya está ejecutándose, pero no se pudo contactar con la ventana existente.")
        return
    event_handle = None
    try:
        if mutex is not None:
            kernel32, _ = mutex
            kernel32.CreateEventW.argtypes = (
                ctypes.c_void_p,
                ctypes.c_bool,
                ctypes.c_bool,
                ctypes.c_wchar_p,
            )
            kernel32.CreateEventW.restype = ctypes.c_void_p
            event_handle = kernel32.CreateEventW(
                None,
                False,
                False,
                "Local\\GDMGamepadManager.Restore",
            )
            if not event_handle:
                raise ctypes.WinError(ctypes.get_last_error())
        GDMGamepadManager(
            start_in_tray=start_in_tray,
            restore_event_handle=event_handle,
        ).run()
    finally:
        if event_handle is not None:
            kernel32.CloseHandle(event_handle)
        if mutex is not None:
            kernel32, handle = mutex
            kernel32.CloseHandle(handle)


if __name__ == "__main__":
    run_application()
