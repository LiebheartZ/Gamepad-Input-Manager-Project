"""GDM: live gamepad diagnostics and a minimal visualizer."""

from __future__ import annotations

import ctypes
from datetime import datetime
from functools import lru_cache
import hashlib
import importlib.util
import json
import os
import platform
import queue
import re
import subprocess
import sys
import threading
import time
import webbrowser
from pathlib import Path
from typing import Optional

import pygame
from pygame._sdl2 import controller as sdl_controller
from pygame._sdl2.video import Window

from battery_manager import dualsense_hid_battery_reading, gamepad_battery_reading
from bluetooth_manager import BluetoothService, pairing_status_text
from dualsense_led import is_dualsense, joystick_path_for_index, normalize_device_path, set_lightbar_color
from hidhide_manager import HidHideCLIService, HidHideError
from startup_manager import StartupShortcutError, set_windows_startup
from virtual_gamepad import (
    BUTTONS as REMAP_BUTTONS,
    BUTTON_IDS as REMAP_BUTTON_IDS,
    RAW_BUTTONS as REMAP_RAW_BUTTONS,
    SDL_BUTTONS as REMAP_SDL_BUTTONS,
    OUTPUT_TYPES,
    VirtualGamepadBridge,
    button_target_labels,
    default_profile_mapping,
)


APP_NAME = "GDM Python Beta"
APP_VERSION = "0.1.0"
APP_ICON_PATH = Path(__file__).with_name("gdm_icon.ico")
HIDHIDE_RELEASES_URL = "https://github.com/nefarius/HidHide/releases"
VIGEMBUS_RELEASES_URL = "https://github.com/nefarius/ViGEmBus/releases"
PYTHON_DEPENDENCIES = (
    ("pygame", "pygame-ce"),
    ("PIL", "Pillow"),
    ("hid", "hidapi"),
    ("pystray", "pystray"),
    ("winrt.windows.devices.bluetooth", "winrt-Windows.Devices.Bluetooth"),
    ("winrt.windows.devices.enumeration", "winrt-Windows.Devices.Enumeration"),
    ("winrt.windows.devices.power", "winrt-Windows.Devices.Power"),
    ("winrt.windows.devices.radios", "winrt-Windows.Devices.Radios"),
    ("winrt.windows.foundation", "winrt-Windows.Foundation"),
    (
        "winrt.windows.foundation.collections",
        "winrt-Windows.Foundation.Collections",
    ),
    ("winrt.windows.gaming.input", "winrt-Windows.Gaming.Input"),
)
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
LIST_BACKGROUND = (25, 30, 40)
LIST_SELECTED = (52, 67, 88)
DISABLED_BACKGROUND = (35, 40, 50)
CONSOLE_BACKGROUND = (13, 17, 24)
CONSOLE_BORDER = (76, 91, 112)
CONSOLE_HEADER = (35, 43, 56)
CONSOLE_LOG_BACKGROUND = (9, 12, 18)


def apply_color_theme(theme: str) -> None:
    global BG, PANEL, PANEL_LIGHT, TEXT, MUTED, ACCENT, ACTIVE, PAIRED_COLOR
    global VIRTUAL_COLOR, WARN, ERROR_COLOR, SUCCESS_COLOR, PAD_BODY, PAD_EDGE
    global LIST_BACKGROUND, LIST_SELECTED, DISABLED_BACKGROUND, CONSOLE_BACKGROUND
    global CONSOLE_BORDER, CONSOLE_HEADER, CONSOLE_LOG_BACKGROUND

    if theme == "light":
        (
            BG, PANEL, PANEL_LIGHT, TEXT, MUTED, ACCENT, ACTIVE, PAIRED_COLOR,
            VIRTUAL_COLOR, WARN, ERROR_COLOR, SUCCESS_COLOR, PAD_BODY, PAD_EDGE,
            LIST_BACKGROUND, LIST_SELECTED, DISABLED_BACKGROUND, CONSOLE_BACKGROUND,
            CONSOLE_BORDER, CONSOLE_HEADER, CONSOLE_LOG_BACKGROUND,
        ) = (
            (228, 233, 241), (243, 246, 250), (198, 208, 222), (24, 32, 45),
            (67, 80, 99), (29, 78, 160), (20, 105, 65), (38, 78, 145),
            (104, 52, 145), (133, 78, 0), (160, 35, 35), (20, 105, 65),
            (218, 226, 238), (91, 111, 139), (232, 237, 244), (199, 218, 245),
            (215, 221, 230), (244, 246, 249), (115, 129, 149), (220, 226, 235),
            (238, 242, 247),
        )
    else:
        (
            BG, PANEL, PANEL_LIGHT, TEXT, MUTED, ACCENT, ACTIVE, PAIRED_COLOR,
            VIRTUAL_COLOR, WARN, ERROR_COLOR, SUCCESS_COLOR, PAD_BODY, PAD_EDGE,
            LIST_BACKGROUND, LIST_SELECTED, DISABLED_BACKGROUND, CONSOLE_BACKGROUND,
            CONSOLE_BORDER, CONSOLE_HEADER, CONSOLE_LOG_BACKGROUND,
        ) = (
            (21, 25, 34), (31, 37, 49), (43, 51, 66), (235, 239, 246),
            (151, 163, 181), (74, 170, 255), (74, 220, 147), (113, 164, 255),
            (190, 140, 255), (255, 190, 82), (255, 91, 91), (74, 220, 147),
            (20, 34, 59), (105, 126, 157), (25, 30, 40), (52, 67, 88),
            (35, 40, 50), (13, 17, 24), (76, 91, 112), (35, 43, 56),
            (9, 12, 18),
        )


SDL_CONTROLLER_BUTTON_TOUCHPAD = 20
HOME_RAW_BUTTONS = {
    "Xbox": 8,
    "PlayStation": 5,
}
XINPUT_GAMEPAD_GUIDE = 0x0400
XBOX_ONE_GUIDE_PRODUCT_IDS = {
    0x02D1,
    0x02DD,
    0x02E0,
    0x02EA,
    0x0B12,
    0x0B13,
    0x0B20,
    0x0B22,
}


class XInputGamepadState(ctypes.Structure):
    _fields_ = (
        ("buttons", ctypes.c_ushort),
        ("left_trigger", ctypes.c_ubyte),
        ("right_trigger", ctypes.c_ubyte),
        ("left_x", ctypes.c_short),
        ("left_y", ctypes.c_short),
        ("right_x", ctypes.c_short),
        ("right_y", ctypes.c_short),
    )


class XInputState(ctypes.Structure):
    _fields_ = (
        ("packet_number", ctypes.c_uint32),
        ("gamepad", XInputGamepadState),
    )


class XInputCapabilities(ctypes.Structure):
    _fields_ = (
        ("device_type", ctypes.c_ubyte),
        ("device_subtype", ctypes.c_ubyte),
        ("flags", ctypes.c_ushort),
        ("gamepad", XInputGamepadState),
        ("left_motor", ctypes.c_ushort),
        ("right_motor", ctypes.c_ushort),
    )


class XInputCapabilitiesEx(ctypes.Structure):
    _fields_ = (
        ("capabilities", XInputCapabilities),
        ("vendor_id", ctypes.c_ushort),
        ("product_id", ctypes.c_ushort),
        ("product_version", ctypes.c_ushort),
        ("unknown_1", ctypes.c_ushort),
        ("unknown_2", ctypes.c_uint32),
    )


@lru_cache(maxsize=1)
def xinput_guide_api():
    if platform.system() != "Windows":
        return None

    win_dll = getattr(ctypes, "WinDLL", None)
    win_function = getattr(ctypes, "WINFUNCTYPE", None)
    if win_dll is None or win_function is None:
        return None

    xinput = None
    for library_name in ("xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll"):
        try:
            xinput = win_dll(library_name)
            break
        except OSError:
            continue
    if xinput is None:
        return None

    kernel32 = win_dll("kernel32.dll", use_last_error=True)
    get_proc_address = kernel32.GetProcAddress
    get_proc_address.argtypes = (ctypes.c_void_p, ctypes.c_void_p)
    get_proc_address.restype = ctypes.c_void_p

    guide_address = get_proc_address(xinput._handle, ctypes.c_void_p(100))
    if not guide_address:
        return None

    state_function = win_function(
        ctypes.c_uint32,
        ctypes.c_uint32,
        ctypes.POINTER(XInputState),
    )(guide_address)

    capabilities_address = get_proc_address(
        xinput._handle,
        ctypes.c_void_p(108),
    )
    capabilities_function = (
        win_function(
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.POINTER(XInputCapabilitiesEx),
        )(capabilities_address)
        if capabilities_address
        else None
    )
    return xinput, state_function, capabilities_function


def xinput_user_index_for_joystick(joystick, capabilities_function) -> Optional[int]:
    name = joystick.get_name() or ""
    match = re.fullmatch(r"XInput .+ #([1-4])", name, flags=re.IGNORECASE)
    if match:
        return int(match.group(1)) - 1
    if capabilities_function is None:
        return None

    vendor_id = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
    product_id = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
    if vendor_id and product_id:
        expected_ids = {(vendor_id, product_id)}
    elif product_id:
        expected_ids = {
            (candidate_vendor, product_id)
            for candidate_vendor in (0, 0x045E)
        }
    else:
        expected_ids = {
            (0x045E, candidate_product)
            for candidate_product in XBOX_ONE_GUIDE_PRODUCT_IDS
        }

    matching_indices = []
    for user_index in range(4):
        capabilities = XInputCapabilitiesEx()
        if capabilities_function(
            1,
            user_index,
            0,
            ctypes.byref(capabilities),
        ) != 0:
            continue
        if (capabilities.vendor_id, capabilities.product_id) in expected_ids:
            matching_indices.append(user_index)

    return matching_indices[0] if len(matching_indices) == 1 else None


