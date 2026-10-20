import unittest
import ctypes
from unittest.mock import Mock, patch

from bluetooth_manager import (
    BluetoothService,
    bluetooth_disconnect_candidates,
    bluetooth_disconnect_success_message,
    bluetooth_gamepad_display_name,
    disconnect_bluetooth_address,
    gamepad_match,
    is_bluetooth_device_already_disconnected,
    should_auto_pair,
)


class BluetoothManagerTests(unittest.TestCase):
    def test_generic_gamepad_name_is_recognized_and_auto_paired(self):
        is_gamepad, reason = gamepad_match("gamepad", {})
        attempted = set()

        self.assertTrue(is_gamepad)
        self.assertEqual(reason, "nombre")
        self.assertTrue(
            should_auto_pair(True, False, is_gamepad, "device-1", attempted)
        )

    def test_generic_gamepad_name_accepts_spaces_and_punctuation(self):
        for name in ("Game Pad", "GAME-PAD", "Generic Game Controller"):
            with self.subTest(name=name):
                self.assertTrue(gamepad_match(name, {})[0])

    def test_sony_bluetooth_names_use_model_from_hardware_id(self):
        self.assertEqual(
            bluetooth_gamepad_display_name(
                "DualSense Wireless Controller",
                {
                    "System.Devices.HardwareIds": [
                        "BTHENUM\\VID_054C&PID_05C4",
                    ],
                },
            ),
            "DualShock 4",
        )
        self.assertEqual(
            bluetooth_gamepad_display_name(
                "Wireless Controller",
                {
                    "System.Devices.DeviceInstanceId":
                        "BTHENUM\\VID_054C&PID_0CE6",
                },
            ),
            "DualSense",
        )

    def test_auto_pair_skips_connected_or_unpairable_devices(self):
        self.assertFalse(
            should_auto_pair(
                True,
                False,
                True,
                "connected-device",
                set(),
                connected=True,
            )
        )
        self.assertFalse(
            should_auto_pair(
                True,
                False,
                True,
                "unpairable-device",
                set(),
                can_pair=False,
            )
        )

    def test_windows_device_not_connected_is_treated_as_already_disconnected(self):
        already_disconnected = OSError("device is not connected")
        already_disconnected.winerror = 1167
        other_error = OSError("access denied")
        other_error.winerror = 5

        self.assertTrue(
            is_bluetooth_device_already_disconnected(already_disconnected)
        )
        self.assertFalse(
            is_bluetooth_device_already_disconnected(other_error)
        )

    def test_disconnect_success_message_does_not_claim_physical_power_off(self):
        message = bluetooth_disconnect_success_message("DualShock 4")

        self.assertIn("aceptó la solicitud", message)
        self.assertIn("puede seguir encendido", message)
        self.assertIn("no lo apaga físicamente", message)

    def test_disconnect_uses_ds4windows_radio_ioctl_with_remote_address(self):
        ioctl = 0x41000C
        remote_address = 0x123456789ABC
        radio_handle = 123

        def find_first_radio(_params, handle_out):
            ctypes.cast(
                handle_out,
                ctypes.POINTER(ctypes.c_void_p),
            )[0] = ctypes.c_void_p(radio_handle)
            return 1

        first_radio = Mock(side_effect=find_first_radio)
        device_io_control = Mock(return_value=1)
        close_handle = Mock(return_value=1)
        close_search = Mock(return_value=1)
        bthprops = Mock(
            BluetoothFindFirstRadio=first_radio,
            BluetoothFindNextRadio=Mock(return_value=0),
            BluetoothFindRadioClose=close_search,
        )
        kernel32 = Mock(
            DeviceIoControl=device_io_control,
            CloseHandle=close_handle,
        )
        with (
            patch("bluetooth_manager.platform.system", return_value="Windows"),
            patch(
                "bluetooth_manager.ctypes.WinDLL",
                side_effect=(bthprops, kernel32),
            ),
        ):
            disconnect_bluetooth_address(remote_address)

        input_address = ctypes.cast(
            device_io_control.call_args.args[2],
            ctypes.POINTER(ctypes.c_uint64),
        ).contents.value
        self.assertEqual(input_address, remote_address)
        self.assertEqual(
            device_io_control.call_args.args[1],
            ioctl,
        )
        close_handle.assert_called_once()
        close_search.assert_called_once()

    def test_disconnect_candidates_exclude_unrelated_devices_in_same_container(self):
        selected = {
            "id": "ds4",
            "name": "Wireless Controller",
            "container_id": "shared-container",
            "hardware_ids": ["BTHENUM\\VID_054C&PID_09CC"],
            "connected": False,
            "paired": True,
        }
        sibling = {
            "id": "ds4-service",
            "name": "Wireless Controller",
            "container_id": "shared-container",
            "hardware_ids": ["BTHENUM\\VID_054C&PID_09CC&MI_00"],
            "connected": False,
            "paired": True,
        }
        unrelated = {
            "id": "xbox",
            "name": "Xbox Wireless Controller",
            "container_id": "shared-container",
            "hardware_ids": ["BTHENUM\\VID_045E&PID_02FD"],
            "connected": True,
            "paired": True,
        }
        devices = {
            "ds4": selected,
            "ds4-service": sibling,
            "xbox": unrelated,
        }

        candidates = bluetooth_disconnect_candidates(
            "ds4",
            selected,
            devices,
            {
                "ds4": "Bluetooth clásico",
                "ds4-service": "Bluetooth clásico",
                "xbox": "Bluetooth clásico",
            },
            set(devices),
        )

        self.assertEqual(
            [device_id for device_id, _record in candidates],
            ["ds4", "ds4-service"],
        )

    def test_auto_pair_preference_is_set_before_bluetooth_worker_starts(self):
        auto_pair_state_at_start = []

        class InspectingThread:
            def __init__(self, target, **_kwargs):
                self.target = target

            def start(self):
                service = self.target.__self__
                auto_pair_state_at_start.append(service._auto_pair.is_set())

            def join(self, timeout=None):
                return None

        with patch("bluetooth_manager.threading.Thread", InspectingThread):
            service = BluetoothService(auto_pair=True)
            service.close()

        self.assertEqual(auto_pair_state_at_start, [True])


if __name__ == "__main__":
    unittest.main()
