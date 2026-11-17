"""Windows Bluetooth discovery, pairing, and unpairing service."""

from __future__ import annotations

import asyncio
import ctypes
import platform
import queue
import re
import threading
import time
import traceback
from typing import Any


def is_likely_gamepad(name: str) -> bool:
    normalized = re.sub(r"[^a-z0-9]", "", name.casefold())
    return any(
        marker in normalized
        for marker in (
            "gamepad",
            "gamecontroller",
            "wirelesscontroller",
            "dualshock",
            "dualsense",
            "playstationcontroller",
            "xboxcontroller",
            "xboxwirelesscontroller",
            "xboxadaptivecontroller",
            "steamcontroller",
            "8bitdo",
            "procontroller",
            "switchcontroller",
            "joycon",
        )
    )


def gamepad_match(name: str, properties: dict[str, Any]) -> tuple[bool, str]:
    if is_likely_gamepad(name):
        return True, "nombre"

    values: list[str] = []
    for key in ("System.Devices.DeviceInstanceId", "System.Devices.HardwareIds"):
        value = properties.get(key)
        if isinstance(value, str):
            values.append(value)
        elif value is not None:
            try:
                values.extend(str(item) for item in value)
            except TypeError:
                values.append(str(value))
    identifiers = " ".join(values).casefold()
    supported_ids = {
        ("054c", "05c4"), ("054c", "09cc"), ("054c", "0ce6"), ("054c", "0df2"),
        ("045e", "02d1"), ("045e", "02dd"), ("045e", "02e0"), ("045e", "02ea"),
        ("045e", "0b12"), ("045e", "0b13"), ("045e", "0b20"), ("045e", "0b22"),
        ("057e", "2006"), ("057e", "2007"), ("057e", "2009"),
        ("2dc8", "6000"), ("2dc8", "6001"), ("2dc8", "6002"),
    }
    found_ids = set(re.findall(r"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", identifiers))
    if found_ids & supported_ids:
        return True, "ID de fabricante"
    return False, ""


def bluetooth_gamepad_display_name(
    name: str,
    properties: dict[str, Any],
) -> str:
    values = []
    for key in ("System.Devices.DeviceInstanceId", "System.Devices.HardwareIds"):
        value = properties.get(key)
        if isinstance(value, str):
            values.append(value)
        elif value is not None:
            try:
                values.extend(str(item) for item in value)
            except TypeError:
                values.append(str(value))
    identifiers = " ".join(values).casefold()
    product_ids = set(
        re.findall(r"vid_054c&pid_([0-9a-f]{4})", identifiers)
    )
    if product_ids & {"05c4", "09cc"}:
        return "DualShock 4"
    if product_ids & {"0ce6", "0df2"}:
        return "DualSense"
    return name


