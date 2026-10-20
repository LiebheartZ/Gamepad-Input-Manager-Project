"""Direct DualSense lightbar output over its matching physical HID interface."""

from __future__ import annotations

import ctypes
from functools import lru_cache
from pathlib import Path
import platform
import re
import zlib


DUALSENSE_PRODUCT_IDS = {0x0CE6, 0x0DF2}


def is_dualsense(name: str, product_id: int) -> bool:
    lowered_name = name.casefold()
    return product_id in DUALSENSE_PRODUCT_IDS or "dualsense" in lowered_name or "ps5" in lowered_name


@lru_cache(maxsize=1)
def _sdl_joystick_path_api():
    if platform.system() != "Windows":
        return None
    try:
        import pygame

        library = ctypes.WinDLL(str(Path(pygame.__file__).parent / "SDL2.dll"))
        function = library.SDL_JoystickPathForIndex
        function.argtypes = (ctypes.c_int,)
        function.restype = ctypes.c_char_p
        return function
    except (AttributeError, OSError):
        return None


def joystick_path_for_index(device_index: int) -> str | None:
    function = _sdl_joystick_path_api()
    if function is None:
        return None
    path = function(device_index)
    return path.decode("utf-8", errors="replace") if path else None


def normalize_device_path(path: str | bytes | bytearray) -> str:
    if isinstance(path, (bytes, bytearray)):
        path = bytes(path).decode("utf-8", errors="replace")
    return path.replace("/", "\\").casefold()


def vendor_product_ids_from_path(path: str | bytes | bytearray) -> tuple[int, int] | None:
    normalized = normalize_device_path(path)
    vendor_match = re.search(r"vid(?:_|&)(?:0002)?([0-9a-f]{4})(?:[^0-9a-f]|$)", normalized)
    product_match = re.search(r"pid(?:_|&)(?:0002)?([0-9a-f]{4})(?:[^0-9a-f]|$)", normalized)
    if vendor_match is None or product_match is None:
        return None
    return int(vendor_match.group(1), 16), int(product_match.group(1), 16)


def build_output_report(rgb: tuple[int, int, int], bluetooth: bool) -> bytes:
    if any(not 0 <= channel <= 255 for channel in rgb):
        raise ValueError("LED color channels must be between 0 and 255.")

    offset = 1 if bluetooth else 0
    report = bytearray(78 if bluetooth else 48)
    report[0] = 0x31 if bluetooth else 0x02
    if bluetooth:
        report[1] = 0x02
    report[1 + offset] = 0x0C
    report[2 + offset] = 0x55
    report[39 + offset] = 0x02
    report[42 + offset] = 0x02
    report[43 + offset] = 0x02
    report[44 + offset] = 0x04
    report[45 + offset:48 + offset] = bytes(rgb)

    if bluetooth:
        crc = zlib.crc32(b"\xA2" + report[:-4]) & 0xFFFFFFFF
        report[-4:] = crc.to_bytes(4, "little")
    return bytes(report)


def set_lightbar_color(
    device_index: int,
    vendor_id: int,
    product_id: int,
    rgb: tuple[int, int, int],
) -> tuple[int, int]:
    if platform.system() != "Windows":
        raise RuntimeError("El control HID del LED DualSense está disponible solo en Windows.")

    import hid

    sdl_path = joystick_path_for_index(device_index)
    if not sdl_path:
        raise RuntimeError("SDL no expuso la ruta HID del mando; no se enviaron datos.")
    normalized_path = normalize_device_path(sdl_path)
    path_ids = vendor_product_ids_from_path(sdl_path)
    if path_ids is None:
        raise RuntimeError("No se pudieron leer VID/PID de la ruta del mando SDL; no se envió el color.")
    path_vendor_id, path_product_id = path_ids
    if (
        (vendor_id and vendor_id != path_vendor_id)
        or (product_id and product_id != path_product_id)
    ):
        raise RuntimeError("La ruta SDL no coincide con el VID/PID del mando seleccionado; no se envió el color.")
    vendor_id = vendor_id or path_vendor_id
    product_id = product_id or path_product_id

    candidates = [
        item for item in hid.enumerate(vendor_id, product_id)
        if item.get("vendor_id") == vendor_id
        and item.get("product_id") == product_id
        and item.get("path") is not None
        and normalize_device_path(item.get("path", b"")) == normalized_path
    ]
    if not candidates:
        raise RuntimeError(
            f"hidapi no encontró la interfaz {vendor_id:04X}:{product_id:04X} "
            "que corresponde a la ruta SDL del mando."
        )
    if len(candidates) > 1:
        raise RuntimeError(
            f"hidapi encontró {len(candidates)} interfaces para la ruta SDL "
            "del mando; no se envió el color."
        )

    candidate = candidates[0]
    path = candidate["path"]
    interface_number = candidate.get("interface_number")
    bluetooth_path = any(
        token in normalized_path
        for token in ("bthenum", "bthle", "bluetooth")
    )
    usb_path = "usb" in normalized_path
    if bluetooth_path:
        bluetooth = True
    elif usb_path:
        bluetooth = False
    elif isinstance(interface_number, int) and interface_number == -1:
        bluetooth = True
    elif isinstance(interface_number, int) and interface_number >= 0:
        bluetooth = False
    else:
        raise RuntimeError("No se pudo determinar si el DualSense usa USB o Bluetooth; no se envió el color.")

    report = build_output_report(rgb, bluetooth)
    device = hid.device()
    try:
        device.open_path(path)
        written = device.write(report)
        if written <= 0:
            raise OSError(f"HID no aceptó el informe de salida (resultado: {written}).")
    finally:
        device.close()
    return written, len(report)
