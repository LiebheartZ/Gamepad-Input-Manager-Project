import sys
import types
import unittest
from unittest.mock import patch

from battery_manager import (
    dualsense_hid_battery_percent,
    dualsense_hid_battery_reading,
    gamepad_battery_percent,
    gamepad_battery_reading,
    parse_dualsense_battery_report,
)


class BatteryManagerTests(unittest.TestCase):
    def read_battery(self, controllers, vendor_id=0x054C, product_id=0x0CE6):
        module = types.ModuleType("winrt.windows.gaming.input")
        module.RawGameController = types.SimpleNamespace(raw_game_controllers=controllers)
        with (
            patch("battery_manager.platform.system", return_value="Windows"),
            patch.dict(sys.modules, {"winrt.windows.gaming.input": module}),
        ):
            return gamepad_battery_percent(vendor_id, product_id)

    def test_returns_percent_for_unique_matching_controller(self):
        matching = types.SimpleNamespace(
            hardware_vendor_id=0x054C,
            hardware_product_id=0x0CE6,
            try_get_battery_report=lambda: types.SimpleNamespace(
                remaining_capacity_in_milliwatt_hours=4500,
                full_charge_capacity_in_milliwatt_hours=5000,
            ),
        )
        other = types.SimpleNamespace(
            hardware_vendor_id=0x045E,
            hardware_product_id=0x0B13,
        )
        self.assertEqual(self.read_battery([other, matching]), 90)

    def test_does_not_guess_when_matching_controller_is_ambiguous(self):
        controller = types.SimpleNamespace(
            hardware_vendor_id=0x054C,
            hardware_product_id=0x0CE6,
        )
        self.assertIsNone(self.read_battery([controller, controller]))

    def test_missing_capacity_is_not_reported_as_empty(self):
        matching = types.SimpleNamespace(
            hardware_vendor_id=0x054C,
            hardware_product_id=0x0CE6,
            try_get_battery_report=lambda: types.SimpleNamespace(
                remaining_capacity_in_milliwatt_hours=None,
                full_charge_capacity_in_milliwatt_hours=5000,
            ),
        )
        self.assertIsNone(self.read_battery([matching]))

    def test_non_windows_does_not_import_winrt(self):
        with patch("battery_manager.platform.system", return_value="Linux"):
            self.assertIsNone(gamepad_battery_percent(0x054C, 0x0CE6))

    def test_parses_bluetooth_dualsense_battery_in_ten_percent_steps(self):
        report = bytearray(78)
        report[0] = 0x31
        report[54] = 0x07
        self.assertEqual(parse_dualsense_battery_report(report), 70)

    def test_parses_usb_dualsense_full_charge_flag(self):
        report = bytearray(64)
        report[0] = 0x01
        report[53] = 0x20
        self.assertEqual(parse_dualsense_battery_report(report), 100)

    def test_ignores_unknown_or_truncated_reports(self):
        self.assertIsNone(parse_dualsense_battery_report(b"\x03" + bytes(63)))
        self.assertIsNone(parse_dualsense_battery_report(b"\x31" + bytes(10)))

    def test_reads_only_hid_path_matching_selected_sdl_device(self):
        controller_path = b"\\\\?\\HID#VID_054C&PID_0CE6#BTHENUM#A"
        report = bytearray(78)
        report[0] = 0x31
        report[54] = 0x06
        device = types.SimpleNamespace(
            open_path=lambda path: self.assertEqual(path, controller_path),
            read=lambda _size, timeout_ms: (self.assertEqual(timeout_ms, 750) or list(report)),
            close=lambda: None,
        )
        hid_module = types.SimpleNamespace(
            enumerate=lambda _vendor, _product: [{
                "vendor_id": 0x054C,
                "product_id": 0x0CE6,
                "path": controller_path,
            }],
            device=lambda: device,
        )
        with (
            patch("battery_manager.platform.system", return_value="Windows"),
            patch("battery_manager.joystick_path_for_index", return_value=controller_path),
            patch.dict(sys.modules, {"hid": hid_module}),
        ):
            self.assertEqual(dualsense_hid_battery_percent(0, 0x054C, 0x0CE6), 60)

    def test_reads_dualsense_battery_when_sdl_vendor_and_product_ids_are_zero(self):
        controller_path = b"\\\\?\\HID#VID_054C&PID_0CE6#BTHENUM#A"
        report = bytearray(78)
        report[0] = 0x31
        report[54] = 0x08
        device = types.SimpleNamespace(
            open_path=lambda path: self.assertEqual(path, controller_path),
            read=lambda _size, timeout_ms: (self.assertEqual(timeout_ms, 750) or list(report)),
            close=lambda: None,
        )
        hid_module = types.SimpleNamespace(
            enumerate=lambda vendor_id, product_id: (
                self.assertEqual((vendor_id, product_id), (0x054C, 0x0CE6))
                or [{
                    "vendor_id": 0x054C,
                    "product_id": 0x0CE6,
                    "path": controller_path,
                }]
            ),
            device=lambda: device,
        )
        with (
            patch("battery_manager.platform.system", return_value="Windows"),
            patch("battery_manager.joystick_path_for_index", return_value=controller_path),
            patch.dict(sys.modules, {"hid": hid_module}),
        ):
            self.assertEqual(dualsense_hid_battery_percent(0, 0, 0), 80)

    def test_hid_reading_explains_when_no_interface_matches_sdl_path(self):
        with (
            patch("battery_manager.platform.system", return_value="Windows"),
            patch(
                "battery_manager.joystick_path_for_index",
                return_value=b"HID#VID_054C&PID_0CE6#SDL-path",
            ),
            patch.dict(sys.modules, {
                "hid": types.SimpleNamespace(enumerate=lambda *_args: [], device=lambda: None)
            }),
        ):
            self.assertEqual(
                dualsense_hid_battery_reading(0, 0x054C, 0x0CE6),
                (None, "no_hid_interface_matches_sdl_path"),
            )

    def test_windows_reading_explains_when_raw_controller_is_missing(self):
        module = types.ModuleType("winrt.windows.gaming.input")
        module.RawGameController = types.SimpleNamespace(raw_game_controllers=[])
        with (
            patch("battery_manager.platform.system", return_value="Windows"),
            patch.dict(sys.modules, {"winrt.windows.gaming.input": module}),
        ):
            self.assertEqual(
                gamepad_battery_reading(0x054C, 0x0CE6),
                (None, "raw_game_controller_not_found"),
            )


if __name__ == "__main__":
    unittest.main()