def property_is_true(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return value != 0
    if isinstance(value, str):
        return value.strip().casefold() in {"true", "1", "yes"}
    return False


async def open_bluetooth_connection(
    device_id: str,
    transport: str,
    classic_device_type: Any,
    le_device_type: Any,
) -> Any:
    device_types = (
        (classic_device_type, le_device_type)
        if transport == "Bluetooth clásico"
        else (le_device_type, classic_device_type)
    )
    errors = []
    for device_type in device_types:
        try:
            connection = await device_type.from_id_async(device_id)
        except Exception as error:
            errors.append(f"{device_type.__name__}: {error}")
            continue
        if connection is not None:
            return connection
    if errors:
        raise RuntimeError(
            "Windows no pudo abrir el identificador del mando con Bluetooth "
            "clásico ni Bluetooth LE. " + " | ".join(errors)
        )
    return None


def pairing_status_text(status: str, action: str) -> str:
    names = {
        "PAIRED": "emparejado correctamente",
        "ALREADY_PAIRED": "ya estaba emparejado",
        "UNPAIRED": "emparejamiento eliminado",
        "ALREADY_UNPAIRED": "ya estaba eliminado",
        "NOT_READY_TO_PAIR": "no está listo para emparejar; activa el modo de emparejamiento",
        "AUTHENTICATION_FAILURE": "falló la autenticación",
        "AUTHENTICATION_TIMEOUT": "se agotó el tiempo de autenticación",
        "AUTHENTICATION_NOT_ALLOWED": "el dispositivo no permite la autenticación",
        "CONNECTION_REJECTED": "el dispositivo rechazó la conexión",
        "NO_SUPPORTED_PROFILES": "Windows no encontró un perfil Bluetooth compatible",
        "ACCESS_DENIED": "Windows denegó el acceso",
        "PAIRING_CANCELED": "emparejamiento cancelado",
        "FAILED": "Windows no pudo completar la operación",
        "OPERATION_ALREADY_IN_PROGRESS": "ya hay otra operación en curso",
        "HARDWARE_FAILURE": "fallo de hardware Bluetooth",
        "REJECTED_BY_HANDLER": "el emparejamiento fue rechazado",
        "REMOTE_DEVICE_HAS_ASSOCIATION": "el mando ya está asociado a otro dispositivo",
        "REQUIRED_HANDLER_NOT_REGISTERED": "el dispositivo solicitó una confirmación o PIN que GDM todavía no puede gestionar; usa el modo de emparejamiento estándar de Windows",
    }
    verb = "Emparejamiento" if action == "pair" else "Eliminación"
    return f"{verb}: {names.get(status, status.replace('_', ' ').lower())}."


def handle_pairing_request(name: str, args: Any, emit) -> None:
    pairing_kind = args.pairing_kind.name
    if pairing_kind == "CONFIRM_ONLY":
        args.accept()
    elif pairing_kind == "DISPLAY_PIN":
        emit(("status", f"{name} requiere que ingreses el PIN {args.pin} en el mando o dispositivo."))
        args.accept()
    else:
        emit(("error", f"{name} solicitó un tipo de PIN no compatible ({pairing_kind})."))


async def run_custom_pairing(custom_pairing: Any, supported_kinds: Any, callback) -> Any:
    token = custom_pairing.add_pairing_requested(callback)
    try:
        return await custom_pairing.pair_async(supported_kinds)
    finally:
        custom_pairing.remove_pairing_requested(token)


def should_auto_pair(
    enabled: bool,
    paired: bool,
    is_gamepad: bool,
    device_id: str,
    attempted: set[str],
    connected: bool = False,
    can_pair: bool = True,
) -> bool:
    if (
        not enabled
        or paired
        or connected
        or not can_pair
        or not is_gamepad
        or device_id in attempted
    ):
        return False
    attempted.add(device_id)
    return True


async def bluetooth_adapter_enabled(adapter_type: Any, radio_on_state: Any) -> bool | None:
    adapter = await adapter_type.get_default_async()
    if adapter is None:
        return None
    radio = await adapter.get_radio_async()
    if radio is None:
        return None
    if radio.state.name == "UNKNOWN":
        raise RuntimeError("Windows no pudo determinar si la radio Bluetooth está encendida.")
    return radio.state == radio_on_state


def disconnect_bluetooth_address(address: int) -> None:
    if platform.system() != "Windows":
        raise OSError("La desconexión Bluetooth directa solo está disponible en Windows.")

    from ctypes import wintypes

    class BluetoothFindRadioParams(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD)]

    bthprops = ctypes.WinDLL("bthprops.cpl", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    bthprops.BluetoothFindFirstRadio.argtypes = [
        ctypes.POINTER(BluetoothFindRadioParams),
        ctypes.POINTER(wintypes.HANDLE),
    ]
    bthprops.BluetoothFindFirstRadio.restype = wintypes.HANDLE
    bthprops.BluetoothFindNextRadio.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(wintypes.HANDLE),
    ]
    bthprops.BluetoothFindNextRadio.restype = wintypes.BOOL
    bthprops.BluetoothFindRadioClose.argtypes = [wintypes.HANDLE]
    bthprops.BluetoothFindRadioClose.restype = wintypes.BOOL
    kernel32.DeviceIoControl.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.c_void_p,
    ]
    kernel32.DeviceIoControl.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    params = BluetoothFindRadioParams(ctypes.sizeof(BluetoothFindRadioParams))
    radio = wintypes.HANDLE()
    search = bthprops.BluetoothFindFirstRadio(ctypes.byref(params), ctypes.byref(radio))
    if not search:
        raise ctypes.WinError(ctypes.get_last_error())

    disconnect_ioctl = 0x41000C
    disconnect_address = ctypes.c_uint64(address)
    last_disconnect_error = 0
    disconnected = False
    try:
        while radio:
            bytes_returned = wintypes.DWORD()
            if kernel32.DeviceIoControl(
                radio,
                disconnect_ioctl,
                ctypes.byref(disconnect_address),
                ctypes.sizeof(disconnect_address),
                None,
                0,
                ctypes.byref(bytes_returned),
                None,
            ):
                disconnected = True
                break

            last_disconnect_error = ctypes.get_last_error()
            kernel32.CloseHandle(radio)
            radio = wintypes.HANDLE()
            if not bthprops.BluetoothFindNextRadio(search, ctypes.byref(radio)):
                break
    finally:
        if radio:
            kernel32.CloseHandle(radio)
        bthprops.BluetoothFindRadioClose(search)

    if not disconnected:
        if last_disconnect_error:
            raise ctypes.WinError(last_disconnect_error)
        raise OSError("Windows no pudo desconectar el mando desde ningún adaptador Bluetooth.")