def xinput_guide_pressed_for_joystick(joystick) -> bool:
    api = xinput_guide_api()
    if api is None:
        return False
    _library, state_function, capabilities_function = api
    user_index = xinput_user_index_for_joystick(
        joystick,
        capabilities_function,
    )
    if user_index is None:
        return False

    state = XInputState()
    if state_function(user_index, ctypes.byref(state)) != 0:
        return False
    return bool(state.gamepad.buttons & XINPUT_GAMEPAD_GUIDE)


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
        self.dashboard_battery_values: dict[int, str] = {}
        self.dashboard_battery_diagnostics: dict[int, str] = {}
        self.dashboard_battery_lock = threading.Lock()
        self.dashboard_battery_events: queue.Queue[
            tuple[int, dict[int, str], dict[int, str], tuple[str, ...]]
        ] = queue.Queue()
        self.dashboard_battery_thread: Optional[threading.Thread] = None
        self.dashboard_battery_generation = 0
        self.dashboard_battery_check_at = 0.0
        self.dashboard_battery_started_at: Optional[float] = None
        self.dashboard_battery_timed_out = False
        self.dashboard_battery_scan_diagnostic: Optional[str] = None
        self.dashboard_battery_error: Optional[str] = None
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
        self.welcome_dialog_open = False
        self.welcome_dialog_dont_show = False
        self.welcome_dialog_rects: dict[str, pygame.Rect] = {}
        self.dependency_events: queue.Queue[tuple[str, bool, str]] = queue.Queue()
        self.dependency_installing: Optional[str] = None
        self.dependency_status = ""
        self.dependency_items: dict[str, dict[str, object]] = {}
        self.dependency_rects: dict[str, pygame.Rect] = {}
        self.welcome_dialog_dismissed = False
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
        self.pending_home_auto_pair_retry = True
        self.shutdown_dialog_open = False
        self.shutdown_dialog_rects: dict[str, pygame.Rect] = {}
        self.shutdown_focus_key: Optional[str] = None
        self.bluetooth_selected_id: Optional[str] = None
        self.bluetooth_scroll = 0
        self.active_tab = "devices"
        self.keyboard_focus_key: Optional[str] = None
        self.keyboard_navigation_active = False
        self.startup_tab = "devices"
        self.options_section = "general"
        self.options_scroll = 0
        self.options_viewport = pygame.Rect(0, 0, 0, 0)
        self.options_max_scroll = 0
        self._options_surface: Optional[pygame.Surface] = None
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
        self.shutdown_idle_last_activity: dict[int, float] = {}
        self.shutdown_idle_triggered: set[int] = set()
        self.shutdown_idle_input_errors: set[int] = set()
        self.window_size = (WIDTH, HEIGHT)
        self._window_resize_deadline: Optional[int] = None
        self.tray_events: queue.Queue[object] = queue.Queue()
        self.restore_event_handle = restore_event_handle
        self.restore_signal_stop = threading.Event()
        self.restore_signal_thread: Optional[threading.Thread] = None
        self.tray_icon = None
        self.window_in_tray = False
        self.remap_rects: dict[str, pygame.Rect] = {}
        self.tester_rects: dict[str, pygame.Rect] = {}
        self.remap_device_key: Optional[str] = None
        self.remap_device_scroll = 0
        self.virtual_outputs: dict[str, VirtualGamepadBridge] = {}
        self.virtual_output_statuses: dict[str, str] = {}
        self.legacy_virtual_output_requested = False
        self.virtual_output_auto_start = False
        self.playstation_led_color: Optional[str] = None
        self.playstation_led_status = "Selecciona un color para el mando físico."
        self._playstation_led_last_attempt: Optional[tuple[int, str]] = None
        self._playstation_led_pending: dict[int, tuple[str, float, int]] = {}
        self.default_profile_output = "auto"
        self.default_profile_mapping = default_profile_mapping()
        self.device_profiles: dict[str, dict] = {}
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
        self.theme = "dark"
        self.effective_theme: Optional[str] = None
        self._theme_check_at = 0.0
        self._theme_warning_logged = False
        self.load_settings()
        self.update_color_theme(force=True)
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
        self.bluetooth = BluetoothService(auto_pair=self.bluetooth_auto_pair)
        self.bluetooth_devices: dict[str, dict] = {}
        self.bluetooth_status = "Iniciando búsqueda Bluetooth…"
        self.bluetooth_radio_state = "unknown"
        self.virtual_output_status = "Desactivado"
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
        self.refresh_dependency_status()
        self.refresh_devices()
        if not start_in_tray and not self.welcome_dialog_dismissed:
            self.welcome_dialog_open = True
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
            "welcome_dialog_dismissed": "welcome_dialog_dismissed",
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
        theme = settings.get("theme")
        if isinstance(theme, str) and theme in {"dark", "auto", "light"}:
            self.theme = theme
        virtual_output_enabled = settings.get("virtual_output_enabled")
        if isinstance(virtual_output_enabled, bool):
            self.legacy_virtual_output_requested = virtual_output_enabled
        virtual_output_auto_start = settings.get("virtual_output_auto_start")
        if isinstance(virtual_output_auto_start, bool):
            self.virtual_output_auto_start = virtual_output_auto_start
        playstation_led_color = settings.get("playstation_led_color")
        if playstation_led_color is None or (
            isinstance(playstation_led_color, str)
            and playstation_led_color in PLAYSTATION_LED_COLORS
        ):
            self.playstation_led_color = playstation_led_color
        default_output = settings.get("default_profile_output")
        if isinstance(default_output, str) and default_output in {"auto", *OUTPUT_TYPES}:
            self.default_profile_output = default_output
        legacy_outputs = settings.get("virtual_profile_outputs")
        legacy_mappings = settings.get("virtual_profile_mappings")
        if "default_profile_output" not in settings and isinstance(legacy_outputs, dict):
            legacy_values = {
                value for value in legacy_outputs.values()
                if isinstance(value, str) and value in OUTPUT_TYPES
            }
            if len(legacy_values) == 1:
                self.default_profile_output = legacy_values.pop()

        saved_default_mapping = settings.get("default_profile_mapping")
        if not isinstance(saved_default_mapping, dict) and isinstance(legacy_mappings, dict):
            saved_default_mapping = legacy_mappings.get("Genérico")
        self.load_mapping_values(self.default_profile_mapping, saved_default_mapping)

        saved_device_profiles = settings.get("device_profiles")
        if isinstance(saved_device_profiles, dict):
            for device_key, profile in saved_device_profiles.items():
                if not isinstance(device_key, str) or not isinstance(profile, dict):
                    continue
                output = profile.get("output")
                enabled = profile.get("enabled")
                auto_start = profile.get(
                    "auto_start",
                    self.virtual_output_auto_start,
                )
                led_color = profile.get("led_color", self.playstation_led_color)
                shutdown_enabled = profile.get(
                    "shutdown_enabled",
                    self.disconnect_on_home_hold,
                )
                shutdown_method = profile.get("shutdown_method", "home")
                shutdown_buttons = profile.get("shutdown_buttons", [])
                shutdown_action = profile.get(
                    "shutdown_action",
                    self.home_hold_action,
                )
                shutdown_idle_enabled = profile.get(
                    "shutdown_idle_enabled",
                    False,
                )
                shutdown_idle_minutes = profile.get(
                    "shutdown_idle_minutes",
                    1,
                )
                mapping = default_profile_mapping()
                self.load_mapping_values(mapping, profile.get("mapping"))
                if (
                    isinstance(output, str)
                    and output in OUTPUT_TYPES
                    and isinstance(enabled, bool)
                ):
                    self.device_profiles[device_key] = {
                        "enabled": enabled,
                        "output": output,
                        "mapping": mapping,
                        "name": (
                            profile["name"]
                            if isinstance(profile.get("name"), str)
                            and profile["name"].strip()
                            else "Mando desconectado"
                        ),
                        "vendor_id": (
                            profile["vendor_id"]
                            if isinstance(profile.get("vendor_id"), int)
                            and not isinstance(profile.get("vendor_id"), bool)
                            else 0
                        ),
                        "product_id": (
                            profile["product_id"]
                            if isinstance(profile.get("product_id"), int)
                            and not isinstance(profile.get("product_id"), bool)
                            else 0
                        ),
                        "auto_start": (
                            auto_start
                            if isinstance(auto_start, bool)
                            else self.virtual_output_auto_start
                        ),
                        "led_color": (
                            led_color
                            if led_color is None
                            or (
                                isinstance(led_color, str)
                                and led_color in PLAYSTATION_LED_COLORS
                            )
                            else self.playstation_led_color
                        ),
                        "shutdown_enabled": (
                            shutdown_enabled
                            if isinstance(shutdown_enabled, bool)
                            else self.disconnect_on_home_hold
                        ),
                        "shutdown_method": (
                            shutdown_method
                            if isinstance(shutdown_method, str)
                            and shutdown_method in {"home", "combo"}
                            else "home"
                        ),
                        "shutdown_buttons": (
                            [
                                button_id
                                for button_id in shutdown_buttons
                                if isinstance(button_id, str)
                                and button_id in REMAP_BUTTON_IDS
                            ][:len(REMAP_BUTTON_IDS)]
                            if isinstance(shutdown_buttons, list)
                            else []
                        ),
                        "shutdown_action": (
                            shutdown_action
                            if isinstance(shutdown_action, str)
                            and shutdown_action in {"disconnect", "unpair"}
                            else self.home_hold_action
                        ),
                        "shutdown_idle_enabled": (
                            shutdown_idle_enabled
                            if isinstance(shutdown_idle_enabled, bool)
                            else False
                        ),
                        "shutdown_idle_minutes": (
                            shutdown_idle_minutes
                            if isinstance(shutdown_idle_minutes, int)
                            and not isinstance(shutdown_idle_minutes, bool)
                            and 1 <= shutdown_idle_minutes <= 60
                            else 1
                        ),
                    }
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

    @staticmethod
    def load_mapping_values(target: dict[str, Optional[str]], saved) -> None:
        if not isinstance(saved, dict):
            return
        valid_targets = set(REMAP_BUTTON_IDS)
        for button_id in REMAP_BUTTON_IDS:
            value = saved.get(button_id)
            if value is None or (isinstance(value, str) and value in valid_targets):
                target[button_id] = value

    def remap_device_identity(self, joystick) -> tuple[str, bool]:
        instance_id = joystick.get_instance_id()
        device_index = self.joystick_device_indices.get(instance_id)
        if device_index is not None:
            device_path = joystick_path_for_index(device_index)
            if device_path:
                stable_id = hashlib.sha256(
                    normalize_device_path(device_path).encode("utf-8")
                ).hexdigest()
                return f"hid:{stable_id}", True
        try:
            guid = joystick.get_guid()
        except (AttributeError, pygame.error):
            guid = "unknown"
        return f"session:{guid}:{instance_id}", False

    def remap_devices(self) -> list[tuple[str, object | None, str, bool]]:
        devices = []
        connected_keys = set()
        for joystick in self.joysticks:
            try:
                if not joystick.get_init() or self.is_virtual_gamepad(joystick):
                    continue
                device_key, stable = self.remap_device_identity(joystick)
                name = joystick.get_name() or "Mando sin nombre"
                devices.append((device_key, joystick, name, stable))
                connected_keys.add(device_key)
            except pygame.error:
                continue
        for device_key, profile in self.device_profiles.items():
            if (
                device_key in connected_keys
                or not profile.get("enabled")
            ):
                continue
            name = profile.get("name")
            if not isinstance(name, str) or not name.strip():
                name = "Mando desconectado"
            devices.append((device_key, None, name, True))
        name_counts = {
            name: sum(1 for _key, _joystick, device_name, _stable in devices if device_name == name)
            for _key, _joystick, name, _stable in devices
        }
        name_indices: dict[str, int] = {}
        labeled_devices = []
        for device_key, joystick, name, stable in devices:
            if name_counts[name] > 1:
                name_indices[name] = name_indices.get(name, 0) + 1
                name = f"{name} {name_indices[name]}"
            labeled_devices.append((device_key, joystick, name, stable))
        return labeled_devices

    def active_remap_joystick(self):
        entry = self.active_remap_device()
        return entry[1] if entry is not None else None

    def active_remap_device(self):
        devices = self.remap_devices()
        for entry in devices:
            device_key = entry[0]
            if device_key == self.remap_device_key:
                return entry
        return devices[0] if devices else None

    def active_remap_profile(self) -> tuple[str, bool, str, dict]:
        entry = self.active_remap_device()
        if entry is None:
            return "", False, "Xbox 360", self.default_profile_mapping
        device_key, joystick, _name, _stable = entry
        if joystick is not None:
            return self.remap_profile_for_device(joystick)
        profile = self.device_profiles.get(device_key)
        if profile and profile.get("enabled"):
            return device_key, True, profile["output"], profile["mapping"]
        return device_key, False, "Xbox 360", self.default_profile_mapping

    def remap_profile_for_device(self, joystick) -> tuple[str, bool, str, dict]:
        device_key, _stable = self.remap_device_identity(joystick)
        profile = self.device_profiles.get(device_key)
        if profile and profile["enabled"]:
            return device_key, True, profile["output"], profile["mapping"]
        output = self.default_profile_output
        if output == "auto":
            vendor_id = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
            output = "DualShock 4" if self.model_name(joystick.get_name() or "", vendor_id) == "PlayStation" else "Xbox 360"
        return device_key, False, output, self.default_profile_mapping

    def profile_auto_start_for_device(self, joystick=None) -> bool:
        if joystick is None:
            device_key, enabled, _output, _mapping = self.active_remap_profile()
            if enabled:
                return self.device_profiles[device_key]["auto_start"]
            return self.virtual_output_auto_start
        device_key, enabled, _output, _mapping = self.remap_profile_for_device(joystick)
        if enabled:
            profile = self.device_profiles[device_key]
            return profile["auto_start"]
        return self.virtual_output_auto_start

    def profile_led_color_for_device(self, joystick) -> Optional[str]:
        if joystick is None:
            device_key, enabled, _output, _mapping = self.active_remap_profile()
            if enabled:
                return self.device_profiles[device_key]["led_color"]
            return self.playstation_led_color
        device_key, enabled, _output, _mapping = self.remap_profile_for_device(joystick)
        if enabled:
            return self.device_profiles[device_key]["led_color"]
        return self.playstation_led_color

    def set_active_profile_preference(
        self,
        preference: str,
        value: bool | str | None,
    ) -> None:
        entry = self.active_remap_device()
        if entry is not None:
            device_key, joystick, _name, _stable = entry
            if joystick is not None:
                device_key, enabled, _output, _mapping = self.remap_profile_for_device(joystick)
            else:
                enabled = bool(
                    self.device_profiles.get(device_key, {}).get("enabled")
                )
            if enabled:
                if preference == "auto_start" and isinstance(value, bool):
                    self.device_profiles[device_key][preference] = value
                    return
                if preference == "led_color" and (
                    value is None
                    or (
                        isinstance(value, str)
                        and value in PLAYSTATION_LED_COLORS
                    )
                ):
                    self.device_profiles[device_key][preference] = value
                    return
                raise ValueError(f"Valor no válido para la preferencia de perfil: {preference}")
        if preference == "auto_start" and isinstance(value, bool):
            self.virtual_output_auto_start = value
        elif preference == "led_color" and (
            value is None
            or (
                isinstance(value, str)
                and value in PLAYSTATION_LED_COLORS
            )
        ):
            self.playstation_led_color = value
        else:
            raise ValueError(f"Valor no válido para la preferencia de perfil: {preference}")

    def shutdown_settings_for_device(self, joystick) -> dict:
        profile_key = None
        enabled = False
        if joystick is not None:
            profile_key, enabled, _output, _mapping = self.remap_profile_for_device(
                joystick
            )
        else:
            profile_key, enabled, _output, _mapping = self.active_remap_profile()

        if enabled:
            profile = self.device_profiles[profile_key]
            return {
                "custom": True,
                "enabled": profile.get(
                    "shutdown_enabled",
                    self.disconnect_on_home_hold,
                ),
                "method": profile.get("shutdown_method", "home"),
                "buttons": profile.get("shutdown_buttons", []),
                "action": profile.get("shutdown_action", self.home_hold_action),
                "idle_enabled": profile.get("shutdown_idle_enabled", False),
                "idle_minutes": profile.get("shutdown_idle_minutes", 1),
            }
        return {
            "custom": False,
            "enabled": self.disconnect_on_home_hold,
            "method": "home",
            "buttons": [],
            "action": self.home_hold_action,
            "idle_enabled": False,
            "idle_minutes": 1,
        }

    def set_active_shutdown_setting(self, setting: str, value) -> None:
        entry = self.active_remap_device()
        if entry is None:
            return
        device_key, joystick, _name, _stable = entry
        if joystick is not None:
            device_key, enabled, _output, _mapping = self.remap_profile_for_device(
                joystick
            )
        else:
            enabled = bool(
                self.device_profiles.get(device_key, {}).get("enabled")
            )
        if not enabled:
            return
        profile = self.device_profiles[device_key]
        if setting == "shutdown_enabled" and isinstance(value, bool):
            profile[setting] = value
        elif (
            setting == "shutdown_method"
            and isinstance(value, str)
            and value in {"home", "combo"}
        ):
            profile[setting] = value
        elif (
            setting == "shutdown_action"
            and isinstance(value, str)
            and value in {"disconnect", "unpair"}
        ):
            profile[setting] = value
        elif setting == "shutdown_idle_enabled" and isinstance(value, bool):
            profile[setting] = value
            self.reset_shutdown_idle_timer(joystick)
        elif (
            setting == "shutdown_idle_minutes"
            and isinstance(value, int)
            and not isinstance(value, bool)
            and 1 <= value <= 60
        ):
            profile[setting] = value
            self.reset_shutdown_idle_timer(joystick)
        elif setting == "shutdown_buttons" and isinstance(value, str):
            buttons = profile["shutdown_buttons"]
            if value in buttons:
                buttons.remove(value)
            elif value in REMAP_BUTTON_IDS:
                buttons.append(value)
        else:
            raise ValueError(f"Valor no válido para la preferencia de apagado: {setting}")

    def reset_shutdown_idle_timer(self, joystick) -> None:
        if joystick is None:
            return
        instance_id = joystick.get_instance_id()
        self.shutdown_idle_last_activity.pop(instance_id, None)
        self.shutdown_idle_triggered.discard(instance_id)

    def ensure_device_profile(self, joystick) -> tuple[str, dict]:
        device_key, stable = self.remap_device_identity(joystick)
        profile = self.device_profiles.get(device_key)
        if profile is None:
            _key, _enabled, output, _mapping = self.remap_profile_for_device(joystick)
            profile = {
                "enabled": False,
                "output": output,
                "mapping": self.default_profile_mapping.copy(),
                "name": joystick.get_name() or "Mando sin nombre",
                "vendor_id": (
                    joystick.get_vendor_id()
                    if hasattr(joystick, "get_vendor_id")
                    else 0
                ),
                "product_id": (
                    joystick.get_product_id()
                    if hasattr(joystick, "get_product_id")
                    else 0
                ),
                "auto_start": self.virtual_output_auto_start,
                "led_color": self.playstation_led_color,
                "shutdown_enabled": self.disconnect_on_home_hold,
                "shutdown_method": "home",
                "shutdown_buttons": [],
                "shutdown_action": self.home_hold_action,
                "shutdown_idle_enabled": False,
                "shutdown_idle_minutes": 1,
            }
            self.device_profiles[device_key] = profile
        else:
            profile["name"] = joystick.get_name() or profile.get("name", "Mando sin nombre")
            profile["vendor_id"] = (
                joystick.get_vendor_id()
                if hasattr(joystick, "get_vendor_id")
                else profile.get("vendor_id", 0)
            )
            profile["product_id"] = (
                joystick.get_product_id()
                if hasattr(joystick, "get_product_id")
                else profile.get("product_id", 0)
            )
        if not stable:
            self.write_console(
                "WARN",
                "SDL no expuso una ruta persistente para este mando; su perfil personalizado "
                "solo podrá identificarse mientras dure esta conexión.",
            )
        return device_key, profile

    @staticmethod
    def python_module_available(module_name: str) -> bool:
        try:
            return importlib.util.find_spec(module_name) is not None
        except (ImportError, ModuleNotFoundError, ValueError):
            return False

    @staticmethod
    def vigembus_installed() -> Optional[bool]:
        if platform.system() != "Windows":
            return None
        try:
            import winreg

            with winreg.OpenKey(
                winreg.HKEY_LOCAL_MACHINE,
                r"SYSTEM\CurrentControlSet\Services\ViGEmBus",
            ):
                return True
        except FileNotFoundError:
            return False
        except (ImportError, OSError) as error:
            raise RuntimeError(f"No se pudo consultar el servicio ViGEmBus: {error}") from error

    def refresh_dependency_status(self) -> None:
        self.hidhide_service.cli_path = HidHideCLIService.find_cli()
        python_missing = [
            package
            for module, package in PYTHON_DEPENDENCIES
            if not module.startswith("winrt.")
            and not self.python_module_available(module)
        ]
        if platform.system() == "Windows":
            python_missing.extend(
                package
                for module, package in PYTHON_DEPENDENCIES
                if module.startswith("winrt.")
                and not self.python_module_available(module)
            )
        try:
            vigembus_status = self.vigembus_installed()
        except RuntimeError as error:
            vigembus_status = None
            self.write_console("ERROR", str(error))
        self.dependency_items = {
            "python": {
                "title": "Dependencias Python",
                "description": (
                    "pygame-ce, Pillow, hidapi, pystray y las API WinRT requeridas."
                ),
                "installed": not python_missing,
                "status": (
                    "Instaladas"
                    if not python_missing
                    else f"Faltan: {', '.join(python_missing[:3])}"
                    + ("…" if len(python_missing) > 3 else "")
                ),
                "action": "install_python",
            },
            "vgamepad": {
                "title": "vgamepad (opcional)",
                "description": "Wrapper Python necesario para crear mandos virtuales.",
                "installed": self.python_module_available("vgamepad"),
                "status": (
                    "Instalado"
                    if self.python_module_available("vgamepad")
                    else "No instalado"
                ),
                "action": "install_vgamepad",
            },
            "vigembus": {
                "title": "ViGEmBus",
                "description": "Driver de sistema para la emulación Xbox 360 / DualShock 4.",
                "installed": vigembus_status,
                "status": "",
                "action": "open_vigembus",
                "url": VIGEMBUS_RELEASES_URL,
            },
            "hidhide": {
                "title": "HidHide",
                "description": "Driver y herramienta opcional para ocultar el mando físico a los juegos.",
                "installed": self.hidhide_service.cli_path is not None,
                "status": (
                    "Instalado"
                    if self.hidhide_service.cli_path is not None
                    else "No instalado"
                ),
                "action": "open_hidhide",
                "url": HIDHIDE_RELEASES_URL,
            },
        }
        vigembus = self.dependency_items["vigembus"]
        if vigembus["installed"] is True:
            vigembus["status"] = "Instalado"
        elif vigembus["installed"] is False:
            vigembus["status"] = "No instalado"
        else:
            vigembus["status"] = "Solo disponible en Windows"

    def start_dependency_install(self, dependency: str) -> None:
        if self.dependency_installing is not None:
            return
        if dependency not in {"python", "vgamepad"}:
            raise ValueError(f"No se puede instalar automáticamente: {dependency}")
        if dependency == "python":
            requirement_path = Path(__file__).with_name("requirements.txt")
            if not requirement_path.is_file():
                self.dependency_status = f"No se encontró {requirement_path.name}."
                self.write_console("ERROR", self.dependency_status)
                return
            command = [
                sys.executable,
                "-m",
                "pip",
                "install",
                "-r",
                str(requirement_path),
            ]
        else:
            command = [sys.executable, "-m", "pip", "install", "vgamepad"]
        environment = os.environ.copy()
        if dependency == "vgamepad":
            environment["VGAMEPAD_SKIP_VIGEMBUS_INSTALL"] = "true"
        self.dependency_installing = dependency
        self.dependency_status = "Instalando paquetes Python…"
        self.write_console(
            "INFO",
            f"Ejecutando instalación Python: {' '.join(command)}",
        )

        def worker() -> None:
            try:
                result = subprocess.run(
                    command,
                    capture_output=True,
                    text=True,
                    timeout=900,
                    env=environment,
                    check=False,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
            except (OSError, subprocess.TimeoutExpired) as error:
                self.dependency_events.put(
                    (dependency, False, f"No se pudo completar pip: {error}")
                )
                return
            output = (result.stdout or "").strip()
            error_output = (result.stderr or "").strip()
            detail = error_output or output or f"Código de salida {result.returncode}."
            self.dependency_events.put(
                (dependency, result.returncode == 0, detail[-3000:])
            )

        threading.Thread(
            target=worker,
            name=f"GDMDependencyInstall-{dependency}",
            daemon=True,
        ).start()

    def process_dependency_events(self) -> None:
        while True:
            try:
                dependency, succeeded, detail = self.dependency_events.get_nowait()
            except queue.Empty:
                return
            self.dependency_installing = None
            if succeeded:
                self.refresh_dependency_status()
                item = self.dependency_items.get(dependency, {})
                if item.get("installed"):
                    self.dependency_status = "Instalación terminada y dependencia verificada."
                    self.write_console("SUCCESS", self.dependency_status)
                else:
                    self.dependency_status = (
                        "pip terminó, pero GDM aún no detecta el paquete. "
                        "Reinicia GDM y vuelve a comprobar."
                    )
                    self.write_console("WARN", f"{self.dependency_status}\n{detail}")
            else:
                self.dependency_status = f"Falló la instalación: {detail}"
                self.write_console("ERROR", self.dependency_status)

    def open_dependency_website(self, dependency: str) -> None:
        item = self.dependency_items.get(dependency)
        url = item.get("url") if item else None
        if not isinstance(url, str):
            raise ValueError(f"No hay una página oficial configurada para {dependency}.")
        try:
            opened = webbrowser.open(url, new=2)
        except (OSError, webbrowser.Error) as error:
            self.dependency_status = f"No se pudo abrir el navegador: {error}"
            self.write_console("ERROR", self.dependency_status)
            return
        if not opened:
            self.dependency_status = "No se pudo abrir el navegador predeterminado."
            self.write_console("ERROR", self.dependency_status)
            return
        self.dependency_status = f"Abierta la página oficial de {item['title']}."
        self.write_console("INFO", self.dependency_status)

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
            "theme": self.theme,
            "console_visible": self.console_visible,
            "console_expanded": self.console_expanded,
            "show_console_button": self.show_console_button,
            "welcome_dialog_dismissed": self.welcome_dialog_dismissed,
            "window_width": self.window_size[0],
            "window_height": self.window_size[1],
            "virtual_output_enabled": bool(self.virtual_outputs),
            "virtual_output_auto_start": self.virtual_output_auto_start,
            "playstation_led_color": self.playstation_led_color,
            "default_profile_output": self.default_profile_output,
            "default_profile_mapping": self.default_profile_mapping,
            "device_profiles": self.device_profiles,
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
        self.dashboard_battery_generation += 1
        self.dashboard_battery_check_at = 0.0
        self.dashboard_battery_scan_diagnostic = None
        with self.dashboard_battery_lock:
            self.dashboard_battery_values.clear()
            self.dashboard_battery_diagnostics.clear()
        with self.transport_lock:
            self.dashboard_transport_generation += 1
            self.dashboard_transport_values.clear()
        self.devices_scroll = 0
        previous_id = None
        previous_device_ids = set()
        for old_joystick in self.joysticks:
            try:
                previous_device_ids.add(old_joystick.get_instance_id())
            except pygame.error:
                continue
        previous_device_ids_copy = set(previous_device_ids)
        if self.joysticks and self.selected < len(self.joysticks):
            try:
                previous_id = self.joysticks[self.selected].get_instance_id()
            except pygame.error:
                pass
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
        if (
            self._playstation_led_last_attempt is not None
            and not any(
                joystick.get_instance_id() == self._playstation_led_last_attempt[0]
                for joystick in self.joysticks
                if joystick.get_init()
            )
        ):
            self._playstation_led_last_attempt = None
        self.selected = 0
        if previous_id is not None:
            for index, joystick in enumerate(self.joysticks):
                try:
                    if joystick.get_instance_id() == previous_id:
                        self.selected = index
                        break
                except pygame.error:
                    continue
        connected_physical_keys = set()
        for joystick in self.joysticks:
            try:
                if joystick.get_init() and not self.is_virtual_gamepad(joystick):
                    device_key, _stable = self.remap_device_identity(joystick)
                    connected_physical_keys.add(device_key)
            except pygame.error:
                continue
        for device_key in tuple(self.virtual_outputs):
            if device_key not in connected_physical_keys:
                self.virtual_outputs.pop(device_key).close()
                self.virtual_output_statuses[device_key] = (
                    "El mando físico se desconectó; salida virtual detenida."
                )
                self.write_console(
                    "INFO",
                    "El mando de origen se desconectó; se detuvo su salida virtual.",
                )

        if self.joysticks and (
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
            for source in new_joysticks:
                try:
                    if (
                        self.is_virtual_gamepad(source)
                        or not self.profile_auto_start_for_device(source)
                    ):
                        continue
                    self.start_virtual_output_for_device(source)
                    self.write_console(
                        "INFO",
                        f"Mando conectado; iniciando la salida virtual para {source.get_name()}.",
                    )
                except (ValueError, pygame.error) as error:
                    self.write_console("WARN", f"No se pudo iniciar automáticamente la salida virtual: {error}")
            if self.legacy_virtual_output_requested:
                source = next(
                    (
                        joystick for joystick in new_joysticks
                        if not self.is_virtual_gamepad(joystick)
                    ),
                    None,
                )
                if source is not None:
                    self.start_virtual_output_for_device(source)
                    self.legacy_virtual_output_requested = False
            self.save_settings()
        if not self.joysticks:
            self.transport = "—"
            self.transport_key = None
        remap_devices = self.remap_devices()
        remap_device_keys = {device_key for device_key, _joystick, _name, _stable in remap_devices}
        if self.remap_device_key not in remap_device_keys:
            selected_joystick = self.joystick
            selected_key = None
            if selected_joystick is not None and not self.is_virtual_gamepad(selected_joystick):
                selected_key, _stable = self.remap_device_identity(selected_joystick)
            self.remap_device_key = (
                selected_key if selected_key in remap_device_keys
                else remap_devices[0][0] if remap_devices
                else None
            )
            self.remap_device_scroll = 0
        active_device_ids = set()
        for joystick in self.joysticks:
            try:
                if joystick.get_init():
                    active_device_ids.add(joystick.get_instance_id())
            except pygame.error:
                continue
        self._playstation_led_pending = {
            instance_id: pending
            for instance_id, pending in self._playstation_led_pending.items()
            if instance_id in active_device_ids
        }
        new_device_ids = active_device_ids - previous_device_ids_copy
        for joystick in self.joysticks:
            try:
                instance_id = joystick.get_instance_id()
                if (
                    instance_id not in new_device_ids
                    or not joystick.get_init()
                    or self.is_virtual_gamepad(joystick)
                ):
                    continue
                name = joystick.get_name() or ""
                product_id = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
                color_key = self.profile_led_color_for_device(joystick)
                compatible_led_model = (
                    is_dualsense(name, product_id)
                    or product_id in {0x05C4, 0x09CC}
                    or "dualshock 4" in name.casefold()
                )
                if (
                    not compatible_led_model
                    or color_key not in PLAYSTATION_LED_COLORS
                ):
                    continue
                self._playstation_led_pending[instance_id] = (
                    color_key,
                    time.monotonic() + 1.0,
                    0,
                )
                self.write_console(
                    "INFO",
                    f"Se aplicará el color LED guardado al mando {name} recién conectado.",
                )
            except pygame.error:
                continue
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
            instance_id = joystick.get_instance_id()
        except pygame.error:
            return True
        normalized_name = self.normalize_device_name(name)
        return (
            self.hidhide_type_for_gamepad(vendor_id, product_id, name) == "virtual"
            or self.name_indicates_virtual_gamepad(normalized_name)
            or self.is_active_virtual_gamepad_output(instance_id)
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

    def start_dashboard_battery_detection(self) -> None:
        if platform.system() != "Windows":
            return
        now = time.monotonic()
        if self.dashboard_battery_thread is not None and self.dashboard_battery_thread.is_alive():
            if (
                self.dashboard_battery_started_at is not None
                and now - self.dashboard_battery_started_at > 5
                and not self.dashboard_battery_timed_out
            ):
                self.dashboard_battery_timed_out = True
                with self.dashboard_battery_lock:
                    pending_ids = [
                        instance_id
                        for instance_id, diagnostic in self.dashboard_battery_diagnostics.items()
                        if diagnostic == "consulting"
                    ]
                    for instance_id in pending_ids:
                        self.dashboard_battery_diagnostics[instance_id] = "worker_timeout"
                self.write_console(
                    "WARN",
                    "La consulta de batería sigue bloqueada después de 5 segundos; "
                    "revise el acceso de Windows o HID al mando.",
                )
            return
        if now < self.dashboard_battery_check_at:
            return

        instance_ids_by_hardware: dict[tuple[int, int], list[tuple[int, int]]] = {}
        skipped_devices: list[str] = []
        for joystick in self.joysticks:
            try:
                name = joystick.get_name() or "Mando sin nombre"
                instance_id = joystick.get_instance_id()
                if not joystick.get_init():
                    skipped_devices.append(f"{name} (SDL {instance_id}: no inicializado)")
                    continue
                if self.is_virtual_gamepad(joystick):
                    skipped_devices.append(f"{name} (SDL {instance_id}: clasificado como virtual)")
                    continue
                vendor_id = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
                product_id = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
                if (not vendor_id or not product_id) and not is_dualsense(name, product_id):
                    skipped_devices.append(
                        f"{name} (SDL {instance_id}: VID/PID no disponibles y modelo no identificable)"
                    )
                    continue
                device_index = self.joystick_device_indices.get(instance_id)
                if device_index is None:
                    skipped_devices.append(
                        f"{name} (SDL {instance_id}: índice de dispositivo no encontrado)"
                    )
                    continue
                instance_ids_by_hardware.setdefault((vendor_id, product_id), []).append(
                    (instance_id, device_index)
                )
            except pygame.error:
                continue

        if not instance_ids_by_hardware:
            details = "; ".join(skipped_devices) or "SDL no enumera mandos para la consulta."
            diagnostic = f"No se inició la consulta de batería: {details}."
            if diagnostic != self.dashboard_battery_scan_diagnostic:
                self.write_console("WARN", diagnostic)
                self.dashboard_battery_scan_diagnostic = diagnostic
            self.dashboard_battery_check_at = now + 15
            return

        self.dashboard_battery_scan_diagnostic = None
        self.dashboard_battery_check_at = now + 15
        generation = self.dashboard_battery_generation
        self.dashboard_battery_started_at = now
        self.dashboard_battery_timed_out = False
        pending_ids = [
            instance_id
            for devices in instance_ids_by_hardware.values()
            for instance_id, _device_index in devices
        ]
        with self.dashboard_battery_lock:
            for instance_id in pending_ids:
                if instance_id not in self.dashboard_battery_diagnostics:
                    self.dashboard_battery_diagnostics[instance_id] = "consulting"
                    self.write_console(
                        "INFO",
                        f"Consultando batería de Windows para el mando SDL {instance_id}.",
                    )

        def worker() -> None:
            values: dict[int, str] = {}
            diagnostics: dict[int, str] = {}
            errors: list[str] = []
            for (vendor_id, product_id), devices in instance_ids_by_hardware.items():
                try:
                    percent, windows_status = gamepad_battery_reading(vendor_id, product_id)
                except Exception as error:
                    errors.append(f"{vendor_id:04X}:{product_id:04X}: {error}")
                    percent = None
                    windows_status = f"raw_game_controller_error: {error}"
                if percent is not None:
                    values.update({instance_id: f"{percent}%" for instance_id, _ in devices})
                    diagnostics.update({
                        instance_id: "windows_battery_report"
                        for instance_id, _ in devices
                    })
                    continue
                for instance_id, device_index in devices:
                    try:
                        percent, hid_status = dualsense_hid_battery_reading(
                            device_index,
                            vendor_id,
                            product_id,
                        )
                    except Exception as error:
                        errors.append(f"{vendor_id:04X}:{product_id:04X}: {error}")
                        hid_status = f"hid_error: {error}"
                        diagnostics[instance_id] = (
                            f"Windows: {windows_status}; HID: {hid_status}"
                        )
                        continue
                    if percent is not None:
                        values[instance_id] = f"~{percent}%"
                        diagnostics[instance_id] = "hid_report_battery"
                    else:
                        diagnostics[instance_id] = (
                            f"Windows: {windows_status}; HID: {hid_status}"
                        )
            self.dashboard_battery_events.put(
                (generation, values, diagnostics, tuple(errors))
            )

        self.dashboard_battery_thread = threading.Thread(
            target=worker,
            name="GDMBattery",
            daemon=True,
        )
        self.dashboard_battery_thread.start()

    def process_dashboard_battery_events(self) -> None:
        while True:
            try:
                generation, values, diagnostics, errors = self.dashboard_battery_events.get_nowait()
            except queue.Empty:
                return
            if generation != self.dashboard_battery_generation:
                continue
            with self.dashboard_battery_lock:
                self.dashboard_battery_values = values
            for instance_id, diagnostic in diagnostics.items():
                with self.dashboard_battery_lock:
                    previous = self.dashboard_battery_diagnostics.get(instance_id)
                    self.dashboard_battery_diagnostics[instance_id] = diagnostic
                if diagnostic != previous:
                    if diagnostic in {"hid_report_battery", "windows_battery_report"}:
                        source = "HID" if diagnostic == "hid_report_battery" else "Windows"
                        message = f"La lectura de batería mediante {source} está disponible para el mando SDL {instance_id}."
                        level = "SUCCESS"
                    else:
                        message = (
                            f"Batería no disponible para el mando SDL {instance_id}: "
                            f"{diagnostic}."
                        )
                        level = "INFO"
                    self.write_console(level, message)
            error_message = "; ".join(errors) if errors else None
            if error_message and error_message != self.dashboard_battery_error:
                self.write_console(
                    "WARN",
                    f"No se pudo consultar el informe de batería de Windows; se muestra el nivel SDL cuando esté disponible. {error_message}",
                )
            elif self.dashboard_battery_error and error_message is None:
                self.write_console("SUCCESS", "La consulta de batería de Windows volvió a funcionar.")
            self.dashboard_battery_error = error_message

    def controller_for(self, joystick):
        if joystick is None:
            return None
        return self.controllers.get(joystick.get_instance_id())

    def playstation_led_target(self):
        joystick = self.active_remap_joystick()
        if joystick is None or self.is_virtual_gamepad(joystick):
            return None
        return joystick

    def request_playstation_led_reapply(self, joystick) -> None:
        if joystick is None:
            return
        try:
            instance_id = joystick.get_instance_id()
        except pygame.error as error:
            self.write_console(
                "WARN",
                f"No se pudo programar la reaplicación del LED del mando: {error}",
            )
            return
        color_key = self.profile_led_color_for_device(joystick)
        if color_key not in PLAYSTATION_LED_COLORS:
            self._playstation_led_pending.pop(instance_id, None)
            return
        available, message = self.playstation_led_availability(joystick)
        if not available:
            self.playstation_led_status = message
            self._playstation_led_pending.pop(instance_id, None)
            return
        self._playstation_led_pending[instance_id] = (
            color_key,
            time.monotonic(),
            0,
        )
        self._playstation_led_last_attempt = None
        self.playstation_led_status = "Reaplicando el color LED guardado al mando físico…"

    def playstation_led_availability(self, joystick) -> tuple[bool, str]:
        if joystick is None:
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
        pending_target = None
        for instance_id in self._playstation_led_pending:
            for device in self.joysticks:
                try:
                    if device.get_init() and device.get_instance_id() == instance_id:
                        pending_target = device
                        break
                except pygame.error:
                    continue
            if pending_target is not None:
                break
        joystick = pending_target or self.active_remap_joystick()
        color_key = self.profile_led_color_for_device(joystick)
        if color_key not in PLAYSTATION_LED_COLORS:
            if pending_target is not None:
                self._playstation_led_pending.pop(pending_target.get_instance_id(), None)
            return
        available, message = self.playstation_led_availability(joystick)
        if not available:
            self.playstation_led_status = message
            return
        pending = None
        instance_id = None
        try:
            instance_id = joystick.get_instance_id()
            attempt = (instance_id, color_key)
            pending = self._playstation_led_pending.get(instance_id)
            if pending is not None and pending[0] != color_key:
                pending = (color_key, time.monotonic(), 0)
                self._playstation_led_pending[instance_id] = pending
            if pending is not None and time.monotonic() < pending[1]:
                return
            if pending is None and self._playstation_led_last_attempt == attempt:
                return
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
                    self.playstation_led_status = f"Informe LED aceptado parcialmente ({written} bytes)."
                    self.write_console(
                        "WARN",
                        f"HID aceptó {written} de {report_size} bytes del informe LED.",
                    )
                    self.schedule_playstation_led_retry(instance_id, color_key, pending)
                    return
            else:
                controller = self.controller_for(joystick)
                applied = bool(controller and controller.set_led(pygame.Color(*rgb)))
            if applied:
                if pending is not None and pending[2] < 2:
                    self._playstation_led_pending[instance_id] = (
                        color_key,
                        time.monotonic() + (1.5 if pending[2] == 0 else 2.5),
                        pending[2] + 1,
                    )
                    return
                self._playstation_led_pending.pop(instance_id, None)
                self._playstation_led_last_attempt = attempt
                self.playstation_led_status = f"Informe LED de {_label} enviado."
                self.write_console(
                    "SUCCESS",
                    f"Informe LED de {_label} enviado tras inicializar el mando PlayStation.",
                )
            else:
                self.playstation_led_status = "El mando no permite controlar sus LED."
                self.write_console("WARN", self.playstation_led_status)
                self.schedule_playstation_led_retry(instance_id, color_key, pending)
        except (pygame.error, AttributeError, ImportError, OSError, RuntimeError) as error:
            self.playstation_led_status = f"No se pudo cambiar el LED: {error}"
            self.write_console("ERROR", self.playstation_led_status)
            if pending is not None and instance_id is not None:
                self.schedule_playstation_led_retry(instance_id, color_key, pending)

    def schedule_playstation_led_retry(
        self,
        instance_id: int,
        color_key: str,
        pending: Optional[tuple[str, float, int]],
    ) -> None:
        if pending is None:
            self._playstation_led_last_attempt = (instance_id, color_key)
            return
        failed_attempts = pending[2] + 1
        if failed_attempts >= 3:
            self._playstation_led_pending.pop(instance_id, None)
            self._playstation_led_last_attempt = (instance_id, color_key)
            self.write_console(
                "ERROR",
                "El mando no aceptó el informe LED guardado tras tres intentos.",
            )
            return
        self._playstation_led_pending[instance_id] = (
            color_key,
            time.monotonic() + (1.5 if failed_attempts == 1 else 2.5),
            failed_attempts,
        )

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
        self.start_dashboard_battery_detection()
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
                battery = self.battery_status_text(joystick)
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

    def virtual_output_active_for_device(self, joystick=None, device_key=None) -> bool:
        if joystick is not None:
            device_key, _stable = self.remap_device_identity(joystick)
        if device_key is None:
            entry = self.active_remap_device()
            if entry is not None:
                device_key = entry[0]
        return device_key in self.virtual_outputs if device_key is not None else False

    def start_virtual_output_for_device(self, joystick) -> None:
        device_key, _stable = self.remap_device_identity(joystick)
        if device_key in self.virtual_outputs:
            return
        bridge = VirtualGamepadBridge()
        self.virtual_outputs[device_key] = bridge
        self.virtual_output_statuses[device_key] = bridge.status

    def stop_virtual_output_for_device(self, device_key: str) -> None:
        bridge = self.virtual_outputs.pop(device_key, None)
        if bridge is not None:
            bridge.close()
        self.virtual_output_statuses[device_key] = "Desactivado"

    def is_active_virtual_gamepad_output(self, instance_id: Optional[int]) -> bool:
        if not self.virtual_outputs or instance_id is None:
            return False
        candidate = next(
            (
                joystick
                for joystick in self.joysticks
                if joystick.get_instance_id() == instance_id
            ),
            None,
        )
        if candidate is None:
            return False

        output_types = set()
        source_instance_ids = set()
        for device_key in self.virtual_outputs:
            source = next(
                (
                    joystick for joystick in self.joysticks
                    if joystick.get_init()
                    and self.remap_device_identity(joystick)[0] == device_key
                ),
                None,
            )
            if source is None:
                continue
            source_instance_ids.add(source.get_instance_id())
            _key, _custom, output_type, _mapping = self.remap_profile_for_device(source)
            output_types.add(output_type)

        if instance_id in source_instance_ids:
            return False
        candidate_name = self.normalize_device_name(candidate.get_name() or "")
        if (
            "Xbox 360" in output_types
            and self.is_xbox_360_name(candidate_name)
        ):
            return True
        if (
            "DualShock 4" in output_types
            and self.model_name(
                candidate.get_name() or "",
                candidate.get_vendor_id() if hasattr(candidate, "get_vendor_id") else 0,
            ) == "PlayStation"
        ):
            other_candidates = []
            for joystick in self.joysticks:
                try:
                    if joystick.get_instance_id() in source_instance_ids:
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
        pygame.draw.rect(self.screen, LIST_BACKGROUND, list_rect, border_radius=10)
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
            fill = LIST_SELECTED if selected else PANEL
            pygame.draw.rect(self.screen, fill, row, border_radius=8)
            if selected:
                pygame.draw.rect(self.screen, ACCENT, row, 1, border_radius=8)
            self.bluetooth_row_rects.append((row, device["id"]))
            label = self.bluetooth_display_name(device)[:54]
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
        pygame.draw.rect(self.screen, LIST_BACKGROUND, list_rect, border_radius=10)
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
                fill = LIST_SELECTED if selected else PANEL_LIGHT
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
        pygame.draw.rect(self.screen, PANEL, rect, border_radius=14)
        self.draw_text(
            "Remapeo y mando virtual",
            rect.x + 20,
            rect.y + 4,
            TEXT,
            self.title_font,
        )
        self.remap_rects = {}
        inner_width = rect.width - 44
        profile_y = rect.y + 40
        devices = self.remap_devices()
        tab_gap = max(5, round(6 * self.ui_scale))
        max_tab_width = round(150 * self.ui_scale)
        visible_count = max(
            1,
            (inner_width + tab_gap) // (max_tab_width + tab_gap),
        )
        max_device_scroll = max(0, len(devices) - visible_count)
        self.remap_device_scroll = max(0, min(self.remap_device_scroll, max_device_scroll))
        selected_index = next(
            (
                index
                for index, entry in enumerate(devices)
                if entry[0] == self.remap_device_key
            ),
            None,
        )
        if selected_index is not None:
            if selected_index < self.remap_device_scroll:
                self.remap_device_scroll = selected_index
            elif selected_index >= self.remap_device_scroll + visible_count:
                self.remap_device_scroll = selected_index - visible_count + 1
        visible_devices = devices[self.remap_device_scroll:self.remap_device_scroll + visible_count]
        tab_height = round(34 * self.ui_scale)
        self.remap_rects["device_tabs"] = pygame.Rect(
            rect.x + 22,
            profile_y,
            inner_width,
            tab_height,
        )
        pygame.draw.line(
            self.screen,
            PANEL_LIGHT,
            (rect.x + 22, profile_y + tab_height),
            (rect.x + 22 + inner_width, profile_y + tab_height),
            1,
        )
        tab_x = rect.x + 22
        for device_key, _joystick, label, _stable in visible_devices:
            connected = _joystick is not None
            name = (_joystick.get_name() or label) if connected else label
            saved_profile = self.device_profiles.get(device_key, {})
            vendor_id = (
                _joystick.get_vendor_id()
                if connected and hasattr(_joystick, "get_vendor_id")
                else saved_profile.get("vendor_id", 0)
            )
            model = self.model_name(name, vendor_id)
            lowered_name = name.casefold()
            if model == "PlayStation":
                tab_label = (
                    "Dualsense"
                    if "dualsense" in lowered_name
                    else "DualShock"
                    if "dualshock" in lowered_name
                    else "PlayStation"
                )
            elif model == "Xbox":
                tab_label = "Xbox Controller"
            else:
                tab_label = "Gamepad"
            duplicate_number = label.rsplit(" ", 1)[-1]
            if label != name and duplicate_number.isdigit():
                tab_label = f"{tab_label} {duplicate_number}"
            icon_width = round(18 * self.ui_scale)
            horizontal_padding = round(9 * self.ui_scale)
            rendered = self.small.render(tab_label, True, TEXT)
            button_width = min(
                inner_width,
                max(
                    round(80 * self.ui_scale),
                    min(
                        round(150 * self.ui_scale),
                        rendered.get_width() + icon_width + horizontal_padding * 3,
                    ),
                ),
            )
            if tab_x + button_width > rect.x + 22 + inner_width:
                break
            button = pygame.Rect(
                tab_x,
                profile_y,
                button_width,
                tab_height,
            )
            self.remap_rects[f"device:{device_key}"] = button
            selected = self.remap_device_key == device_key
            fill = PANEL if selected else BG
            border = ACCENT if selected else PANEL_LIGHT
            foreground = TEXT if selected and connected else MUTED
            pygame.draw.rect(
                self.screen,
                fill,
                button,
                border_top_left_radius=7,
                border_top_right_radius=7,
            )
            pygame.draw.rect(self.screen, border, button, 1, border_radius=6)
            if selected:
                pygame.draw.rect(
                    self.screen,
                    fill,
                    pygame.Rect(button.x + 1, button.bottom - 1, button.width - 2, 2),
                )
            icon_x = button.x + horizontal_padding
            icon_y = button.centery
            if model == "PlayStation":
                icon = self.small.render("PS", True, foreground)
                icon_rect = icon.get_rect(midleft=(icon_x, icon_y))
                self.screen.blit(icon, icon_rect)
                self.screen.blit(icon, icon_rect.move(1, 0))
            elif model == "Xbox":
                icon_radius = max(6, round(7 * self.ui_scale))
                pygame.draw.circle(self.screen, foreground, (icon_x + icon_radius, icon_y), icon_radius, 1)
                pygame.draw.line(
                    self.screen,
                    foreground,
                    (icon_x + icon_radius - 3, icon_y - 3),
                    (icon_x + icon_radius + 3, icon_y + 3),
                    1,
                )
                pygame.draw.line(
                    self.screen,
                    foreground,
                    (icon_x + icon_radius + 3, icon_y - 3),
                    (icon_x + icon_radius - 3, icon_y + 3),
                    1,
                )
            else:
                controller_icon = pygame.Rect(icon_x, icon_y - 5, 17, 10)
                pygame.draw.rect(self.screen, foreground, controller_icon, 1, border_radius=4)
                pygame.draw.line(
                    self.screen,
                    foreground,
                    (icon_x + 3, icon_y),
                    (icon_x + 7, icon_y),
                    1,
                )
                pygame.draw.line(
                    self.screen,
                    foreground,
                    (icon_x + 5, icon_y - 2),
                    (icon_x + 5, icon_y + 2),
                    1,
                )
                pygame.draw.circle(self.screen, foreground, (icon_x + 13, icon_y - 1), 1)
                pygame.draw.circle(self.screen, foreground, (icon_x + 15, icon_y + 2), 1)
            text_left = icon_x + icon_width + round(3 * self.ui_scale)
            available_text_width = button.right - text_left - horizontal_padding
            while tab_label and self.small.size(tab_label)[0] > available_text_width:
                tab_label = tab_label[:-2].rstrip() + "…"
            rendered = self.small.render(tab_label, True, foreground)
            self.screen.blit(
                rendered,
                rendered.get_rect(midleft=(text_left, button.centery)),
            )
            if not connected:
                pygame.draw.circle(
                    self.screen,
                    MUTED,
                    (button.right - round(7 * self.ui_scale), button.y + round(7 * self.ui_scale)),
                    max(2, round(2 * self.ui_scale)),
                )
            tab_x += button_width + tab_gap
        active_device = self.active_remap_device()
        joystick = active_device[1] if active_device is not None else None
        active_device_name = active_device[2] if active_device is not None else None
        profile_key, profile_enabled, profile_output, profile_mapping = (
            self.active_remap_profile()
        )
        if active_device is not None:
            custom_rect = pygame.Rect(rect.x + 22, rect.y + 94, 22, 22)
            self.remap_rects["custom_profile"] = pygame.Rect(
                custom_rect.x, custom_rect.y, inner_width, 27
            )
            pygame.draw.rect(self.screen, PANEL_LIGHT, custom_rect, border_radius=4)
            pygame.draw.rect(
                self.screen,
                ACCENT if profile_enabled else MUTED,
                custom_rect,
                2,
                border_radius=4,
            )
            if profile_enabled:
                pygame.draw.line(self.screen, ACTIVE, (custom_rect.x + 4, custom_rect.y + 11), (custom_rect.x + 9, custom_rect.y + 16), 3)
                pygame.draw.line(self.screen, ACTIVE, (custom_rect.x + 9, custom_rect.y + 16), (custom_rect.x + 18, custom_rect.y + 5), 3)
            self.draw_text_fitted(
                f"Establecer un perfil personalizado para {active_device_name}",
                custom_rect.x + 32,
                custom_rect.y + 1,
                inner_width - 36,
                TEXT,
                self.small,
            )
        else:
            self.draw_text(
                "Conecta un mando físico o elige un perfil personalizado guardado.",
                rect.x + 22,
                rect.y + 98,
                MUTED,
                self.small,
            )

        output_y = rect.y + 126
        output_label = (
            "Dispositivo virtual para este perfil:"
            if profile_enabled
            else "Salida del perfil predeterminado:"
        )
        self.draw_text(output_label, rect.x + 22, output_y, TEXT, self.small)
        choice_y = output_y + 24
        choice_gap = max(8, round(10 * self.ui_scale))
        choice_width = min(190, (inner_width - choice_gap - 160) // 2)
        if profile_enabled:
            for index, output_type in enumerate(OUTPUT_TYPES):
                button = pygame.Rect(
                    rect.x + 22 + index * (choice_width + choice_gap),
                    choice_y,
                    choice_width,
                    round(32 * self.ui_scale),
                )
                self.remap_rects[f"output:{output_type}"] = button
                selected = profile_output == output_type
                pygame.draw.rect(self.screen, ACCENT if selected else PANEL_LIGHT, button, border_radius=8)
                pygame.draw.rect(self.screen, ACCENT if selected else MUTED, button, 1, border_radius=8)
                label = self.small.render(output_type, True, BG if selected else TEXT)
                self.screen.blit(label, label.get_rect(center=button.center))
        else:
            output_text = (
                "Automático · PlayStation → DualShock 4 · otros → Xbox 360"
                if self.default_profile_output == "auto"
                else self.default_profile_output
            )
            self.draw_text_fitted(
                output_text,
                rect.x + 22,
                choice_y + 8,
                choice_width * 2 + choice_gap,
                MUTED,
                self.small,
            )

        toggle = pygame.Rect(rect.right - 178, choice_y, 156, round(32 * self.ui_scale))
        self.remap_rects["toggle"] = toggle
        output_active = self.virtual_output_active_for_device(
            joystick,
            profile_key,
        )
        self.draw_action_button(
            toggle,
            "Detener salida" if output_active else "Iniciar salida virtual",
            joystick is not None,
        )
        output_status = self.virtual_output_statuses.get(
            profile_key,
            "Desactivado",
        )
        status_color = (
            ERROR_COLOR if "No se pudo" in output_status or "Falta" in output_status
            else ACTIVE if "activo" in output_status.casefold()
            else WARN if "conecta" in output_status.casefold()
            else MUTED
        )
        self.draw_text_fitted(
            output_status,
            rect.x + 22,
            choice_y + 38,
            inner_width,
            status_color,
            self.small,
        )
        warning = (
            "ViGEmBus está retirado; GDM no instala drivers. Los juegos pueden detectar también el mando físico."
            if joystick is not None
            else "Conecta el mando para iniciar o detener la salida virtual."
        )
        self.draw_text_fitted(
            warning,
            rect.x + 22,
            choice_y + 58,
            inner_width,
            WARN if joystick is not None else MUTED,
            self.small,
        )

        preference_heading_y = rect.y + 232
        preference_control_y = rect.y + 254
        column_gap = max(18, round(24 * self.ui_scale))
        preference_column_width = (inner_width - column_gap) // 2
        auto_start_enabled = (
            self.device_profiles[profile_key]["auto_start"]
            if profile_enabled
            else self.virtual_output_auto_start
        )
        led_color = (
            self.device_profiles[profile_key]["led_color"]
            if profile_enabled
            else self.playstation_led_color
        )
        self.draw_text(
            "Al conectar · personalizado" if profile_enabled else "Al conectar · predeterminado",
            rect.x + 22,
            preference_heading_y,
            TEXT,
            self.small,
        )
        auto_start = pygame.Rect(rect.x + 22, preference_control_y, 22, 22)
        self.remap_rects["auto_start"] = pygame.Rect(
            auto_start.x,
            auto_start.y,
            preference_column_width,
            25,
        )
        pygame.draw.rect(self.screen, PANEL_LIGHT, auto_start, border_radius=4)
        pygame.draw.rect(
            self.screen,
            ACCENT if auto_start_enabled else MUTED,
            auto_start,
            2,
            border_radius=4,
        )
        if auto_start_enabled:
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
            "Iniciar salida virtual automáticamente",
            auto_start.right + 10,
            auto_start.y + 1,
            preference_column_width - 34,
            TEXT,
            self.small,
        )
        if profile_enabled:
            shutdown_button = pygame.Rect(
                rect.x + 22,
                rect.y + 284,
                preference_column_width,
                28,
            )
            self.remap_rects["shutdown_settings"] = shutdown_button
            self.draw_action_button(shutdown_button, "Configurar apagado…", True)

        led_column_x = rect.x + 22 + preference_column_width + column_gap
        led_target = joystick
        led_available, led_message = self.playstation_led_availability(led_target)
        active_profile_data = self.device_profiles.get(profile_key, {})
        led_name = (
            joystick.get_name() or ""
            if joystick is not None
            else active_profile_data.get("name", "")
        )
        led_vendor = (
            joystick.get_vendor_id()
            if joystick is not None and hasattr(joystick, "get_vendor_id")
            else active_profile_data.get("vendor_id", 0)
        )
        led_product = (
            joystick.get_product_id()
            if joystick is not None and hasattr(joystick, "get_product_id")
            else active_profile_data.get("product_id", 0)
        )
        led_model_supported = (
            self.model_name(led_name, led_vendor) == "PlayStation"
            and (
                is_dualsense(led_name, led_product)
                or led_product in {0x05C4, 0x09CC}
                or any(marker in led_name.casefold() for marker in ("dualshock 4", "dualsense"))
            )
        )
        self.draw_text_fitted(
            "Color LED del mando",
            led_column_x,
            preference_heading_y,
            preference_column_width,
            TEXT,
            self.small,
        )
        if led_available or (joystick is None and led_model_supported):
            swatch_size = 20
            swatch_gap = 5
            swatch_x = led_column_x
            swatch_y = preference_control_y
            for color_key, (_label, rgb) in PLAYSTATION_LED_COLORS.items():
                swatch = pygame.Rect(swatch_x, swatch_y, swatch_size, swatch_size)
                self.remap_rects[f"playstation_led:{color_key}"] = swatch
                pygame.draw.rect(self.screen, rgb, swatch, border_radius=6)
                pygame.draw.rect(
                    self.screen,
                    TEXT if led_color == color_key else PANEL_LIGHT,
                    swatch,
                    2 if led_color == color_key else 1,
                    border_radius=6,
                )
                swatch_x += swatch_size + swatch_gap
            led_off = pygame.Rect(swatch_x + 2, swatch_y, 68, swatch_size)
            self.remap_rects["playstation_led:off"] = led_off
            self.draw_action_button(led_off, "Desactivar", True)
        if joystick is None and led_model_supported:
            led_status = (
                "Se aplicará al conectar el mando."
                if led_color in PLAYSTATION_LED_COLORS
                else "Sin cambio LED al conectar."
            )
        else:
            led_status = self.playstation_led_status if led_available else led_message
        self.draw_text_fitted(
            led_status,
            led_column_x,
            preference_control_y + 23,
            preference_column_width,
            MUTED if led_available or (joystick is None and led_model_supported) else WARN,
            self.small,
        )

        heading_y = rect.y + 316
        self.draw_text(
            "Clic en una fila para cambiar destino · ejes y gatillos directos",
            rect.x + 22, heading_y, ACCENT, self.small,
        )
        target_labels = button_target_labels(profile_output)
        rows_y = heading_y + 24
        column_gap = max(12, round(20 * self.ui_scale))
        mapping_column_width = (inner_width - column_gap) // 2
        row_height = min(25, max(14, (rect.bottom - rows_y - 8) // 8))
        for index, (button_id, label) in enumerate(REMAP_BUTTONS):
            column = index // 8
            row = index % 8
            item_rect = pygame.Rect(
                rect.x + 22 + column * (mapping_column_width + column_gap),
                rows_y + row * row_height,
                mapping_column_width,
                row_height - 2,
            )
            self.remap_rects[f"mapping:{button_id}"] = item_rect
            pygame.draw.rect(
                self.screen,
                PANEL_LIGHT if profile_enabled else PANEL,
                item_rect,
                border_radius=5,
            )
            source_surface = self.small.render(label, True, TEXT)
            self.screen.blit(source_surface, (item_rect.x + 7, item_rect.y + 3))
            target = profile_mapping.get(button_id)
            target_name = target_labels.get(target, "— Desactivado") if target is not None else "— Desactivado"
            rendered = self.small.render(target_name, True, ACCENT)
            target_rect = rendered.get_rect(right=item_rect.right - 7, centery=item_rect.centery)
            self.screen.blit(rendered, target_rect)

    def draw_options_page(self, rect: pygame.Rect) -> None:
        content_bottom = {
            "general": 360,
            "controllers": 700,
            "interface": 410,
            "dependencies": 590,
        }[self.options_section]
        required_height = max(
            self.screen.get_height(),
            rect.y + content_bottom + 8,
        )
        surface = self._options_surface
        if surface is None or surface.get_size() != (self.screen.get_width(), required_height):
            surface = pygame.Surface((self.screen.get_width(), required_height))
            self._options_surface = surface
        surface.fill(
            PANEL,
            pygame.Rect(rect.x, rect.y, rect.width, surface.get_height() - rect.y),
        )
        target_screen = self.screen
        self.screen = surface
        try:
            self.draw_options_page_content(rect)
        finally:
            self.screen = target_screen

        self.draw_panel(rect, "Configuración del programa")
        self.draw_text(
            "Preferencias guardadas automáticamente en este equipo.",
            rect.x + 22,
            rect.y + 57,
            MUTED,
            self.small,
        )
        section_header = pygame.Rect(rect.x + 3, rect.y + 88, rect.width - 6, 48)
        self.screen.blit(surface, section_header.topleft, section_header)

        self.options_viewport = pygame.Rect(
            rect.x + 3,
            rect.y + 138,
            rect.width - 6,
            max(1, rect.height - 152),
        )
        visible_bottom = self.options_viewport.bottom - rect.y
        self.options_max_scroll = max(
            0,
            content_bottom - visible_bottom,
        )
        self.options_scroll = max(0, min(self.options_scroll, self.options_max_scroll))
        source_rect = self.options_viewport.move(0, self.options_scroll)
        self.screen.blit(surface, self.options_viewport.topleft, source_rect)
        if self.options_max_scroll:
            track = pygame.Rect(
                self.options_viewport.right - 5,
                self.options_viewport.y + 4,
                3,
                self.options_viewport.height - 8,
            )
            pygame.draw.rect(self.screen, PANEL_LIGHT, track, border_radius=2)
            thumb_height = max(
                24,
                round(track.height * self.options_viewport.height / (self.options_viewport.height + self.options_max_scroll)),
            )
            thumb_y = track.y + round(
                (track.height - thumb_height)
                * self.options_scroll
                / self.options_max_scroll
            )
            pygame.draw.rect(
                self.screen,
                ACCENT,
                pygame.Rect(track.x, thumb_y, track.width, thumb_height),
                border_radius=2,
            )

    def draw_options_page_content(self, rect: pygame.Rect) -> None:
        self.draw_panel(rect, "Configuración del programa")
        self.draw_text("Preferencias guardadas automáticamente en este equipo.", rect.x + 22, rect.y + 57, MUTED, self.small)

        self.options_rects = {}
        section_tabs = (
            ("general", "General"),
            ("interface", "Interfaz"),
            ("controllers", "Mandos"),
            ("dependencies", "Dependencias"),
        )
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
            startup_checkboxes = (
                ("launch_at_startup", "Iniciar GDM automáticamente al iniciar Windows", self.launch_at_startup, rect.y + 151),
                ("start_minimized_in_tray", "Iniciar minimizado en la bandeja del sistema", self.start_minimized_in_tray, rect.y + 183),
            )
            self.draw_text("Color del icono de la bandeja", rect.x + 22, rect.y + 231, TEXT, self.font)
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
                    rect.y + 265,
                    mode_width,
                    round(34 * self.ui_scale),
                )
                self.options_rects[f"tray_icon_mode:{mode}"] = button
                selected = self.tray_icon_mode == mode
                pygame.draw.rect(self.screen, ACCENT if selected else PANEL_LIGHT, button, border_radius=round(8 * self.ui_scale))
                pygame.draw.rect(self.screen, ACCENT if selected else MUTED, button, 1, border_radius=round(8 * self.ui_scale))
                text = self.small.render(label, True, BG if selected else TEXT)
                self.screen.blit(text, text.get_rect(center=button.center))
            console_checkbox = pygame.Rect(rect.x + 22, rect.y + 308, 22, 22)
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
            self.draw_text("Al presionar Escape", exit_column_x, rect.y + 151, TEXT, self.font)
            escape_prompt = pygame.Rect(exit_column_x, rect.y + 180, 22, 22)
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
            remember_choice = pygame.Rect(exit_column_x, rect.y + 214, 22, 22)
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
            self.draw_text("Al cerrar la ventana con X", exit_column_x, rect.y + 255, TEXT, self.font)
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
                    rect.y + 281,
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
        elif self.options_section == "interface":
            self.draw_text("Tema de la interfaz", rect.x + 22, rect.y + 151, TEXT, self.font)
            theme_choices = (
                ("dark", "Oscuro"),
                ("auto", "Automático"),
                ("light", "Claro"),
            )
            theme_gap = max(8, round(12 * self.ui_scale))
            theme_width = min(
                150,
                (rect.width - 44 - theme_gap * (len(theme_choices) - 1)) // len(theme_choices),
            )
            for index, (theme, label) in enumerate(theme_choices):
                button = pygame.Rect(
                    rect.x + 22 + index * (theme_width + theme_gap),
                    rect.y + 184,
                    theme_width,
                    round(38 * self.ui_scale),
                )
                self.options_rects[f"theme:{theme}"] = button
                selected = self.theme == theme
                pygame.draw.rect(
                    self.screen,
                    ACCENT if selected else PANEL_LIGHT,
                    button,
                    border_radius=round(8 * self.ui_scale),
                )
                pygame.draw.rect(
                    self.screen,
                    ACCENT if selected else MUTED,
                    button,
                    1,
                    border_radius=round(8 * self.ui_scale),
                )
                text = self.small.render(label, True, BG if selected else TEXT)
                self.screen.blit(text, text.get_rect(center=button.center))

            self.draw_text("Pestaña que se abrirá al iniciar", rect.x + 22, rect.y + 243, TEXT, self.font)
            startup_tabs = (
                ("devices", "Mandos conectados"),
                ("tester", "Gamepad Tester"),
                ("bluetooth", "Bluetooth"),
                ("remap", "Remapeo"),
                ("hidhide", "HidHide"),
                ("options", "Configuración"),
                ("about", "Acerca de"),
            )
            startup_columns = 4
            startup_gap = max(8, round(12 * self.ui_scale))
            startup_width = min(
                172,
                (rect.width - 44 - startup_gap * (startup_columns - 1)) // startup_columns,
            )
            for index, (key, label) in enumerate(startup_tabs):
                button = pygame.Rect(
                    rect.x + 22 + (index % startup_columns) * (startup_width + startup_gap),
                    rect.y + 278 + (index // startup_columns) * 44,
                    startup_width,
                    round(38 * self.ui_scale),
                )
                self.options_rects[f"startup_{key}"] = button
                selected = self.startup_tab == key
                pygame.draw.rect(
                    self.screen,
                    ACCENT if selected else PANEL_LIGHT,
                    button,
                    border_radius=round(8 * self.ui_scale),
                )
                pygame.draw.rect(
                    self.screen,
                    ACCENT if selected else MUTED,
                    button,
                    1,
                    border_radius=round(8 * self.ui_scale),
                )
                text = self.small.render(label, True, BG if selected else TEXT)
                self.screen.blit(text, text.get_rect(center=button.center))
            startup_checkboxes = (
                (
                    "welcome_dialog",
                    "Mostrar el cuadro de bienvenida al iniciar GDM",
                    not self.welcome_dialog_dismissed,
                    rect.y + 374,
                ),
            )
            footer_text = ""
        elif self.options_section == "controllers":
            startup_checkboxes = (
                ("auto", "Emparejar automáticamente los mandos compatibles al detectarlos", self.bluetooth_auto_pair, rect.y + 145),
                ("gamepads", "Mostrar solo mandos en la lista Bluetooth", self.bluetooth_gamepads_only, rect.y + 177),
                ("home_disconnect", "Activar acción al mantener Home/PS durante 2 segundos", self.disconnect_on_home_hold, rect.y + 221),
            )
            self.draw_text("Acción para Home/PS", rect.x + 22, rect.y + 267, TEXT, self.font)
            card_gap = max(12, round(20 * self.ui_scale))
            card_width = (rect.width - 44 - card_gap) // 2
            actions = (
                ("disconnect", "Desconectar de Windows", "Conserva el vínculo; el mando puede seguir encendido."),
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
            self.draw_text("Perfil predeterminado para mandos sin perfil personalizado", rect.x + 22, rect.y + 384, TEXT, self.font)
            self.draw_text("Salida virtual", rect.x + 22, rect.y + 416, MUTED, self.small)
            default_outputs = (
                ("auto", "Automático"),
                ("Xbox 360", "Xbox 360"),
                ("DualShock 4", "DualShock 4"),
            )
            output_gap = max(8, round(10 * self.ui_scale))
            output_width = min(190, (rect.width - 44 - output_gap * 2) // 3)
            for index, (output, label) in enumerate(default_outputs):
                button = pygame.Rect(
                    rect.x + 22 + index * (output_width + output_gap),
                    rect.y + 439,
                    output_width,
                    round(32 * self.ui_scale),
                )
                self.options_rects[f"default_output:{output}"] = button
                selected = self.default_profile_output == output
                pygame.draw.rect(self.screen, ACCENT if selected else PANEL_LIGHT, button, border_radius=8)
                pygame.draw.rect(self.screen, ACCENT if selected else MUTED, button, 1, border_radius=8)
                rendered = self.small.render(label, True, BG if selected else TEXT)
                self.screen.blit(rendered, rendered.get_rect(center=button.center))

            self.draw_text(
                "Asignación predeterminada de botones · clic en una fila para cambiarla",
                rect.x + 22,
                rect.y + 482,
                ACCENT,
                self.small,
            )
            default_targets = button_target_labels(
                "DualShock 4"
                if self.default_profile_output == "DualShock 4"
                else "Xbox 360"
            )
            mapping_y = rect.y + 507
            mapping_gap = max(12, round(20 * self.ui_scale))
            mapping_width = (rect.width - 44 - mapping_gap) // 2
            mapping_row_height = 23
            for index, (button_id, label) in enumerate(REMAP_BUTTONS):
                column = index // 8
                row = index % 8
                item_rect = pygame.Rect(
                    rect.x + 22 + column * (mapping_width + mapping_gap),
                    mapping_y + row * mapping_row_height,
                    mapping_width,
                    mapping_row_height - 2,
                )
                self.options_rects[f"default_mapping:{button_id}"] = item_rect
                pygame.draw.rect(self.screen, PANEL_LIGHT, item_rect, border_radius=5)
                self.draw_text_fitted(label, item_rect.x + 7, item_rect.y + 3, item_rect.width // 2 - 8, TEXT, self.small)
                target = self.default_profile_mapping.get(button_id)
                target_label = default_targets.get(target, "— Desactivado") if target is not None else "— Desactivado"
                self.draw_text_fitted(
                    target_label,
                    item_rect.x + item_rect.width // 2,
                    item_rect.y + 3,
                    item_rect.width // 2 - 8,
                    ACCENT,
                    self.small,
                )
            footer_text = ""
        else:
            startup_checkboxes = ()
            self.draw_dependency_settings(rect)
            footer_text = ""

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

    def draw_dependency_settings(self, rect: pygame.Rect) -> None:
        self.dependency_rects = {}
        self.draw_text(
            "Dependencias y herramientas",
            rect.x + 22,
            rect.y + 151,
            TEXT,
            self.font,
        )
        refresh = pygame.Rect(rect.right - 166, rect.y + 146, 144, 32)
        self.dependency_rects["refresh"] = refresh
        self.draw_action_button(refresh, "Volver a comprobar", True)

        rows = (
            ("python", "Instalar dependencias Python"),
            ("vgamepad", "Instalar"),
            ("vigembus", "Abrir página oficial"),
            ("hidhide", "Abrir página oficial"),
        )
        row_y = rect.y + 192
        row_height = 76
        row_gap = 11
        button_width = 152
        for index, (key, action_label) in enumerate(rows):
            item = self.dependency_items[key]
            row = pygame.Rect(
                rect.x + 22,
                row_y + index * (row_height + row_gap),
                rect.width - 44,
                row_height,
            )
            pygame.draw.rect(self.screen, PANEL, row, border_radius=9)
            pygame.draw.rect(self.screen, PANEL_LIGHT, row, 1, border_radius=9)
            title = str(item["title"])
            description = str(item["description"])
            installed = item["installed"]
            status = str(item["status"])
            self.draw_text_fitted(
                title,
                row.x + 14,
                row.y + 7,
                row.width - button_width - 48,
                TEXT,
                self.small,
            )
            self.draw_text_fitted(
                description,
                row.x + 14,
                row.y + 29,
                row.width - button_width - 48,
                MUTED,
                self.small,
            )
            state_color = (
                ACTIVE if installed is True
                else MUTED if installed is None
                else WARN
            )
            self.draw_text_fitted(
                status,
                row.x + 14,
                row.y + 51,
                row.width - button_width - 48,
                state_color,
                self.small,
            )
            button = pygame.Rect(
                row.right - button_width - 12,
                row.y + 20,
                button_width,
                36,
            )
            self.dependency_rects[f"action:{key}"] = button
            if self.dependency_installing == key:
                self.draw_action_button(button, "Instalando…", False)
            elif installed is True:
                self.draw_action_button(button, "Instalado", False)
            elif installed is None:
                self.draw_action_button(button, "Solo Windows", False)
            else:
                self.draw_action_button(
                    button,
                    action_label,
                    self.dependency_installing is None,
                )

        if self.dependency_status:
            color = (
                ERROR_COLOR
                if "falló" in self.dependency_status.casefold()
                or "no se pudo" in self.dependency_status.casefold()
                else ACTIVE
                if "terminada" in self.dependency_status.casefold()
                else MUTED
            )
            self.draw_text_fitted(
                self.dependency_status,
                rect.x + 22,
                row_y + len(rows) * (row_height + row_gap) + 5,
                rect.width - 44,
                color,
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
        color = PANEL_LIGHT if enabled else DISABLED_BACKGROUND
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
                previous = self.bluetooth_devices.get(value["id"])
                self.bluetooth_devices[value["id"]] = value
                if (
                    previous is not None
                    and previous.get("connected")
                    and not value.get("connected")
                ):
                    self._device_refresh_deadline = time.monotonic() + 0.3
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

    def handle_welcome_dialog_click(self, position) -> None:
        checkbox = self.welcome_dialog_rects.get("dont_show")
        if checkbox and checkbox.collidepoint(position):
            self.welcome_dialog_dont_show = not self.welcome_dialog_dont_show
            return
        if self.welcome_dialog_rects.get("dependencies", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            self.welcome_dialog_open = False
            self.active_tab = "options"
            self.options_section = "dependencies"
            self.options_scroll = 0
            return
        if self.welcome_dialog_rects.get("continue", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            self.dismiss_welcome_dialog()

    def dismiss_welcome_dialog(self) -> None:
        self.welcome_dialog_open = False
        if self.welcome_dialog_dont_show:
            self.welcome_dialog_dismissed = True
            self.save_settings()

    def draw_welcome_dialog(self) -> None:
        screen_width, screen_height = self.screen.get_size()
        overlay = pygame.Surface((screen_width, screen_height), pygame.SRCALPHA)
        overlay.fill((0, 0, 0, 175))
        self.screen.blit(overlay, (0, 0))

        width = min(760, screen_width - 32)
        height = min(390, screen_height - 32)
        dialog = pygame.Rect(0, 0, width, height)
        dialog.center = (screen_width // 2, screen_height // 2)
        pygame.draw.rect(self.screen, PANEL, dialog, border_radius=14)
        pygame.draw.rect(self.screen, PANEL_LIGHT, dialog, 1, border_radius=14)
        content_x = dialog.x + 32
        content_width = dialog.width - 64
        self.draw_text("¡Bienvenido a GDM!", content_x, dialog.y + 26, TEXT, self.title_font)
        self.draw_text_fitted(
            "Comprobamos las dependencias disponibles en este equipo.",
            content_x,
            dialog.y + 72,
            content_width,
            MUTED,
            self.small,
        )

        missing = [
            str(item["title"])
            for item in self.dependency_items.values()
            if item["installed"] is False
        ]
        if missing:
            summary = "Requieren atención: " + ", ".join(missing[:3])
            if len(missing) > 3:
                summary += f" y {len(missing) - 3} más"
            summary_color = WARN
        else:
            summary = "Las dependencias Python están instaladas."
            summary_color = ACTIVE
        self.draw_text_fitted(
            summary,
            content_x,
            dialog.y + 112,
            content_width,
            summary_color,
            self.small,
        )
        paragraph_y = dialog.y + 150
        line_height = self.small.get_linesize()
        for text, color in (
            (
                "Puedes instalar los paquetes Python desde GDM o abrir las páginas oficiales de los drivers.",
                TEXT,
            ),
            (
                "Los drivers externos no se ejecutan automáticamente: sus instaladores pueden requerir permisos de administrador.",
                MUTED,
            ),
        ):
            lines = []
            current_line = ""
            for word in text.split():
                candidate = f"{current_line} {word}".strip()
                if current_line and self.small.size(candidate)[0] > content_width:
                    lines.append(current_line)
                    current_line = word
                else:
                    current_line = candidate
            if current_line:
                lines.append(current_line)
            for line_index, line in enumerate(lines):
                self.draw_text(
                    line,
                    content_x,
                    paragraph_y + line_index * line_height,
                    color,
                    self.small,
                )
            paragraph_y += len(lines) * line_height + 12

        button_height = round(42 * self.ui_scale)
        button_y = dialog.bottom - round(62 * self.ui_scale)
        checkbox = pygame.Rect(
            content_x,
            button_y - round(46 * self.ui_scale),
            22,
            22,
        )
        self.welcome_dialog_rects = {
            "dont_show": pygame.Rect(
                checkbox.x,
                checkbox.y,
                content_width,
                26,
            ),
        }
        pygame.draw.rect(self.screen, PANEL_LIGHT, checkbox, border_radius=4)
        pygame.draw.rect(
            self.screen,
            ACCENT if self.welcome_dialog_dont_show else MUTED,
            checkbox,
            2,
            border_radius=4,
        )
        if self.welcome_dialog_dont_show:
            pygame.draw.line(
                self.screen,
                ACTIVE,
                (checkbox.x + 4, checkbox.y + 11),
                (checkbox.x + 9, checkbox.y + 16),
                3,
            )
            pygame.draw.line(
                self.screen,
                ACTIVE,
                (checkbox.x + 9, checkbox.y + 16),
                (checkbox.x + 18, checkbox.y + 5),
                3,
            )
        self.draw_text(
            "No volver a mostrar esta bienvenida",
            checkbox.right + 10,
            checkbox.y + 2,
            TEXT,
            self.small,
        )

        gap = 16
        button_width = (dialog.width - 64 - gap) // 2
        dependencies = pygame.Rect(
            content_x,
            button_y,
            button_width,
            button_height,
        )
        continue_button = pygame.Rect(
            dependencies.right + gap,
            button_y,
            button_width,
            button_height,
        )
        self.welcome_dialog_rects["dependencies"] = dependencies
        self.welcome_dialog_rects["continue"] = continue_button
        self.draw_action_button(dependencies, "Ver dependencias", True)
        self.draw_action_button(continue_button, "Continuar", True)

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

    def windows_apps_use_light_theme(self) -> bool:
        if platform.system() != "Windows":
            return False
        import winreg

        key_path = r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path) as key:
            value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
        if not isinstance(value, int):
            raise OSError("Windows devolvió un valor no válido para AppsUseLightTheme.")
        return bool(value)

    def update_color_theme(self, force: bool = False) -> None:
        now = time.monotonic()
        if not force and now < self._theme_check_at:
            return
        self._theme_check_at = now + 5
        effective_theme = self.theme
        if effective_theme == "auto":
            try:
                effective_theme = "light" if self.windows_apps_use_light_theme() else "dark"
            except (ImportError, OSError) as error:
                effective_theme = "dark"
                if not self._theme_warning_logged:
                    self.write_console(
                        "WARN",
                        f"No se pudo detectar el tema de aplicaciones de Windows; se usará oscuro: {error}",
                    )
                    self._theme_warning_logged = True
        if effective_theme != self.effective_theme:
            apply_color_theme(effective_theme)
            self.effective_theme = effective_theme

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

    def draw_shutdown_dialog(self) -> None:
        screen_width, screen_height = self.screen.get_size()
        overlay = pygame.Surface((screen_width, screen_height), pygame.SRCALPHA)
        overlay.fill((0, 0, 0, 175))
        self.screen.blit(overlay, (0, 0))

        settings = self.shutdown_settings_for_device(self.active_remap_joystick())
        combo_mode = settings["method"] == "combo"
        width = min(760, screen_width - 32)
        height = min(570 if combo_mode else 450, screen_height - 32)
        dialog = pygame.Rect(0, 0, width, height)
        dialog.center = (screen_width // 2, screen_height // 2)
        pygame.draw.rect(self.screen, PANEL, dialog, border_radius=14)
        pygame.draw.rect(self.screen, PANEL_LIGHT, dialog, 1, border_radius=14)

        content_x = dialog.x + 28
        content_width = dialog.width - 56
        self.draw_text(
            "Apagado del mando",
            content_x,
            dialog.y + 20,
            TEXT,
            self.title_font,
        )
        self.draw_text_fitted(
            "Mantén botones 2 segundos o configura el apagado tras inactividad.",
            content_x,
            dialog.y + 58,
            content_width,
            MUTED,
            self.small,
        )

        enabled = pygame.Rect(content_x, dialog.y + 92, 22, 22)
        self.shutdown_dialog_rects = {
            "enabled": pygame.Rect(content_x, enabled.y, content_width, 26),
        }
        pygame.draw.rect(self.screen, PANEL_LIGHT, enabled, border_radius=4)
        pygame.draw.rect(
            self.screen,
            ACCENT if settings["enabled"] else MUTED,
            enabled,
            2,
            border_radius=4,
        )
        if settings["enabled"]:
            pygame.draw.line(
                self.screen,
                ACTIVE,
                (enabled.x + 4, enabled.y + 11),
                (enabled.x + 9, enabled.y + 16),
                3,
            )
            pygame.draw.line(
                self.screen,
                ACTIVE,
                (enabled.x + 9, enabled.y + 16),
                (enabled.x + 18, enabled.y + 5),
                3,
            )
        self.draw_text(
            "Activar acción al mantener botones en este mando",
            enabled.right + 10,
            enabled.y + 1,
            TEXT,
            self.small,
        )

        idle_y = dialog.y + 124
        duration_width = 34
        duration_value_width = 70
        plus_x = content_x + content_width - duration_width
        value_x = plus_x - duration_value_width - 8
        minus_x = value_x - duration_width - 8
        idle_checkbox = pygame.Rect(content_x, idle_y, 22, 22)
        self.shutdown_dialog_rects["idle_enabled"] = pygame.Rect(
            content_x,
            idle_y,
            minus_x - content_x - 12,
            26,
        )
        pygame.draw.rect(self.screen, PANEL_LIGHT, idle_checkbox, border_radius=4)
        pygame.draw.rect(
            self.screen,
            ACCENT if settings["idle_enabled"] else MUTED,
            idle_checkbox,
            2,
            border_radius=4,
        )
        if settings["idle_enabled"]:
            pygame.draw.line(
                self.screen,
                ACTIVE,
                (idle_checkbox.x + 4, idle_checkbox.y + 11),
                (idle_checkbox.x + 9, idle_checkbox.y + 16),
                3,
            )
            pygame.draw.line(
                self.screen,
                ACTIVE,
                (idle_checkbox.x + 9, idle_checkbox.y + 16),
                (idle_checkbox.x + 18, idle_checkbox.y + 5),
                3,
            )
        self.draw_text(
            "Apagar por inactividad",
            idle_checkbox.right + 10,
            idle_checkbox.y + 1,
            TEXT,
            self.small,
        )
        for key, x, label in (
            ("idle_minutes:decrease", minus_x, "-"),
            ("idle_minutes:increase", plus_x, "+"),
        ):
            button = pygame.Rect(x, idle_y - 3, duration_width, 28)
            self.shutdown_dialog_rects[key] = button
            self.draw_action_button(button, label, True)
        value_rect = pygame.Rect(value_x, idle_y - 3, duration_value_width, 28)
        pygame.draw.rect(self.screen, PANEL_LIGHT, value_rect, border_radius=7)
        value_surface = self.small.render(
            f"{settings['idle_minutes']} min",
            True,
            TEXT,
        )
        self.screen.blit(
            value_surface,
            value_surface.get_rect(center=value_rect.center),
        )

        mode_y = dialog.y + 164
        self.draw_text("Método de activación", content_x, mode_y, TEXT, self.small)
        mode_gap = 10
        mode_width = (content_width - mode_gap) // 2
        for index, (mode, label) in enumerate(
            (("home", "Botón Home / PS"), ("combo", "Combinación"))
        ):
            button = pygame.Rect(
                content_x + index * (mode_width + mode_gap),
                mode_y + 24,
                mode_width,
                34,
            )
            self.shutdown_dialog_rects[f"method:{mode}"] = button
            selected = settings["method"] == mode
            pygame.draw.rect(
                self.screen,
                ACCENT if selected else PANEL_LIGHT,
                button,
                border_radius=8,
            )
            pygame.draw.rect(
                self.screen,
                ACCENT if selected else MUTED,
                button,
                1,
                border_radius=8,
            )
            label_surface = self.small.render(
                label,
                True,
                BG if selected else TEXT,
            )
            self.screen.blit(label_surface, label_surface.get_rect(center=button.center))

        if combo_mode:
            combo_y = dialog.y + 239
            self.draw_text(
                "Selecciona los botones que deben mantenerse juntos",
                content_x,
                combo_y,
                TEXT,
                self.small,
            )
            grid_y = combo_y + 25
            columns = 4
            gap_x = 8
            gap_y = 4
            button_width = (content_width - gap_x * (columns - 1)) // columns
            button_height = 29
            selected_buttons = set(settings["buttons"])
            for index, (button_id, label) in enumerate(REMAP_BUTTONS):
                column = index % columns
                row = index // columns
                button = pygame.Rect(
                    content_x + column * (button_width + gap_x),
                    grid_y + row * (button_height + gap_y),
                    button_width,
                    button_height,
                )
                self.shutdown_dialog_rects[f"button:{button_id}"] = button
                selected = button_id in selected_buttons
                pygame.draw.rect(
                    self.screen,
                    PANEL_LIGHT if selected else PANEL,
                    button,
                    border_radius=7,
                )
                pygame.draw.rect(
                    self.screen,
                    ACCENT if selected else PANEL_LIGHT,
                    button,
                    2 if selected else 1,
                    border_radius=7,
                )
                label_surface = self.small.render(
                    label,
                    True,
                    TEXT if selected else MUTED,
                )
                self.screen.blit(
                    label_surface,
                    label_surface.get_rect(center=button.center),
                )
            if not selected_buttons:
                self.draw_text(
                    "Aún no hay botones seleccionados.",
                    content_x,
                    dialog.y + 399,
                    WARN,
                    self.small,
                )
            action_y = dialog.y + 429
        else:
            action_y = dialog.y + 254

        self.draw_text("Acción", content_x, action_y, TEXT, self.small)
        action_y += 23
        action_gap = 10
        action_width = (content_width - action_gap) // 2
        for index, (action, label) in enumerate(
            (
                ("disconnect", "Desconectar Bluetooth"),
                ("unpair", "Desemparejar"),
            )
        ):
            button = pygame.Rect(
                content_x + index * (action_width + action_gap),
                action_y,
                action_width,
                36,
            )
            self.shutdown_dialog_rects[f"action:{action}"] = button
            selected = settings["action"] == action
            pygame.draw.rect(
                self.screen,
                ACCENT if selected else PANEL_LIGHT,
                button,
                border_radius=8,
            )
            pygame.draw.rect(
                self.screen,
                ACCENT if selected else MUTED,
                button,
                1,
                border_radius=8,
            )
            label_surface = self.small.render(
                label,
                True,
                BG if selected else TEXT,
            )
            self.screen.blit(label_surface, label_surface.get_rect(center=button.center))

        detail = (
            "Desconectar de Bluetooth conserva el emparejamiento y no apaga físicamente el mando."
            if settings["action"] == "disconnect"
            else "Desemparejar elimina el vínculo Bluetooth guardado; no apaga físicamente el mando."
        )
        detail_y = action_y + 41
        self.draw_text_fitted(
            detail,
            content_x,
            detail_y,
            content_width,
            MUTED,
            self.small,
        )
        close = pygame.Rect(
            dialog.right - 126,
            dialog.bottom - 46,
            98,
            30,
        )
        self.shutdown_dialog_rects["close"] = close
        self.draw_action_button(close, "Listo", True)
        focused = self.shutdown_dialog_rects.get(self.shutdown_focus_key or "")
        if focused is not None:
            pygame.draw.rect(
                self.screen,
                ACTIVE,
                focused.inflate(4, 4),
                2,
                border_radius=8,
            )

    def handle_shutdown_dialog_click(self, position) -> None:
        for key, rect in self.shutdown_dialog_rects.items():
            if rect.collidepoint(position):
                self.shutdown_focus_key = key
                break
        if self.shutdown_dialog_rects.get("close", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            self.shutdown_dialog_open = False
            return
        settings = self.shutdown_settings_for_device(self.active_remap_joystick())
        enabled = self.shutdown_dialog_rects.get("enabled")
        if enabled and enabled.collidepoint(position):
            self.set_active_shutdown_setting("shutdown_enabled", not settings["enabled"])
        elif self.shutdown_dialog_rects["idle_enabled"].collidepoint(position):
            self.set_active_shutdown_setting(
                "shutdown_idle_enabled",
                not settings["idle_enabled"],
            )
        elif self.shutdown_dialog_rects["idle_minutes:decrease"].collidepoint(position):
            self.set_active_shutdown_setting(
                "shutdown_idle_minutes",
                max(1, settings["idle_minutes"] - 1),
            )
        elif self.shutdown_dialog_rects["idle_minutes:increase"].collidepoint(position):
            self.set_active_shutdown_setting(
                "shutdown_idle_minutes",
                min(60, settings["idle_minutes"] + 1),
            )
        else:
            for mode in ("home", "combo"):
                rect = self.shutdown_dialog_rects.get(f"method:{mode}")
                if rect and rect.collidepoint(position):
                    self.set_active_shutdown_setting("shutdown_method", mode)
                    break
            else:
                for button_id in REMAP_BUTTON_IDS:
                    rect = self.shutdown_dialog_rects.get(f"button:{button_id}")
                    if rect and rect.collidepoint(position):
                        self.set_active_shutdown_setting(
                            "shutdown_buttons",
                            button_id,
                        )
                        break
                else:
                    for action in ("disconnect", "unpair"):
                        rect = self.shutdown_dialog_rects.get(f"action:{action}")
                        if rect and rect.collidepoint(position):
                            self.set_active_shutdown_setting(
                                "shutdown_action",
                                action,
                            )
                            break
                    else:
                        return
        self.save_settings()

    def move_shutdown_dialog_focus(self, direction: str) -> bool:
        focusable = self.shutdown_dialog_rects
        if not focusable:
            return False
        if self.shutdown_focus_key not in focusable:
            self.shutdown_focus_key = next(iter(focusable))
            return True
        current = focusable[self.shutdown_focus_key]
        candidates = []
        for key, rect in focusable.items():
            if key == self.shutdown_focus_key:
                continue
            delta_x = rect.centerx - current.centerx
            delta_y = rect.centery - current.centery
            if direction == "left" and delta_x < -1:
                distance = -delta_x + abs(delta_y) * 1.5
            elif direction == "right" and delta_x > 1:
                distance = delta_x + abs(delta_y) * 1.5
            elif direction == "up" and delta_y < -1:
                distance = -delta_y + abs(delta_x) * 1.5
            elif direction == "down" and delta_y > 1:
                distance = delta_y + abs(delta_x) * 1.5
            else:
                continue
            candidates.append((distance, key))
        if candidates:
            _distance, self.shutdown_focus_key = min(candidates)
        else:
            ordered = list(focusable)
            index = ordered.index(self.shutdown_focus_key)
            step = -1 if direction in {"left", "up"} else 1
            self.shutdown_focus_key = ordered[(index + step) % len(ordered)]
        return True

    def activate_shutdown_dialog_focus(self) -> bool:
        if self.shutdown_focus_key is None:
            return False
        rect = self.shutdown_dialog_rects.get(self.shutdown_focus_key)
        if rect is None:
            return False
        self.handle_shutdown_dialog_click(rect.center)
        return True

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
        pygame.draw.rect(self.screen, CONSOLE_BACKGROUND, panel, border_radius=10)
        pygame.draw.rect(self.screen, CONSOLE_BORDER, panel, 1, border_radius=10)
        title_height = 32
        header = pygame.Rect(panel.x, panel.y, panel.width, title_height)
        pygame.draw.rect(self.screen, CONSOLE_HEADER, header, border_top_left_radius=10, border_top_right_radius=10)
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
        pygame.draw.rect(self.screen, CONSOLE_LOG_BACKGROUND, log_rect, border_radius=5)
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

    def keyboard_focusable_rects(self) -> dict[str, pygame.Rect]:
        focusable = {
            f"nav:{tab}": rect
            for tab, rect in self.nav_rects.items()
        }
        if self.active_tab == "devices":
            rect = self.devices_rects.get("windows_panel")
            if rect is not None:
                focusable["devices:windows_panel"] = rect
        elif self.active_tab == "tester":
            focusable.update({
                f"tester:{key}": rect
                for key, rect in self.tester_rects.items()
            })
        elif self.active_tab == "bluetooth":
            focusable.update({
                f"bluetooth:device:{device_id}": rect
                for rect, device_id in self.bluetooth_row_rects
            })
            focusable.update({
                f"bluetooth:{key}": rect
                for key, rect in self.bluetooth_rects.items()
                if key in {"pair", "remove", "refresh"}
            })
        elif self.active_tab == "remap":
            focusable.update({
                f"remap:{key}": rect
                for key, rect in self.remap_rects.items()
                if key != "device_tabs"
            })
        elif self.active_tab == "hidhide":
            focusable.update({
                f"hidhide:{key}": rect
                for key, rect in self.hidhide_rects.items()
                if key != "list"
            })
        elif self.active_tab == "options":
            for key, rect in self.options_rects.items():
                if key.startswith("section_"):
                    focusable[f"options:{key}"] = rect
                else:
                    focusable[f"options:{key}"] = rect.move(
                        0,
                        -self.options_scroll,
                    )
        return focusable

    def move_keyboard_focus(self, direction: str) -> bool:
        focusable = self.keyboard_focusable_rects()
        if len(focusable) < 2:
            return False
        self.keyboard_navigation_active = True
        if self.keyboard_focus_key not in focusable:
            current_key = f"nav:{self.active_tab}"
            self.keyboard_focus_key = (
                current_key if current_key in focusable else next(iter(focusable))
            )
            if len(focusable) < 2:
                return False

        current = focusable[self.keyboard_focus_key]
        current_x, current_y = current.center
        directional = []
        for key, rect in focusable.items():
            if key == self.keyboard_focus_key:
                continue
            delta_x = rect.centerx - current_x
            delta_y = rect.centery - current_y
            if direction == "left" and delta_x < -1:
                primary, secondary = -delta_x, abs(delta_y)
            elif direction == "right" and delta_x > 1:
                primary, secondary = delta_x, abs(delta_y)
            elif direction == "up" and delta_y < -1:
                primary, secondary = -delta_y, abs(delta_x)
            elif direction == "down" and delta_y > 1:
                primary, secondary = delta_y, abs(delta_x)
            else:
                continue
            directional.append((primary + secondary * 1.5, key))

        if directional:
            _distance, next_key = min(directional)
        else:
            def wrap_distance(item: tuple[str, pygame.Rect]) -> tuple[int, int]:
                key, rect = item
                if direction == "left":
                    edge = -rect.centerx
                    secondary = abs(rect.centery - current_y)
                elif direction == "right":
                    edge = rect.centerx
                    secondary = abs(rect.centery - current_y)
                elif direction == "up":
                    edge = -rect.centery
                    secondary = abs(rect.centerx - current_x)
                else:
                    edge = rect.centery
                    secondary = abs(rect.centerx - current_x)
                return edge, secondary

            next_key, _rect = min(
                (
                    item
                    for item in focusable.items()
                    if item[0] != self.keyboard_focus_key
                ),
                key=wrap_distance,
            )

        self.keyboard_focus_key = next_key
        if (
            self.active_tab == "options"
            and not next_key.startswith("options:section_")
        ):
            source_key = next_key.removeprefix("options:")
            source_rect = self.options_rects.get(source_key)
            if source_rect is not None:
                if source_rect.bottom - self.options_scroll > self.options_viewport.bottom:
                    self.options_scroll = min(
                        self.options_max_scroll,
                        source_rect.bottom - self.options_viewport.bottom,
                    )
                elif source_rect.top - self.options_scroll < self.options_viewport.top:
                    self.options_scroll = max(
                        0,
                        source_rect.top - self.options_viewport.top,
                    )
        return True

    def activate_navigation_tab(self, tab: str) -> None:
        self.active_tab = tab
        self.keyboard_focus_key = f"nav:{tab}"
        if tab == "hidhide":
            self.start_hidhide_task("refresh")
        elif (
            tab == "devices"
            and not self.hidhide_devices_loaded
            and self.hidhide_service.cli_path is not None
        ):
            self.start_hidhide_task("refresh")

    def activate_keyboard_focus(self) -> bool:
        key = self.keyboard_focus_key
        if key is None:
            return False
        if key.startswith("nav:"):
            self.activate_navigation_tab(key.removeprefix("nav:"))
            return True
        if key.startswith("tester:"):
            rect = self.tester_rects.get(key.removeprefix("tester:"))
            return bool(rect and self.handle_tester_click(rect.center))
        if key.startswith("options:"):
            option_key = key.removeprefix("options:")
            rect = self.options_rects.get(option_key)
            return bool(rect and self.handle_options_click(rect.center))
        if key.startswith("remap:"):
            rect = self.remap_rects.get(key.removeprefix("remap:"))
            return bool(rect and self.handle_remap_click(rect.center))
        if key.startswith("hidhide:"):
            rect = self.hidhide_rects.get(key.removeprefix("hidhide:"))
            return bool(rect and self.handle_hidhide_click(rect.center))
        if key.startswith("bluetooth:device:"):
            device_id = key.removeprefix("bluetooth:device:")
            row = next(
                (
                    rect for rect, candidate_id in self.bluetooth_row_rects
                    if candidate_id == device_id
                ),
                None,
            )
            return bool(row and self.handle_bluetooth_click(row.center))
        if key.startswith("bluetooth:"):
            rect = self.bluetooth_rects.get(key.removeprefix("bluetooth:"))
            return bool(rect and self.handle_bluetooth_click(rect.center))
        if key == "devices:windows_panel":
            rect = self.devices_rects.get("windows_panel")
            if rect is not None:
                self.open_windows_game_controllers()
                return True
        return False

    def draw_keyboard_focus(self) -> None:
        if not self.keyboard_navigation_active:
            return
        focusable = self.keyboard_focusable_rects()
        if self.keyboard_focus_key not in focusable:
            self.keyboard_focus_key = f"nav:{self.active_tab}"
        rect = focusable.get(self.keyboard_focus_key)
        if rect is None:
            return
        if (
            self.active_tab == "options"
            and not self.keyboard_focus_key.startswith("options:section_")
        ):
            old_clip = self.screen.get_clip()
            self.screen.set_clip(self.options_viewport)
            pygame.draw.rect(
                self.screen,
                ACTIVE,
                rect.inflate(4, 4),
                2,
                border_radius=7,
            )
            self.screen.set_clip(old_clip)
        else:
            pygame.draw.rect(
                self.screen,
                ACTIVE,
                rect.inflate(4, 4),
                2,
                border_radius=7,
            )

    def handle_options_click(self, position) -> bool:
        for section in ("general", "controllers", "interface", "dependencies"):
            rect = self.options_rects.get(f"section_{section}")
            if rect and rect.collidepoint(position):
                self.options_section = section
                self.options_scroll = 0
                return True

        if self.options_section == "dependencies":
            if self.dependency_rects.get("refresh", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
                self.refresh_dependency_status()
                self.dependency_status = "Comprobación de dependencias actualizada."
                return True
            for key in ("python", "vgamepad", "vigembus", "hidhide"):
                rect = self.dependency_rects.get(f"action:{key}")
                if not rect or not rect.collidepoint(position):
                    continue
                item = self.dependency_items[key]
                if item["installed"] is True or item["installed"] is None:
                    return True
                if key in {"python", "vgamepad"}:
                    self.start_dependency_install(key)
                else:
                    self.open_dependency_website(key)
                return True

        for theme in ("dark", "auto", "light"):
            rect = self.options_rects.get(f"theme:{theme}")
            if rect and rect.collidepoint(position):
                if self.theme != theme:
                    self.theme = theme
                    self.update_color_theme(force=True)
                    self.save_settings()
                return True

        if self.options_rects.get("welcome_dialog", pygame.Rect(0, 0, 0, 0)).collidepoint(position):
            self.welcome_dialog_dismissed = not self.welcome_dialog_dismissed
            self.save_settings()
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

        for output in ("auto", *OUTPUT_TYPES):
            rect = self.options_rects.get(f"default_output:{output}")
            if rect and rect.collidepoint(position):
                if self.default_profile_output != output:
                    self.default_profile_output = output
                    _device_key, custom_profile, _output, _mapping = self.active_remap_profile()
                    if not custom_profile:
                        self.request_playstation_led_reapply(
                            self.active_remap_joystick()
                        )
                self.save_settings()
                return True

        for button_id in REMAP_BUTTON_IDS:
            rect = self.options_rects.get(f"default_mapping:{button_id}")
            if not rect or not rect.collidepoint(position):
                continue
            target_output = (
                "DualShock 4"
                if self.default_profile_output == "DualShock 4"
                else "Xbox 360"
            )
            targets = tuple(button_target_labels(target_output)) + (None,)
            current = self.default_profile_mapping.get(button_id)
            try:
                next_index = (targets.index(current) + 1) % len(targets)
            except ValueError:
                next_index = 0
            self.default_profile_mapping[button_id] = targets[next_index]
            self.save_settings()
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
        for device_key, _joystick, _name, _stable in self.remap_devices():
            rect = self.remap_rects.get(f"device:{device_key}")
            if rect and rect.collidepoint(position):
                self.remap_device_key = device_key
                return True

        custom_rect = self.remap_rects.get("custom_profile")
        if custom_rect and custom_rect.collidepoint(position):
            active_device = self.active_remap_device()
            if active_device is None:
                return True
            device_key, joystick, _name, _stable = active_device
            if joystick is None:
                profile = self.device_profiles[device_key]
            else:
                _device_key, profile = self.ensure_device_profile(joystick)
            profile["enabled"] = not profile["enabled"]
            self.save_settings()
            return True

        shutdown_settings = self.remap_rects.get("shutdown_settings")
        if shutdown_settings and shutdown_settings.collidepoint(position):
            _profile_key, profile_enabled, _output, _mapping = self.active_remap_profile()
            if profile_enabled:
                self.shutdown_dialog_open = True
                self.shutdown_focus_key = "enabled"
            return True

        for output_type in OUTPUT_TYPES:
            rect = self.remap_rects.get(f"output:{output_type}")
            if rect and rect.collidepoint(position):
                profile_key, enabled, _current_output, _mapping = self.active_remap_profile()
                if not enabled:
                    return True
                if self.device_profiles[profile_key]["output"] != output_type:
                    self.device_profiles[profile_key]["output"] = output_type
                    self.request_playstation_led_reapply(
                        self.active_remap_joystick()
                    )
                self.save_settings()
                return True

        toggle = self.remap_rects.get("toggle")
        if toggle and toggle.collidepoint(position):
            active_device = self.active_remap_device()
            joystick = active_device[1] if active_device is not None else None
            device_key = active_device[0] if active_device is not None else None
            if joystick is None or device_key is None:
                self.virtual_output_status = "Conecta este mando para iniciar la salida virtual."
                return True
            if self.virtual_output_active_for_device(joystick, device_key):
                self.stop_virtual_output_for_device(device_key)
            else:
                try:
                    self.start_virtual_output_for_device(joystick)
                except pygame.error as error:
                    self.virtual_output_statuses[device_key] = (
                        f"No se pudo iniciar la salida virtual: {error}"
                    )
                    self.write_console("ERROR", self.virtual_output_statuses[device_key])
            self.save_settings()
            return True
        auto_start = self.remap_rects.get("auto_start")
        if auto_start and auto_start.collidepoint(position):
            joystick = self.active_remap_joystick()
            current_value = (
                self.profile_auto_start_for_device(joystick)
                if joystick is not None
                else self.virtual_output_auto_start
            )
            self.set_active_profile_preference("auto_start", not current_value)
            self.save_settings()
            return True
        for color_key in PLAYSTATION_LED_COLORS:
            rect = self.remap_rects.get(f"playstation_led:{color_key}")
            if rect and rect.collidepoint(position):
                joystick = self.active_remap_joystick()
                if joystick is not None:
                    available, message = self.playstation_led_availability(joystick)
                    if not available:
                        self.playstation_led_status = message
                        return True
                self.set_active_profile_preference("led_color", color_key)
                if joystick is not None:
                    self._playstation_led_pending.pop(joystick.get_instance_id(), None)
                self._playstation_led_last_attempt = None
                self.playstation_led_status = (
                    "Aplicando color al mando PlayStation físico…"
                    if joystick is not None
                    else "El color LED se aplicará al conectar el mando."
                )
                self.save_settings()
                return True
        led_off = self.remap_rects.get("playstation_led:off")
        if led_off and led_off.collidepoint(position):
            joystick = self.active_remap_joystick()
            self.set_active_profile_preference("led_color", None)
            if joystick is not None:
                self._playstation_led_pending.pop(joystick.get_instance_id(), None)
            self._playstation_led_last_attempt = None
            self.playstation_led_status = "GDM no cambiará el LED del mando."
            self.save_settings()
            return True

        profile_key, enabled, output_type, mapping = self.active_remap_profile()
        if not enabled:
            return False
        target_labels = button_target_labels(output_type)
        targets: tuple[Optional[str], ...] = tuple(target_labels) + (None,)
        for button_id in REMAP_BUTTON_IDS:
            rect = self.remap_rects.get(f"mapping:{button_id}")
            if not rect or not rect.collidepoint(position):
                continue
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
        for device_key, bridge in tuple(self.virtual_outputs.items()):
            joystick = next(
                (
                    device
                    for device in self.joysticks
                    if device.get_init()
                    and self.remap_device_identity(device)[0] == device_key
                ),
                None,
            )
            if joystick is None:
                continue
            _key, _custom, output_type, mapping = self.remap_profile_for_device(joystick)
            status = bridge.update(
                True,
                output_type,
                joystick,
                self.controller_for(joystick),
                mapping,
            )
            if status != self.virtual_output_statuses.get(device_key):
                self.virtual_output_statuses[device_key] = status
                level = "ERROR" if "No se pudo" in status or "Falta" in status else "INFO"
                self.write_console(level, f"{joystick.get_name()}: {status}")
        active_entry = self.active_remap_device()
        self.virtual_output_status = (
            self.virtual_output_statuses.get(active_entry[0], "Desactivado")
            if active_entry is not None
            else "Desactivado"
        )

    def gamepad_has_activity(self, joystick) -> bool:
        instance_id = None
        try:
            instance_id = joystick.get_instance_id()
            for button_index in range(joystick.get_numbuttons()):
                if joystick.get_button(button_index):
                    return True

            for axis_index in range(joystick.get_numaxes()):
                value = joystick.get_axis(axis_index)
                if axis_index in (4, 5):
                    if value > 0.2:
                        return True
                elif abs(value) > 0.2:
                    return True

            for hat_index in range(joystick.get_numhats()):
                if joystick.get_hat(hat_index) != (0, 0):
                    return True
        except pygame.error as error:
            if (
                instance_id is None
                or instance_id not in self.shutdown_idle_input_errors
            ):
                self.write_console(
                    "WARN",
                    f"No se pudo comprobar la actividad del mando para el apagado automático: {error}",
                )
                if instance_id is not None:
                    self.shutdown_idle_input_errors.add(instance_id)
            return True
        if instance_id is not None:
            self.shutdown_idle_input_errors.discard(instance_id)
        return False

    def check_shutdown_idle_timers(self) -> None:
        now = time.monotonic()
        tracked_instances = set()
        for joystick in getattr(self, "joysticks", ()):
            try:
                if not joystick.get_init() or self.is_virtual_gamepad(joystick):
                    continue
                instance_id = joystick.get_instance_id()
                settings = self.shutdown_settings_for_device(joystick)
            except pygame.error as error:
                self.write_console(
                    "WARN",
                    f"No se pudo comprobar el temporizador de inactividad del mando: {error}",
                )
                continue

            if not (
                settings["custom"]
                and settings["idle_enabled"]
            ):
                continue

            tracked_instances.add(instance_id)
            if self.pending_home_action:
                continue
            if instance_id in self.shutdown_idle_triggered:
                continue
            if self.gamepad_has_activity(joystick):
                self.shutdown_idle_last_activity[instance_id] = now
                continue

            last_activity = self.shutdown_idle_last_activity.setdefault(
                instance_id,
                now,
            )
            if now - last_activity < settings["idle_minutes"] * 60:
                continue

            self.shutdown_idle_triggered.add(instance_id)
            self.write_console(
                "INFO",
                f"{joystick.get_name()}: transcurrieron {settings['idle_minutes']} "
                "minutos sin actividad; ejecutando la acción de apagado configurada.",
            )
            self.request_shutdown_action(joystick, settings)

        for instance_id in tuple(self.shutdown_idle_last_activity):
            if instance_id not in tracked_instances:
                self.shutdown_idle_last_activity.pop(instance_id, None)
        self.shutdown_idle_triggered.intersection_update(tracked_instances)
        self.shutdown_idle_input_errors.intersection_update(tracked_instances)

    def check_home_hold(self) -> None:
        if self.pending_home_action:
            if time.monotonic() >= self.pending_home_deadline:
                self.pending_home_action = None
                self.pending_home_joystick = None
                self.pending_home_auto_pair_retry = True
                self.bluetooth_status = "No se encontró el mando Bluetooth para ejecutar la acción solicitada."
                self.write_console("ERROR", self.bluetooth_status)
            return
        self.check_shutdown_idle_timers()
        if self.pending_home_action:
            return
        joystick = self.home_action_joystick()
        settings = self.shutdown_settings_for_device(joystick)
        if not settings["enabled"] or joystick is None:
            self.home_hold_started = None
            self.home_hold_triggered = False
            return
        if settings["method"] == "combo":
            pressed = self.shutdown_button_combo_pressed(
                joystick,
                settings["buttons"],
            )
        else:
            pressed = self.home_button_pressed(joystick)

        if not pressed:
            self.home_hold_started = None
            self.home_hold_triggered = False
            return
        if self.home_hold_started is None:
            self.home_hold_started = pygame.time.get_ticks()
            trigger_label = (
                "la combinación seleccionada"
                if settings["method"] == "combo"
                else "Home/PS"
            )
            self.write_console(
                "INFO",
                f"{trigger_label.capitalize()} presionado; mantenlo 2 segundos para ejecutar la acción.",
            )
            return
        if self.home_hold_triggered or pygame.time.get_ticks() - self.home_hold_started < 2000:
            return

        self.home_hold_triggered = True
        self.request_shutdown_action(joystick, settings)

    def request_shutdown_action(self, joystick, settings: dict) -> None:
        match = self.find_bluetooth_device(
            joystick,
            prefer_classic=settings["action"] == "disconnect",
        )
        retry_auto_pair = not settings["custom"]
        if match is None:
            self.pending_home_action = settings["action"]
            self.pending_home_joystick = joystick
            self.pending_home_deadline = time.monotonic() + 10
            self.pending_home_auto_pair_retry = retry_auto_pair
            self.bluetooth_status = "Actualizando Bluetooth para encontrar el mando…"
            self.write_console("INFO", self.bluetooth_status)
            self.bluetooth.refresh(retry_auto_pair=False)
            return

        self.execute_home_action(
            settings["action"],
            match,
            retry_auto_pair=retry_auto_pair,
        )

    def home_action_joystick(self):
        remap_joystick = self.active_remap_joystick()
        if remap_joystick is not None:
            _key, custom_profile, _output, _mapping = self.remap_profile_for_device(
                remap_joystick
            )
            if custom_profile:
                return remap_joystick

        joystick = self.joystick
        if joystick is not None and not self.is_virtual_gamepad(joystick):
            return joystick
        return self.find_physical_gamepad()

    def shutdown_button_combo_pressed(self, joystick, button_ids: list[str]) -> bool:
        if not button_ids:
            return False
        controller = self.controller_for(joystick)
        for button_id in button_ids:
            if controller is not None:
                button = REMAP_SDL_BUTTONS.get(button_id)
                if button is None:
                    return False
                try:
                    if not controller.get_button(button):
                        return False
                except pygame.error:
                    return False
            else:
                raw_index = REMAP_RAW_BUTTONS.get(button_id)
                if raw_index is None or not self.pressed(joystick, raw_index):
                    return False
        return True

    def home_button_pressed(self, joystick) -> bool:
        controller = self.controller_for(joystick)
        if controller is not None:
            try:
                if controller.get_button(pygame.CONTROLLER_BUTTON_GUIDE):
                    return True
            except pygame.error as error:
                self.write_console("WARN", f"No se pudo leer Home/PS mediante SDL: {error}")

        try:
            name = joystick.get_name() or ""
            vendor_id = (
                joystick.get_vendor_id()
                if hasattr(joystick, "get_vendor_id")
                else 0
            )
            product_id = (
                joystick.get_product_id()
                if hasattr(joystick, "get_product_id")
                else 0
            )
            model = self.model_name(name, vendor_id)
            if model == "Xbox":
                lowered_name = name.casefold()
                is_xbox_one = (
                    product_id in XBOX_ONE_GUIDE_PRODUCT_IDS
                    or (
                        not product_id
                        and "xbox one" in lowered_name
                    )
                )
                if is_xbox_one and xinput_guide_pressed_for_joystick(joystick):
                    return True
                home_button_index = None if is_xbox_one else HOME_RAW_BUTTONS["Xbox"]
            else:
                home_button_index = HOME_RAW_BUTTONS.get(model)
            return home_button_index is not None and self.pressed(
                joystick,
                home_button_index,
            )
        except pygame.error as error:
            self.write_console("ERROR", f"No se pudo leer el botón Home/PS del mando: {error}")
            return False

    def change_tester_joystick(self, offset: int) -> bool:
        if not self.joysticks:
            return False
        self.selected = (self.selected + offset) % len(self.joysticks)
        self.home_hold_started = None
        self.home_hold_triggered = False
        return True

    def handle_tester_click(self, position) -> bool:
        for direction, offset in (("previous", -1), ("next", 1)):
            rect = self.tester_rects.get(direction)
            if rect is not None and rect.collidepoint(position):
                return self.change_tester_joystick(offset)
        return False

    def execute_home_action(
        self,
        action: str,
        match: dict,
        retry_auto_pair: bool = True,
    ) -> None:
        device_name = match.get("display_name", match["name"])
        if action == "unpair":
            self.bluetooth_status = f"Desemparejando {device_name} de Windows…"
            self.write_console("WARN", self.bluetooth_status)
            self.bluetooth.unpair(
                match["id"],
                retry_auto_pair=retry_auto_pair,
            )
        else:
            self.bluetooth_status = f"Solicitando desconexión de {device_name} en Windows…"
            self.write_console("INFO", self.bluetooth_status)
            self.bluetooth.disconnect(
                match["id"],
                display_name=device_name,
            )

    def try_pending_home_action(self) -> None:
        joystick = self.pending_home_joystick
        if joystick is None or self.pending_home_action is None:
            return
        match = self.find_bluetooth_device(
            joystick,
            prefer_classic=self.pending_home_action == "disconnect",
        )
        if match is None:
            return
        action = self.pending_home_action
        retry_auto_pair = self.pending_home_auto_pair_retry
        self.pending_home_action = None
        self.pending_home_joystick = None
        self.pending_home_deadline = 0.0
        self.pending_home_auto_pair_retry = True
        self.execute_home_action(
            action,
            match,
            retry_auto_pair=retry_auto_pair,
        )

    def find_bluetooth_device(
        self,
        joystick,
        prefer_classic: bool = False,
    ) -> Optional[dict]:
        def preferred(devices: list[dict]) -> Optional[dict]:
            candidates = devices
            if prefer_classic:
                classic_devices = [
                    device for device in candidates
                    if device.get("transport") == "Bluetooth clásico"
                ]
                if classic_devices:
                    candidates = classic_devices
            connected_devices = [
                device for device in candidates if device.get("connected")
            ]
            candidates = connected_devices or candidates
            groups: dict[str, list[dict]] = {}
            for device in candidates:
                container_id = str(device.get("container_id", "")).strip().casefold()
                instance_id = str(
                    device.get("device_instance_id", "")
                ).strip().casefold()
                identity = (
                    f"container:{container_id}"
                    if container_id
                    else f"instance:{instance_id}"
                    if instance_id
                    else f"id:{device.get('id', '')}"
                )
                groups.setdefault(identity, []).append(device)
            if len(groups) != 1:
                return None
            match = self.preferred_bluetooth_match(next(iter(groups.values())))
            if match is not None and controller_display_name != joystick_name:
                match = {
                    **match,
                    "display_name": controller_display_name,
                }
            return match

        gamepads = [
            device for device in self.bluetooth_devices.values()
            if device.get("gamepad")
        ]
        joystick_name = joystick.get_name().strip().casefold()
        vendor = joystick.get_vendor_id() if hasattr(joystick, "get_vendor_id") else 0
        product = joystick.get_product_id() if hasattr(joystick, "get_product_id") else 0
        controller_display_name = GDMGamepadManager.bluetooth_controller_display_name(
            joystick_name,
            vendor,
            product,
        )
        exact_name_matches = [
            device for device in gamepads
            if device["name"].strip().casefold() == joystick_name
        ]
        if exact_name_matches:
            return preferred(exact_name_matches)

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
                return preferred(id_matches)

        def canonical_device_name(name: str) -> str:
            normalized = self.normalize_device_name(name)
            for transport_prefix in ("bluetooth", "wireless"):
                if normalized.startswith(transport_prefix):
                    return normalized[len(transport_prefix):]
            return normalized

        canonical_joystick_name = canonical_device_name(joystick_name)
        normalized_matches = [
            device for device in gamepads
            if canonical_device_name(device.get("name", ""))
            == canonical_joystick_name
        ]
        if normalized_matches:
            connected_matches = [
                device for device in normalized_matches
                if device.get("connected")
            ]
            candidates = connected_matches or normalized_matches
            physical_device_groups: dict[str, list[dict]] = {}
            for device in candidates:
                container_id = str(device.get("container_id", "")).strip().casefold()
                instance_id = str(
                    device.get("device_instance_id", "")
                ).strip().casefold()
                identity = (
                    f"container:{container_id}"
                    if container_id
                    else f"instance:{instance_id}"
                    if instance_id
                    else f"id:{device.get('id', '')}"
                )
                physical_device_groups.setdefault(identity, []).append(device)
            if len(physical_device_groups) == 1:
                return preferred(next(iter(physical_device_groups.values())))
            if len(physical_device_groups) > 1:
                return None

        model = self.model_name(joystick_name, vendor)
        controller_generation = GDMGamepadManager.bluetooth_controller_generation(
            joystick_name,
            vendor,
            product,
        )
        if model in {"Xbox", "PlayStation"}:
            family_matches = [
                device for device in gamepads
                if self.bluetooth_device_model(device) == model
            ]
            if model == "PlayStation" and controller_generation is not None:
                compatible_names = {
                    "wirelesscontroller",
                    "ps4controller",
                    "ps5controller",
                }
                family_matches = [
                    device for device in gamepads
                    if (
                        self.bluetooth_device_model(device) == model
                        or self.normalize_device_name(
                            device.get("name", "")
                        ) in compatible_names
                    )
                    and GDMGamepadManager.bluetooth_device_generation(device)
                    in {None, controller_generation}
                ]
            connected_matches = [
                device for device in family_matches if device.get("connected")
            ]
            match = preferred(connected_matches or family_matches)
            if match is not None:
                return match
        return None

    def bluetooth_device_model(self, device: dict) -> str:
        identifiers = " ".join(
            str(value)
            for value in (
                device.get("hardware_ids", []),
                device.get("device_instance_id", ""),
            )
        ).casefold()
        vendor_id = (
            0x054C if "vid_054c" in identifiers
            else 0x045E if "vid_045e" in identifiers
            else 0
        )
        return self.model_name(device.get("name", ""), vendor_id)

    @staticmethod
    def bluetooth_controller_generation(
        name: str,
        vendor_id: int = 0,
        product_id: int = 0,
    ) -> Optional[str]:
        if vendor_id == 0x054C:
            if product_id in {0x05C4, 0x09CC}:
                return "dualshock4"
            if product_id in {0x0CE6, 0x0DF2}:
                return "dualsense"
        normalized_name = GDMGamepadManager.normalize_device_name(name)
        if any(marker in normalized_name for marker in ("dualshock4", "ps4")):
            return "dualshock4"
        if any(marker in normalized_name for marker in ("dualsense", "ps5")):
            return "dualsense"
        return None

    @staticmethod
    def bluetooth_device_generation(device: dict) -> Optional[str]:
        identifiers = " ".join(
            str(value)
            for value in (
                device.get("hardware_ids", []),
                device.get("device_instance_id", ""),
            )
        ).casefold()
        product_ids = set(
            re.findall(r"vid_054c&pid_([0-9a-f]{4})", identifiers)
        )
        if product_ids & {"05c4", "09cc"}:
            return "dualshock4"
        if product_ids & {"0ce6", "0df2"}:
            return "dualsense"
        return GDMGamepadManager.bluetooth_controller_generation(
            device.get("name", "")
        )

    @staticmethod
    def bluetooth_controller_display_name(
        name: str,
        vendor_id: int = 0,
        product_id: int = 0,
    ) -> str:
        generation = GDMGamepadManager.bluetooth_controller_generation(
            name,
            vendor_id,
            product_id,
        )
        if generation == "dualshock4":
            return "DualShock 4"
        if generation == "dualsense":
            return "DualSense"
        return name

    def bluetooth_display_name(self, device: dict) -> str:
        for joystick in self.joysticks:
            try:
                if not joystick.get_init() or self.is_virtual_gamepad(joystick):
                    continue
                vendor_id = (
                    joystick.get_vendor_id()
                    if hasattr(joystick, "get_vendor_id")
                    else 0
                )
                product_id = (
                    joystick.get_product_id()
                    if hasattr(joystick, "get_product_id")
                    else 0
                )
                if vendor_id != 0x054C:
                    continue
                match = self.find_bluetooth_device(joystick)
                if match is not None and match.get("id") == device.get("id"):
                    return self.bluetooth_controller_display_name(
                        joystick.get_name() or "",
                        vendor_id,
                        product_id,
                    )
            except pygame.error:
                continue
        return device.get("display_name", device["name"])

    @staticmethod
    def preferred_bluetooth_match(devices: list[dict]) -> Optional[dict]:
        return max(
            devices,
            key=lambda device: (
                bool(device.get("connected")),
                bool(device.get("paired")),
                device.get("transport") == "Bluetooth clásico",
            ),
            default=None,
        )

    def bluetooth_device_has_live_joystick(self, device: dict) -> bool:
        device_name = device.get("name", "").strip().casefold()
        for joystick in self.joysticks:
            try:
                if not joystick.get_init() or self.is_virtual_gamepad(joystick):
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
        self.tester_rects = {}
        if len(self.joysticks) > 1:
            button_size = max(28, round(32 * self.ui_scale))
            button_gap = max(5, round(6 * self.ui_scale))
            button_y = rect.y + 13
            next_button = pygame.Rect(
                rect.right - 20 - button_size,
                button_y,
                button_size,
                button_size,
            )
            previous_button = pygame.Rect(
                next_button.x - button_gap - button_size,
                button_y,
                button_size,
                button_size,
            )
            self.tester_rects = {
                "previous": previous_button,
                "next": next_button,
            }
            self.draw_action_button(previous_button, "<", True)
            self.draw_action_button(next_button, ">", True)
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
        self.draw_text_fitted(f"Batería: {self.battery_status_text(joystick)}", x, y, inner_width, MUTED, self.small)
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

    def battery_status_text(self, joystick) -> str:
        try:
            instance_id = joystick.get_instance_id()
        except pygame.error:
            return "no disponible"
        with self.dashboard_battery_lock:
            battery = self.dashboard_battery_values.get(instance_id)
            diagnostic = self.dashboard_battery_diagnostics.get(instance_id)
        if battery is not None:
            return battery
        if diagnostic == "consulting":
            return "consultando…"
        if diagnostic is not None:
            return "no disponible · ver consola"
        return self.battery_text(joystick)

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
            home_active = self.home_button_pressed(joystick)
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
        self.process_dashboard_battery_events()
        self.process_hidhide_events()
        self.process_startup_events()
        self.process_dependency_events()
        self.process_tray_events()
        for event in pygame.event.get():
            if self.shutdown_dialog_open:
                if event.type in (pygame.QUIT, pygame.WINDOWCLOSE):
                    self.handle_window_close()
                elif event.type == pygame.MOUSEBUTTONDOWN and event.button == 1:
                    self.handle_shutdown_dialog_click(event.pos)
                elif event.type == pygame.KEYDOWN and event.key == pygame.K_ESCAPE:
                    self.shutdown_dialog_open = False
                elif event.type == pygame.KEYDOWN and event.key in (
                    pygame.K_LEFT,
                    pygame.K_RIGHT,
                    pygame.K_UP,
                    pygame.K_DOWN,
                ):
                    directions = {
                        pygame.K_LEFT: "left",
                        pygame.K_RIGHT: "right",
                        pygame.K_UP: "up",
                        pygame.K_DOWN: "down",
                    }
                    self.move_shutdown_dialog_focus(directions[event.key])
                elif event.type == pygame.KEYDOWN and event.key in (
                    pygame.K_RETURN,
                    pygame.K_KP_ENTER,
                ):
                    self.activate_shutdown_dialog_focus()
                continue
            if self.welcome_dialog_open:
                if event.type in (pygame.QUIT, pygame.WINDOWCLOSE):
                    self.handle_window_close()
                elif event.type == pygame.MOUSEBUTTONDOWN and event.button == 1:
                    self.handle_welcome_dialog_click(event.pos)
                elif event.type == pygame.KEYDOWN:
                    if event.key in (pygame.K_ESCAPE, pygame.K_RETURN, pygame.K_KP_ENTER):
                        self.dismiss_welcome_dialog()
                continue
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
                self.keyboard_navigation_active = False
                for tab, rect in self.nav_rects.items():
                    if rect.collidepoint(event.pos):
                        self.activate_navigation_tab(tab)
                        break
                else:
                    console_clicked = self.handle_console_click(event.pos)
                    if not console_clicked:
                        if self.active_tab == "devices":
                            if self.devices_rects.get("windows_panel", pygame.Rect(0, 0, 0, 0)).collidepoint(event.pos):
                                self.open_windows_game_controllers()
                        elif self.active_tab == "tester":
                            self.handle_tester_click(event.pos)
                        elif self.active_tab == "bluetooth":
                            self.handle_bluetooth_click(event.pos)
                        elif self.active_tab == "remap":
                            self.handle_remap_click(event.pos)
                        elif self.active_tab == "hidhide":
                            self.handle_hidhide_click(event.pos)
                        elif self.active_tab == "options":
                            section_clicked = any(
                                self.options_rects.get(f"section_{section}", pygame.Rect(0, 0, 0, 0)).collidepoint(event.pos)
                                for section in ("general", "controllers", "interface", "dependencies")
                            )
                            if section_clicked:
                                self.handle_options_click(event.pos)
                            elif self.options_viewport.collidepoint(event.pos):
                                self.handle_options_click(
                                    (event.pos[0], event.pos[1] + self.options_scroll)
                                )
            elif event.type == pygame.MOUSEWHEEL:
                mouse_position = pygame.mouse.get_pos()
                if self.console_visible and self.console_rects.get("log", pygame.Rect(0, 0, 0, 0)).collidepoint(mouse_position):
                    self.console_scroll = max(0, min(max(0, len(self.console_lines)), self.console_scroll + event.y * 3))
                elif self.active_tab == "options" and self.options_viewport.collidepoint(mouse_position):
                    self.options_scroll = max(
                        0,
                        min(self.options_max_scroll, self.options_scroll - event.y * 32),
                    )
                elif (
                    self.active_tab == "remap"
                    and self.remap_rects.get("device_tabs", pygame.Rect(0, 0, 0, 0)).collidepoint(mouse_position)
                ):
                    tabs = self.remap_rects["device_tabs"]
                    tab_gap = max(5, round(6 * self.ui_scale))
                    max_tab_width = round(150 * self.ui_scale)
                    visible_count = max(
                        1,
                        (tabs.width + tab_gap) // (max_tab_width + tab_gap),
                    )
                    self.remap_device_scroll = max(
                        0,
                        min(
                            max(0, len(self.remap_devices()) - visible_count),
                            self.remap_device_scroll - event.y,
                        ),
                    )
                elif (
                    self.active_tab == "bluetooth"
                    and self.bluetooth_rects.get("list", pygame.Rect(0, 0, 0, 0)).collidepoint(mouse_position)
                ):
                    self.bluetooth_scroll = max(0, self.bluetooth_scroll - event.y)
                elif (
                    self.active_tab == "hidhide"
                    and self.hidhide_rects.get("list", pygame.Rect(0, 0, 0, 0)).collidepoint(mouse_position)
                ):
                    self.hidhide_scroll = max(0, self.hidhide_scroll - event.y)
                elif (
                    self.active_tab == "devices"
                    and self.devices_rects.get("list", pygame.Rect(0, 0, 0, 0)).collidepoint(mouse_position)
                ):
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
                elif event.key in (
                    pygame.K_LEFT,
                    pygame.K_RIGHT,
                    pygame.K_UP,
                    pygame.K_DOWN,
                ):
                    directions = {
                        pygame.K_LEFT: "left",
                        pygame.K_RIGHT: "right",
                        pygame.K_UP: "up",
                        pygame.K_DOWN: "down",
                    }
                    self.move_keyboard_focus(directions[event.key])
                elif event.key in (pygame.K_RETURN, pygame.K_KP_ENTER):
                    self.activate_keyboard_focus()
                elif event.key == pygame.K_r and self.active_tab == "tester":
                    self.refresh_devices()
                    self.write_console("INFO", "Actualizando lista local de mandos SDL.")
                elif event.key == pygame.K_r and self.active_tab == "bluetooth":
                    self.bluetooth.refresh()
                    self.write_console("INFO", "Solicitando reinicio de búsqueda Bluetooth.")
        if not self.shutdown_dialog_open:
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
            self.update_color_theme()
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
            self.draw_keyboard_focus()
            if self.welcome_dialog_open:
                self.draw_welcome_dialog()
            if self.escape_dialog_open:
                self.draw_escape_dialog()
            if self.shutdown_dialog_open:
                self.draw_shutdown_dialog()
            pygame.display.flip()
            self.clock.tick(60)
        if self._window_resize_deadline is not None:
            self.save_settings()
        self.restore_signal_stop.set()
        if self.restore_signal_thread is not None:
            self.restore_signal_thread.join(timeout=1)
        for bridge in self.virtual_outputs.values():
            bridge.close()
        self.virtual_outputs.clear()
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
