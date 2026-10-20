"""Windows game-controller battery report lookup."""

from __future__ import annotations

import platform
from typing import Optional

from dualsense_led import (
    joystick_path_for_index,
    normalize_device_path,
    vendor_product_ids_from_path,
)


DUALSENSE_BATTERY_PRODUCT_IDS = {0x0CE6, 0x0DF2}


def parse_dualsense_battery_report(report: bytes | list[int]) -> Optional[int]:
    data = bytes(report)
    if not data:
        return None

    if data[0] == 0x31:
        battery_index = 54
    elif data[0] == 0x01:
        battery_index = 53
    else:
        return None
    if len(data) <= battery_index:
        return None

    battery_state = data[battery_index]
    if battery_state & 0x20:
        return 100
    level = battery_state & 0x0F
    if level > 10:
        return None
    return level * 10


def dualsense_hid_battery_percent(
    device_index: int,
    vendor_id: int,
    product_id: int,
) -> Optional[int]:
    percent, _status = dualsense_hid_battery_reading(
        device_index,
        vendor_id,
        product_id,
    )
    return percent


def dualsense_hid_battery_reading(
    device_index: int,
    vendor_id: int,
    product_id: int,
) -> tuple[Optional[int], str]:
    if platform.system() != "Windows":
        return None, "not_a_supported_windows_dualsense"

    joystick_path = joystick_path_for_index(device_index)
    if not joystick_path:
        return None, "sdl_did_not_provide_hid_path"
    path_ids = vendor_product_ids_from_path(joystick_path)
    if path_ids is None:
        return None, "sdl_hid_path_has_no_vid_pid"
    path_vendor_id, path_product_id = path_ids
    if (
        (vendor_id and vendor_id != path_vendor_id)
        or (product_id and product_id != path_product_id)
    ):
        return None, "sdl_vid_pid_does_not_match_hid_path"
    vendor_id = vendor_id or path_vendor_id
    product_id = product_id or path_product_id
    if vendor_id != 0x054C:
        return None, "not_a_supported_windows_dualsense"
    if product_id not in DUALSENSE_BATTERY_PRODUCT_IDS:
        return None, "unsupported_dualsense_product"

    import hid

    normalized_path = normalize_device_path(joystick_path)
    matches = [
        item for item in hid.enumerate(vendor_id, product_id)
        if item.get("vendor_id") == vendor_id
        and item.get("product_id") == product_id
        and item.get("path") is not None
        and normalize_device_path(item["path"]) == normalized_path
    ]
    if not matches:
        return None, "no_hid_interface_matches_sdl_path"
    if len(matches) > 1:
        return None, "multiple_hid_interfaces_match_sdl_path"

    device = hid.device()
    try:
        device.open_path(matches[0]["path"])
        report = device.read(128, timeout_ms=750)
        if not report:
            return None, "hid_input_read_timed_out"
        percent = parse_dualsense_battery_report(report)
        if percent is None:
            return None, f"unsupported_input_report_id_{report[0]:02X}_length_{len(report)}"
        return percent, "ok"
    finally:
        device.close()


def gamepad_battery_percent(vendor_id: int, product_id: int) -> Optional[int]:
    percent, _status = gamepad_battery_reading(vendor_id, product_id)
    return percent


def gamepad_battery_reading(vendor_id: int, product_id: int) -> tuple[Optional[int], str]:
    if platform.system() != "Windows" or not vendor_id or not product_id:
        return None, "not_a_supported_windows_controller"

    from winrt.windows.gaming.input import RawGameController

    matches = [
        controller
        for controller in RawGameController.raw_game_controllers
        if controller.hardware_vendor_id == vendor_id
        and controller.hardware_product_id == product_id
    ]
    if not matches:
        return None, "raw_game_controller_not_found"
    if len(matches) > 1:
        return None, "multiple_raw_game_controllers_match_vid_pid"

    report = matches[0].try_get_battery_report()
    if report is None:
        return None, "raw_game_controller_has_no_battery_report"

    remaining = report.remaining_capacity_in_milliwatt_hours
    full = report.full_charge_capacity_in_milliwatt_hours
    if remaining is None or full is None or full <= 0:
        return None, "battery_report_has_no_capacity"

    return max(0, min(100, round(remaining * 100 / full))), "ok"