def is_bluetooth_device_already_disconnected(error: OSError) -> bool:
    return getattr(error, "winerror", None) == 1167


def bluetooth_disconnect_success_message(display_name: str) -> str:
    return (
        f"Windows aceptó la solicitud para desconectar {display_name} de "
        "Bluetooth. El emparejamiento se conserva, pero el mando puede seguir "
        "encendido: esta acción no lo apaga físicamente."
    )


def bluetooth_disconnect_candidates(
    device_id: str,
    selected_record: dict[str, Any],
    published_devices: dict[str, dict[str, Any]],
    device_transports: dict[str, str],
    known_device_ids: set[str],
) -> list[tuple[str, dict[str, Any]]]:
    container_id = str(selected_record.get("container_id", "")).strip().casefold()
    if not container_id:
        return [(device_id, selected_record)]

    def hardware_ids(record: dict[str, Any]) -> set[tuple[str, str]]:
        values = (
            record.get("hardware_ids", []),
            record.get("device_instance_id", ""),
        )
        return set(
            re.findall(
                r"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})",
                " ".join(
                    str(value)
                    for group in values
                    for value in (
                        group if isinstance(group, (list, tuple, set)) else [group]
                    )
                ).casefold(),
            )
        )

    def normalized_name(record: dict[str, Any]) -> str:
        name = str(record.get("name", "")).casefold()
        return re.sub(r"[^a-z0-9]", "", name)

    selected_hardware_ids = hardware_ids(selected_record)
    selected_name = normalized_name(selected_record)
    candidates = []
    for candidate_id, record in published_devices.items():
        if (
            candidate_id not in known_device_ids
            or device_transports.get(candidate_id) != "Bluetooth clásico"
            or str(record.get("container_id", "")).strip().casefold()
            != container_id
        ):
            continue
        candidate_hardware_ids = hardware_ids(record)
        same_hardware = bool(
            selected_hardware_ids
            and candidate_hardware_ids
            and selected_hardware_ids.intersection(candidate_hardware_ids)
        )
        same_name = (
            not selected_hardware_ids
            or not candidate_hardware_ids
        ) and bool(selected_name) and normalized_name(record) == selected_name
        if candidate_id == device_id or same_hardware or same_name:
            candidates.append((candidate_id, record))

    if not candidates:
        return [(device_id, selected_record)]
    candidates.sort(
        key=lambda candidate: (
            candidate[0] != device_id,
            not candidate[1].get("connected", False),
            not candidate[1].get("paired", False),
        )
    )
    return candidates


class BluetoothService:
    def __init__(self, auto_pair: bool = False) -> None:
        self.events: queue.Queue[tuple[str, Any]] = queue.Queue()
        self.commands: queue.Queue[tuple[str, ...]] = queue.Queue()
        self._stop = threading.Event()
        self._auto_pair = threading.Event()
        if auto_pair:
            self._auto_pair.set()
        self._thread = threading.Thread(target=self._run, name="GDMBluetooth", daemon=True)
        self._thread.start()

    def _log(self, level: str, message: str) -> None:
        self.events.put(("log", (level, message)))

    def set_auto_pair(self, enabled: bool) -> None:
        if enabled:
            self._auto_pair.set()
        else:
            self._auto_pair.clear()

    def pair(self, device_id: str) -> None:
        self.commands.put(("pair", device_id))

    def unpair(self, device_id: str, retry_auto_pair: bool = False) -> None:
        action = "unpair_retry" if retry_auto_pair else "unpair"
        self.commands.put((action, device_id))

    def disconnect(
        self,
        device_id: str,
        display_name: str | None = None,
    ) -> None:
        if display_name is None:
            self.commands.put(("disconnect", device_id))
        else:
            self.commands.put(("disconnect", device_id, display_name))

    def refresh(self, retry_auto_pair: bool = True) -> None:
        action = "refresh" if retry_auto_pair else "refresh_scan"
        self.commands.put((action, ""))

    def close(self) -> None:
        self._stop.set()
        self.commands.put(("stop", ""))
        if threading.current_thread() is not self._thread:
            self._thread.join(timeout=2)

    def _run(self) -> None:
        if platform.system() != "Windows":
            self.events.put(("status", "La administración Bluetooth está disponible en Windows."))
            return

        try:
            from winrt.windows.devices.bluetooth import BluetoothAdapter, BluetoothDevice, BluetoothLEDevice
            from winrt.windows.devices.enumeration import (
                DeviceInformation,
                DevicePairingKinds,
            )
            from winrt.windows.devices.radios import RadioState
        except ImportError as error:
            self.events.put(("error", f"No se pudieron cargar las API Bluetooth de Windows: {error}"))
            return

        known_devices: dict[str, Any] = {}
        published_devices: dict[str, dict[str, Any]] = {}
        device_transports: dict[str, str] = {}
        gamepad_device_ids: set[str] = set()
        attempted_auto_pair: set[str] = set()
        auto_pair_retry_at: dict[str, float] = {}
        watchers: list[Any] = []
        connection_states: dict[str, bool] = {}
        radio_enabled: bool | None = None
        radio_state_initialized = False
        next_radio_check = 0.0
        last_radio_error: str | None = None

        def stop_watchers() -> None:
            nonlocal watchers
            for watcher in watchers:
                try:
                    watcher.stop()
                except Exception as error:
                    self.events.put(("error", f"No se pudo detener un buscador Bluetooth: {error}"))
            watchers = []

        def clear_discovered_devices() -> None:
            for device_id in tuple(known_devices):
                self.events.put(("removed", device_id))
            known_devices.clear()
            published_devices.clear()
            device_transports.clear()
            gamepad_device_ids.clear()
            connection_states.clear()
            attempted_auto_pair.clear()
            auto_pair_retry_at.clear()

        def start_watchers() -> None:
            stop_watchers()
            connection_states.clear()

            selectors = (
                ("Bluetooth clásico", BluetoothDevice.get_device_selector_from_pairing_state(False)),
                ("Bluetooth clásico", BluetoothDevice.get_device_selector_from_pairing_state(True)),
                ("Bluetooth LE", BluetoothLEDevice.get_device_selector_from_pairing_state(False)),
                ("Bluetooth LE", BluetoothLEDevice.get_device_selector_from_pairing_state(True)),
            )
            for transport, selector in selectors:
                try:
                    properties = [
                        "System.Devices.DeviceInstanceId",
                        "System.Devices.HardwareIds",
                        "System.Devices.ContainerId",
                        "System.Devices.Aep.IsConnected",
                    ]
                    watcher = DeviceInformation.create_watcher_aqs_filter_and_additional_properties(selector, properties)

                    def on_added(sender, device, transport=transport, updated_properties=None) -> None:
                        device_id = device.id
                        name = device.name or "Dispositivo Bluetooth"
                        paired = bool(device.pairing.is_paired)
                        device_properties = dict(device.properties)
                        if updated_properties:
                            device_properties.update(updated_properties)
                        previous_record = published_devices.get(device_id)
                        connected_property = "System.Devices.Aep.IsConnected"
                        if updated_properties is not None and connected_property in updated_properties:
                            connected = property_is_true(updated_properties[connected_property])
                            connection_states[device_id] = connected
                        elif connected_property in device_properties:
                            connected = property_is_true(device_properties[connected_property])
                            connection_states[device_id] = connected
                        elif device_id in connection_states:
                            connected = connection_states[device_id]
                        else:
                            connected = (
                                previous_record.get("connected", False)
                                if previous_record
                                else False
                            )
                        is_gamepad, match_reason = gamepad_match(name, device_properties)
                        display_name = bluetooth_gamepad_display_name(
                            name,
                            device_properties,
                        )
                        hardware_ids = device_properties.get("System.Devices.HardwareIds", [])
                        if isinstance(hardware_ids, str):
                            hardware_ids = [hardware_ids]
                        elif hardware_ids is None:
                            hardware_ids = []
                        else:
                            try:
                                hardware_ids = [str(value) for value in hardware_ids]
                            except TypeError:
                                hardware_ids = [str(hardware_ids)]
                        record = {
                            "id": device_id,
                            "name": name,
                            "display_name": display_name,
                            "paired": paired,
                            "connected": connected,
                            "can_pair": bool(device.pairing.can_pair),
                            "gamepad": is_gamepad,
                            "match_reason": match_reason,
                            "hardware_ids": hardware_ids,
                            "device_instance_id": str(device_properties.get("System.Devices.DeviceInstanceId", "")),
                            "container_id": str(device_properties.get("System.Devices.ContainerId", "")),
                            "transport": transport,
                        }
                        known_devices[device_id] = device
                        device_transports[device_id] = transport
                        published_devices[device_id] = record
                        if is_gamepad:
                            gamepad_device_ids.add(device_id)
                        else:
                            gamepad_device_ids.discard(device_id)
                        changed = previous_record != record
                        if changed:
                            self.events.put(("device", record))
                        if is_gamepad and changed:
                            self._log(
                                "SUCCESS" if paired else "INFO",
                                f"Mando detectado: {display_name} "
                                f"({'emparejado' if paired else 'no emparejado'}).",
                            )
                        if should_auto_pair(
                            self._auto_pair.is_set(),
                            paired,
                            record["gamepad"],
                            device_id,
                            attempted_auto_pair,
                            connected=connected,
                            can_pair=record["can_pair"],
                        ):
                            self._log("INFO", f"Emparejamiento automático solicitado para {display_name} (CanPair={record['can_pair']}).")
                            self.events.put(("status", f"Emparejando automáticamente: {display_name}…"))
                            self.commands.put(("pair", device_id))

                    def on_removed(sender, update) -> None:
                        device_id = update.id
                        known_devices.pop(device_id, None)
                        published_devices.pop(device_id, None)
                        device_transports.pop(device_id, None)
                        gamepad_device_ids.discard(device_id)
                        connection_states.pop(device_id, None)
                        attempted_auto_pair.discard(device_id)
                        auto_pair_retry_at.pop(device_id, None)
                        self.events.put(("removed", device_id))

                    def on_updated(sender, update) -> None:
                        device = known_devices.get(update.id)
                        if device is not None:
                            on_added(sender, device, transport, dict(update.properties))

                    def on_completed(sender, args) -> None:
                        if radio_enabled:
                            self.events.put(("status", "Búsqueda Bluetooth activa."))

                    watcher.add_added(on_added)
                    watcher.add_removed(on_removed)
                    watcher.add_updated(on_updated)
                    watcher.add_enumeration_completed(on_completed)
                    watcher.start()
                    watchers.append(watcher)
                    self._log("SUCCESS", f"Buscador iniciado: {transport}.")
                except Exception as error:
                    self._log("ERROR", f"Error al iniciar buscador {transport}: {error}\n{traceback.format_exc()}")
                    self.events.put(("error", f"No se pudo iniciar búsqueda ({transport}): {error}"))

            if not watchers:
                self.events.put(("status", "No se pudo iniciar la búsqueda Bluetooth."))

        async def refresh_connection_states() -> None:
            for device_id in tuple(gamepad_device_ids):
                record = published_devices.get(device_id)
                if record is None:
                    continue
                connection = None
                try:
                    if device_transports.get(device_id) == "Bluetooth clásico":
                        connection = await BluetoothDevice.from_id_async(device_id)
                    else:
                        connection = await BluetoothLEDevice.from_id_async(device_id)
                    if connection is None:
                        raise RuntimeError("Windows no devolvió el estado de conexión Bluetooth.")
                    status = connection.connection_status
                    connected = getattr(status, "name", str(status)).casefold() == "connected"
                    connection_states[device_id] = connected
                    if record["connected"] != connected:
                        updated_record = dict(record)
                        updated_record["connected"] = connected
                        published_devices[device_id] = updated_record
                        self.events.put(("device", updated_record))
                        self._log(
                            "SUCCESS" if connected else "INFO",
                            f"{record.get('display_name', record['name'])}: "
                            f"{'conectado' if connected else 'desconectado'}.",
                        )
                except Exception as error:
                    self._log(
                        "ERROR",
                        f"No se pudo actualizar el estado de conexión de "
                        f"{record.get('display_name', record['name'])}: {error}",
                    )
                finally:
                    if connection is not None:
                        try:
                            connection.close()
                        except Exception as error:
                            self._log(
                                "ERROR",
                                f"No se pudo liberar la consulta Bluetooth de "
                                f"{record.get('display_name', record['name'])}: {error}",
                            )

        def update_radio_state() -> None:
            nonlocal radio_enabled, radio_state_initialized, next_radio_check, last_radio_error
            next_radio_check = time.monotonic() + 2
            try:
                enabled = asyncio.run(bluetooth_adapter_enabled(BluetoothAdapter, RadioState.ON))
            except Exception as error:
                next_radio_check = time.monotonic() + 10
                message = f"No se pudo consultar el estado de Bluetooth: {error}"
                if message != last_radio_error:
                    self._log("ERROR", message)
                    self.events.put(("error", message))
                    self.events.put(("radio", "unknown"))
                    last_radio_error = message
                return
            recovered_from_error = last_radio_error is not None
            last_radio_error = None

            if radio_state_initialized and enabled == radio_enabled:
                if recovered_from_error:
                    radio_state = "on" if enabled else "off" if enabled is False else "unavailable"
                    self.events.put(("radio", radio_state))
                    if enabled:
                        status = "Búsqueda Bluetooth activa." if watchers else "Bluetooth está activado."
                        self.events.put(("status", status))
                    elif enabled is False:
                        self.events.put(("status", "Bluetooth está desactivado. Actívalo en Windows para buscar dispositivos."))
                    else:
                        self.events.put(("status", "No se detectó un adaptador Bluetooth disponible en este equipo."))
                return
            radio_state_initialized = True
            radio_enabled = enabled
            radio_state = "on" if enabled else "off" if enabled is False else "unavailable"
            self.events.put(("radio", radio_state))
            if enabled is False:
                stop_watchers()
                clear_discovered_devices()
                self.events.put(("status", "Bluetooth está desactivado. Actívalo en Windows para buscar dispositivos."))
                self._log("WARN", "El adaptador Bluetooth está desactivado.")
            elif enabled is None:
                stop_watchers()
                clear_discovered_devices()
                self.events.put(("status", "No se detectó un adaptador Bluetooth disponible en este equipo."))
                self._log("WARN", "No se detectó un adaptador Bluetooth disponible.")
            else:
                self.events.put(("status", "Buscando dispositivos Bluetooth…"))
                self._log("INFO", "Iniciando búsqueda de dispositivos Bluetooth clásico y Bluetooth LE.")
                start_watchers()

        async def perform_pairing(
            action: str,
            device_id: str,
            retry_auto_pair_after_unpair: bool = False,
        ) -> None:
            device = known_devices.get(device_id)
            if device is None:
                self.events.put(("error", "El dispositivo ya no está disponible. Actualiza la búsqueda."))
                return
            name = device.name or "Dispositivo Bluetooth"
            try:
                self.events.put(("status", f"{'Emparejando' if action == 'pair' else 'Eliminando'}: {name}…"))
                if action == "pair":
                    custom_pairing = device.pairing.custom
                    self._log("INFO", f"Ejecutando DeviceInformationPairing.custom.pair_async para {name}.")

                    def on_pairing_requested(sender, args) -> None:
                        pairing_kind = args.pairing_kind.name
                        self._log("INFO", f"Windows solicitó el método de emparejamiento {pairing_kind} para {name}.")
                        handle_pairing_request(name, args, self.events.put)

                    supported_kinds = DevicePairingKinds.CONFIRM_ONLY | DevicePairingKinds.DISPLAY_PIN
                    result = await run_custom_pairing(
                        custom_pairing,
                        supported_kinds,
                        on_pairing_requested,
                    )
                    outcome = result.status.name
                else:
                    self._log("INFO", f"Ejecutando DeviceInformationPairing.unpair_async para {name}.")
                    result = await device.pairing.unpair_async()
                    outcome = result.status.name
                self.events.put(("operation", (device_id, action, outcome)))
                self.events.put(("status", f"{name}: {pairing_status_text(outcome, action)}"))
                status_level = "SUCCESS" if outcome in {"PAIRED", "ALREADY_PAIRED", "UNPAIRED", "ALREADY_UNPAIRED"} else "ERROR"
                self._log(status_level, f"{name}: {pairing_status_text(outcome, action)} (código Windows: {outcome}).")
                if action == "pair":
                    if outcome in {"PAIRED", "ALREADY_PAIRED"}:
                        auto_pair_retry_at.pop(device_id, None)
                    elif outcome == "NOT_READY_TO_PAIR" and self._auto_pair.is_set():
                        auto_pair_retry_at[device_id] = time.monotonic() + 5
                        self._log("INFO", f"Se reintentará emparejar {name} automáticamente mientras siga detectado.")
                elif (
                    action == "unpair"
                    and retry_auto_pair_after_unpair
                    and outcome in {"UNPAIRED", "ALREADY_UNPAIRED"}
                ):
                    attempted_auto_pair.add(device_id)
                    if self._auto_pair.is_set():
                        auto_pair_retry_at[device_id] = time.monotonic() + 5
            except Exception as error:
                details = traceback.format_exc()
                self._log("ERROR", f"Excepción durante {action} de {name}: {error}\n{details}")
                self.events.put(("error", f"No se pudo completar la operación con {name}: {error}"))

        def retry_auto_pair_if_due() -> None:
            if not self._auto_pair.is_set():
                auto_pair_retry_at.clear()
                return

            now = time.monotonic()
            for device_id, retry_at in list(auto_pair_retry_at.items()):
                if now < retry_at:
                    continue
                device = known_devices.get(device_id)
                if device is None:
                    auto_pair_retry_at.pop(device_id, None)
                    attempted_auto_pair.discard(device_id)
                    continue
                record = published_devices.get(device_id, {})
                if (
                    device.pairing.is_paired
                    or record.get("connected")
                    or not device.pairing.can_pair
                ):
                    auto_pair_retry_at.pop(device_id, None)
                    attempted_auto_pair.add(device_id)
                    continue

                attempted_auto_pair.discard(device_id)
                if should_auto_pair(
                    True,
                    False,
                    device_id in gamepad_device_ids,
                    device_id,
                    attempted_auto_pair,
                    connected=record.get("connected", False),
                    can_pair=bool(device.pairing.can_pair),
                ):
                    auto_pair_retry_at[device_id] = now + 5
                    display_name = record.get(
                        "display_name",
                        device.name or "mando Bluetooth",
                    )
                    self._log("INFO", f"Reintentando emparejar automáticamente {display_name}.")
                    self.events.put(("status", f"Reintentando emparejamiento: {display_name}…"))
                    self.commands.put(("pair", device_id))

        async def perform_disconnect(
            device_id: str,
            requested_display_name: str = "",
        ) -> None:
            info = known_devices.get(device_id)
            if info is None:
                self.events.put(("error", "El mando Bluetooth ya no está disponible en la búsqueda."))
                return
            selected_record = published_devices.get(device_id, {})
            candidates = bluetooth_disconnect_candidates(
                device_id,
                selected_record,
                published_devices,
                device_transports,
                set(known_devices),
            )

            display_name = (
                requested_display_name
                or selected_record.get("display_name")
                or info.name
                or "Mando Bluetooth"
            )
            failures = []
            for candidate_id, record in candidates:
                candidate = known_devices.get(candidate_id)
                if candidate is None:
                    continue
                transport = device_transports.get(candidate_id)
                connection = None
                try:
                    connection = await open_bluetooth_connection(
                        candidate_id,
                        transport,
                        BluetoothDevice,
                        BluetoothLEDevice,
                    )
                    if connection is None:
                        raise RuntimeError(
                            "Windows no devolvió una conexión Bluetooth para el mando"
                        )
                    address = int(connection.bluetooth_address)
                    connection.close()
                    connection = None
                    if not address:
                        raise RuntimeError(
                            "Windows devolvió una dirección Bluetooth vacía"
                        )
                    await asyncio.to_thread(
                        disconnect_bluetooth_address,
                        address,
                    )
                    message = bluetooth_disconnect_success_message(display_name)
                    self.events.put(("status", message))
                    self._log("SUCCESS", message)
                    return
                except OSError as error:
                    failures.append(error)
                    if is_bluetooth_device_already_disconnected(error):
                        self._log(
                            "INFO",
                            f"Windows no encontró una conexión activa para el "
                            f"registro {record.get('name', display_name)}; "
                            "probando otro registro del mismo mando.",
                        )
                    else:
                        self._log(
                            "WARN",
                            f"No se pudo desconectar el registro "
                            f"{record.get('name', display_name)}: {error}",
                        )
                except Exception as error:
                    failures.append(error)
                    self._log(
                        "WARN",
                        f"No se pudo desconectar el registro "
                        f"{record.get('name', display_name)}: {error}",
                    )
                finally:
                    if connection is not None:
                        try:
                            connection.close()
                        except Exception as error:
                            self._log(
                                "ERROR",
                                f"No se pudo liberar la conexión local de "
                                f"{display_name}: {error}",
                            )

            detail = str(failures[-1]) if failures else (
                "Windows no proporcionó un registro Bluetooth clásico conectado"
            )
            if failures and all(
                isinstance(error, OSError)
                and is_bluetooth_device_already_disconnected(error)
                for error in failures
            ):
                message = (
                    f"Windows no encontró una conexión Bluetooth activa para "
                    f"{display_name} (WinError 1167). El emparejamiento se "
                    "conserva. Esta acción desconecta Bluetooth; no apaga "
                    "físicamente el mando."
                )
            else:
                message = (
                    f"No se pudo desconectar {display_name} en Windows: {detail}. "
                    "El emparejamiento se conserva."
                )
            self.events.put(("error", message))
            self._log("ERROR", message)

        update_radio_state()
        try:
            while not self._stop.is_set():
                if time.monotonic() >= next_radio_check:
                    update_radio_state()
                try:
                    command = self.commands.get(timeout=0.2)
                except queue.Empty:
                    retry_auto_pair_if_due()
                    continue
                action, device_id, *arguments = command
                if action == "stop":
                    break
                if action in ("refresh", "refresh_scan"):
                    if action == "refresh" and self._auto_pair.is_set():
                        attempted_auto_pair.clear()
                        auto_pair_retry_at.clear()
                        self._log("INFO", "Se reinició la búsqueda; los mandos detectados volverán a intentar emparejarse automáticamente.")
                    self._log("INFO", "Reiniciando los buscadores Bluetooth.")
                    if radio_enabled:
                        asyncio.run(refresh_connection_states())
                        start_watchers()
                    elif radio_enabled is False:
                        self.events.put(("status", "Bluetooth está desactivado. Actívalo en Windows para buscar dispositivos."))
                    else:
                        self.events.put(("status", "No se detectó un adaptador Bluetooth disponible en este equipo."))
                elif action in ("pair", "unpair", "unpair_retry"):
                    if not radio_enabled:
                        self.events.put(("error", "No se puede emparejar: Bluetooth está desactivado o no hay adaptador disponible."))
                        continue
                    pairing_action = "unpair" if action in ("unpair", "unpair_retry") else "pair"
                    retry_after_unpair = action == "unpair_retry"
                    if pairing_action == "unpair":
                        attempted_auto_pair.add(device_id)
                    asyncio.run(perform_pairing(pairing_action, device_id, retry_after_unpair))
                elif action == "disconnect":
                    if not radio_enabled:
                        self.events.put(("error", "No se puede desconectar el mando: Bluetooth está desactivado o no hay adaptador disponible."))
                        continue
                    asyncio.run(
                        perform_disconnect(
                            device_id,
                            arguments[0] if arguments else "",
                        )
                    )
        finally:
            stop_watchers()
            self._log("INFO", "Búsqueda Bluetooth detenida.")
            self.events.put(("status", "Búsqueda Bluetooth detenida."))
