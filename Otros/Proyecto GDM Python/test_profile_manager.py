import json
import queue
import threading
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import gdm_manager
from virtual_gamepad import default_profile_mapping


class RemapProfileTests(unittest.TestCase):
    def test_start_transport_detection_does_not_require_a_controller_name(self):
        joystick = SimpleNamespace(
            get_vendor_id=lambda: 0x054C,
            get_product_id=lambda: 0x09CC,
        )
        manager = SimpleNamespace(
            transport_key=None,
            transport="—",
            transport_check_key=None,
            transport_thread=None,
            transport_lock=threading.Lock(),
        )
        with patch("gdm_manager.threading.Thread") as thread:
            gdm_manager.GDMGamepadManager.start_transport_detection(
                manager,
                joystick,
            )

        self.assertEqual(manager.transport_key, (0x054C, 0x09CC))
        thread.assert_called_once()

    def test_gamepad_tester_buttons_cycle_through_available_joysticks(self):
        manager = SimpleNamespace(
            joysticks=[object(), object(), object()],
            selected=0,
            home_hold_started=100,
            home_hold_triggered=True,
            tester_rects={
                "next": gdm_manager.pygame.Rect(20, 20, 32, 32),
                "previous": gdm_manager.pygame.Rect(60, 20, 32, 32),
            },
        )
        manager.change_tester_joystick = lambda offset: (
            gdm_manager.GDMGamepadManager.change_tester_joystick(
                manager,
                offset,
            )
        )

        self.assertTrue(
            gdm_manager.GDMGamepadManager.handle_tester_click(
                manager,
                (25, 25),
            )
        )
        self.assertEqual(manager.selected, 1)
        self.assertIsNone(manager.home_hold_started)
        self.assertFalse(manager.home_hold_triggered)
        self.assertTrue(
            gdm_manager.GDMGamepadManager.handle_tester_click(
                manager,
                (65, 25),
            )
        )
        self.assertEqual(manager.selected, 0)

    def test_gamepad_tester_selection_buttons_wrap_between_first_and_last(self):
        manager = SimpleNamespace(
            joysticks=[object(), object()],
            selected=0,
            home_hold_started=None,
            home_hold_triggered=False,
        )

        self.assertTrue(
            gdm_manager.GDMGamepadManager.change_tester_joystick(
                manager,
                -1,
            )
        )
        self.assertEqual(manager.selected, 1)

    def test_virtual_joystick_does_not_mark_bluetooth_device_connected(self):
        physical_virtual_output = SimpleNamespace(
            get_init=lambda: True,
            get_name=lambda: "PS4 Controller",
            is_virtual=True,
        )
        manager = SimpleNamespace(
            joysticks=[physical_virtual_output],
            is_virtual_gamepad=lambda joystick: joystick.is_virtual,
        )

        connected = gdm_manager.GDMGamepadManager.bluetooth_device_has_live_joystick(
            manager,
            {"name": "PS4 Controller"},
        )

        self.assertFalse(connected)

    def test_bluetooth_disconnect_schedules_sdl_device_refresh(self):
        events = queue.Queue()
        events.put(("device", {"id": "ds4", "connected": False}))
        manager = SimpleNamespace(
            bluetooth=SimpleNamespace(events=events),
            bluetooth_radio_state="on",
            bluetooth_devices={"ds4": {"id": "ds4", "connected": True}},
            pending_home_action=None,
            _device_refresh_deadline=None,
        )

        with patch("gdm_manager.time.monotonic", return_value=123.0):
            gdm_manager.GDMGamepadManager.process_bluetooth_events(manager)

        self.assertAlmostEqual(manager._device_refresh_deadline, 123.3)

    def test_same_model_controllers_get_distinct_stable_device_keys(self):
        first = SimpleNamespace(
            get_init=lambda: True,
            get_instance_id=lambda: 1,
            get_guid=lambda: "shared-guid",
            get_name=lambda: "DualSense Wireless Controller",
        )
        second = SimpleNamespace(
            get_init=lambda: True,
            get_instance_id=lambda: 2,
            get_guid=lambda: "shared-guid",
            get_name=lambda: "DualSense Wireless Controller",
        )
        manager = SimpleNamespace(
            joystick_device_indices={1: 0, 2: 1},
            joysticks=[first, second],
            device_profiles={},
            is_virtual_gamepad=lambda _joystick: False,
        )
        manager.remap_device_identity = lambda joystick: (
            gdm_manager.GDMGamepadManager.remap_device_identity(manager, joystick)
        )
        path_by_index = {0: "HID#DUALSENSE#ONE", 1: "HID#DUALSENSE#TWO"}
        with patch(
            "gdm_manager.joystick_path_for_index",
            side_effect=lambda index: path_by_index[index],
        ):
            devices = gdm_manager.GDMGamepadManager.remap_devices(manager)

        self.assertNotEqual(devices[0][0], devices[1][0])
        self.assertEqual(
            [device[2] for device in devices],
            ["DualSense Wireless Controller 1", "DualSense Wireless Controller 2"],
        )
        self.assertTrue(all(device[3] for device in devices))

    def test_default_profile_is_shared_until_device_custom_profile_is_enabled(self):
        mapping = default_profile_mapping()
        controller = SimpleNamespace(
            get_name=lambda: "DualSense Wireless Controller",
            get_vendor_id=lambda: 0x054C,
        )
        manager = SimpleNamespace(
            device_profiles={},
            default_profile_output="auto",
            default_profile_mapping=mapping,
            remap_device_identity=lambda _joystick: ("hid:one", True),
            model_name=lambda *_args: "PlayStation",
        )

        self.assertEqual(
            gdm_manager.GDMGamepadManager.remap_profile_for_device(manager, controller),
            ("hid:one", False, "DualShock 4", mapping),
        )

        custom_mapping = default_profile_mapping()
        custom_mapping["a"] = "b"
        manager.device_profiles["hid:one"] = {
            "enabled": True,
            "output": "Xbox 360",
            "mapping": custom_mapping,
        }
        self.assertEqual(
            gdm_manager.GDMGamepadManager.remap_profile_for_device(manager, controller),
            ("hid:one", True, "Xbox 360", custom_mapping),
        )

    def test_custom_profile_starts_from_default_mapping(self):
        controller = SimpleNamespace(
            get_instance_id=lambda: 3,
            get_name=lambda: "DualSense Wireless Controller",
            get_vendor_id=lambda: 0x054C,
        )
        default_mapping = default_profile_mapping()
        default_mapping["a"] = "b"
        manager = SimpleNamespace(
            device_profiles={},
            default_profile_output="auto",
            default_profile_mapping=default_mapping,
            virtual_output_auto_start=False,
            playstation_led_color=None,
            disconnect_on_home_hold=True,
            home_hold_action="disconnect",
            remap_device_identity=lambda _joystick: ("hid:one", True),
            model_name=lambda *_args: "PlayStation",
            write_console=lambda *_args: None,
        )
        manager.remap_profile_for_device = lambda joystick: (
            gdm_manager.GDMGamepadManager.remap_profile_for_device(manager, joystick)
        )
        manager.remap_device_identity = lambda joystick: ("hid:one", True)
        _key, profile = gdm_manager.GDMGamepadManager.ensure_device_profile(
            manager,
            controller,
        )

        self.assertFalse(profile["enabled"])
        self.assertEqual(profile["output"], "DualShock 4")
        self.assertEqual(profile["mapping"], default_mapping)
        self.assertIsNot(profile["mapping"], default_mapping)
        self.assertFalse(profile["auto_start"])
        self.assertIsNone(profile["led_color"])
        self.assertFalse(profile["shutdown_idle_enabled"])
        self.assertEqual(profile["shutdown_idle_minutes"], 1)

    def test_saved_mapping_rejects_unknown_targets(self):
        mapping = default_profile_mapping()
        gdm_manager.GDMGamepadManager.load_mapping_values(
            mapping,
            {"a": "not-a-button", "b": None},
        )

        self.assertEqual(mapping["a"], "a")
        self.assertIsNone(mapping["b"])

    def test_remap_device_list_excludes_virtual_controller_names_and_active_output(self):
        names = (
            "ViGEm Xbox 360 Controller",
            "vJoy Device",
            "Steam Virtual Gamepad",
            "Xbox 360 Controller for Windows",
        )
        controllers = [
            SimpleNamespace(
                get_init=lambda: True,
                get_name=lambda name=name: name,
                get_vendor_id=lambda: 0,
                get_product_id=lambda: 0,
                get_instance_id=lambda instance_id=instance_id: instance_id,
            )
            for instance_id, name in enumerate(names, start=1)
        ]
        physical = SimpleNamespace(
            get_init=lambda: True,
            get_name=lambda: "DualSense Wireless Controller",
            get_vendor_id=lambda: 0x054C,
            get_product_id=lambda: 0x0CE6,
            get_instance_id=lambda: 99,
        )
        manager = SimpleNamespace(
            normalize_device_name=gdm_manager.GDMGamepadManager.normalize_device_name,
            name_indicates_virtual_gamepad=gdm_manager.GDMGamepadManager.name_indicates_virtual_gamepad,
            hidhide_type_for_gamepad=lambda *_args: "unknown",
            is_active_virtual_gamepad_output=lambda instance_id: instance_id == 4,
            joysticks=[*controllers, physical],
            joystick_device_indices={},
            device_profiles={},
        )
        manager.is_virtual_gamepad = lambda controller: (
            gdm_manager.GDMGamepadManager.is_virtual_gamepad(manager, controller)
        )
        manager.remap_device_identity = lambda controller: (
            f"session:{controller.get_instance_id()}", False
        )

        self.assertTrue(
            all(
                gdm_manager.GDMGamepadManager.is_virtual_gamepad(manager, controller)
                for controller in controllers
            )
        )
        self.assertFalse(
            gdm_manager.GDMGamepadManager.is_virtual_gamepad(manager, physical)
        )
        visible_devices = gdm_manager.GDMGamepadManager.remap_devices(manager)
        self.assertEqual([device[1] for device in visible_devices], [physical])

    def test_custom_profile_keeps_auto_start_and_led_preferences_per_device(self):
        joystick = object()
        profile = {
            "enabled": True,
            "output": "Xbox 360",
            "mapping": default_profile_mapping(),
            "auto_start": False,
            "led_color": None,
            "shutdown_enabled": True,
            "shutdown_method": "home",
            "shutdown_buttons": [],
            "shutdown_action": "disconnect",
            "shutdown_idle_enabled": True,
            "shutdown_idle_minutes": 3,
        }
        manager = SimpleNamespace(
            active_remap_joystick=lambda: joystick,
            remap_profile_for_device=lambda _joystick: (
                "hid:one",
                True,
                "Xbox 360",
                profile["mapping"],
            ),
            device_profiles={"hid:one": profile},
            virtual_output_auto_start=True,
            playstation_led_color="blue",
            disconnect_on_home_hold=False,
            home_hold_action="unpair",
            remap_device_identity=lambda _joystick: ("hid:one", True),
            active_remap_device=lambda: ("hid:one", joystick, "Controller", True),
        )

        self.assertFalse(
            gdm_manager.GDMGamepadManager.profile_auto_start_for_device(
                manager,
                joystick,
            )
        )
        self.assertIsNone(
            gdm_manager.GDMGamepadManager.profile_led_color_for_device(
                manager,
                joystick,
            )
        )
        self.assertEqual(
            gdm_manager.GDMGamepadManager.shutdown_settings_for_device(
                manager,
                joystick,
            ),
            {
                "custom": True,
                "enabled": True,
                "method": "home",
                "buttons": [],
                "action": "disconnect",
                "idle_enabled": True,
                "idle_minutes": 3,
            },
        )
        gdm_manager.GDMGamepadManager.set_active_profile_preference(
            manager,
            "auto_start",
            True,
        )
        gdm_manager.GDMGamepadManager.set_active_profile_preference(
            manager,
            "led_color",
            "purple",
        )

        self.assertTrue(profile["auto_start"])
        self.assertEqual(profile["led_color"], "purple")
        self.assertTrue(profile["shutdown_enabled"])
        self.assertEqual(profile["shutdown_method"], "home")
        self.assertEqual(profile["shutdown_action"], "disconnect")
        self.assertTrue(manager.virtual_output_auto_start)
        self.assertEqual(manager.playstation_led_color, "blue")

    def test_default_profile_preferences_remain_shared_when_custom_profile_is_disabled(self):
        joystick = object()
        manager = SimpleNamespace(
            active_remap_joystick=lambda: joystick,
            remap_profile_for_device=lambda _joystick: (
                "hid:one",
                False,
                "Xbox 360",
                default_profile_mapping(),
            ),
            device_profiles={},
            virtual_output_auto_start=False,
            playstation_led_color=None,
            disconnect_on_home_hold=True,
            home_hold_action="disconnect",
            active_remap_device=lambda: ("hid:one", joystick, "Controller", True),
        )

        gdm_manager.GDMGamepadManager.set_active_profile_preference(
            manager,
            "auto_start",
            True,
        )
        gdm_manager.GDMGamepadManager.set_active_profile_preference(
            manager,
            "led_color",
            "green",
        )

        self.assertTrue(manager.virtual_output_auto_start)
        self.assertEqual(manager.playstation_led_color, "green")
        self.assertEqual(manager.device_profiles, {})
        self.assertEqual(
            gdm_manager.GDMGamepadManager.shutdown_settings_for_device(
                manager,
                joystick,
            ),
            {
                "custom": False,
                "enabled": True,
                "method": "home",
                "buttons": [],
                "action": "disconnect",
                "idle_enabled": False,
                "idle_minutes": 1,
            },
        )

    def test_changing_custom_virtual_output_reapplies_physical_led_color(self):
        joystick = SimpleNamespace(get_instance_id=lambda: 7)

        for current_output, next_output in (
            ("DualShock 4", "Xbox 360"),
            ("Xbox 360", "DualShock 4"),
        ):
            with self.subTest(current_output=current_output, next_output=next_output):
                profile = {"output": current_output}
                manager = SimpleNamespace(
                    remap_rects={
                        f"output:{next_output}": gdm_manager.pygame.Rect(10, 10, 100, 30),
                    },
                    remap_devices=lambda: [],
                    active_remap_profile=lambda: (
                        "hid:one",
                        True,
                        profile["output"],
                        default_profile_mapping(),
                    ),
                    active_remap_joystick=lambda: joystick,
                    device_profiles={"hid:one": profile},
                    _playstation_led_last_attempt=(7, "purple"),
                    _playstation_led_pending={7: ("purple", 100.0, 1)},
                    _playstation_led_color="green",
                    profile_led_color_for_device=lambda _device: (
                        manager._playstation_led_color
                    ),
                    playstation_led_availability=lambda _device: (True, ""),
                    request_playstation_led_reapply=lambda device: (
                        gdm_manager.GDMGamepadManager.request_playstation_led_reapply(
                            manager,
                            device,
                        )
                    ),
                    save_settings=lambda: None,
                    write_console=lambda *_args: None,
                )

                handled = gdm_manager.GDMGamepadManager.handle_remap_click(
                    manager,
                    (20, 20),
                )

                self.assertTrue(handled)
                self.assertEqual(profile["output"], next_output)
                self.assertIsNone(manager._playstation_led_last_attempt)
                self.assertEqual(
                    manager._playstation_led_pending[7][0],
                    manager._playstation_led_color,
                )
                self.assertEqual(manager._playstation_led_pending[7][2], 0)

    def test_reapplies_saved_physical_led_color_after_virtual_output_change(self):
        colors = []
        joystick = SimpleNamespace(
            get_init=lambda: True,
            get_instance_id=lambda: 7,
            get_name=lambda: "DualShock 4",
            get_product_id=lambda: 0x05C4,
        )
        controller = SimpleNamespace(
            set_led=lambda color: colors.append(color) or True,
        )
        manager = SimpleNamespace(
            _playstation_led_pending={7: ("green", 100.0, 0)},
            _playstation_led_last_attempt=None,
            joysticks=[joystick],
            active_remap_joystick=lambda: joystick,
            profile_led_color_for_device=lambda _device: "green",
            playstation_led_availability=lambda _device: (True, ""),
            controller_for=lambda _device: controller,
            write_console=lambda *_args: None,
        )

        with patch(
            "gdm_manager.time.monotonic",
            side_effect=(100.0, 100.0, 101.5, 101.5, 104.0, 104.0),
        ):
            for _attempt in range(3):
                gdm_manager.GDMGamepadManager.update_playstation_led(manager)

        self.assertEqual(len(colors), 3)
        self.assertTrue(all(color == gdm_manager.pygame.Color(0, 220, 80) for color in colors))
        self.assertNotIn(7, manager._playstation_led_pending)
        self.assertEqual(manager._playstation_led_last_attempt, (7, "green"))

    def test_disconnected_custom_profiles_remain_selectable_for_editing(self):
        mapping = default_profile_mapping()
        mapping["a"] = "b"
        disconnected_key = "hid:previous-controller"
        disconnected_profile = {
            "enabled": True,
            "output": "DualShock 4",
            "mapping": mapping,
            "name": "DualSense Wireless Controller",
            "vendor_id": 0x054C,
            "product_id": 0x0CE6,
            "auto_start": True,
            "led_color": "purple",
        }
        physical = SimpleNamespace(
            get_init=lambda: True,
            get_instance_id=lambda: 1,
            get_name=lambda: "Xbox Controller",
        )
        manager = SimpleNamespace(
            joysticks=[physical],
            device_profiles={disconnected_key: disconnected_profile},
            joystick_device_indices={},
            remap_device_key=disconnected_key,
            default_profile_mapping=default_profile_mapping(),
            default_profile_output="auto",
        )
        manager.is_virtual_gamepad = lambda _joystick: False
        manager.remap_device_identity = lambda _joystick: ("hid:connected", True)
        manager.active_remap_device = lambda: next(
            entry for entry in devices if entry[0] == disconnected_key
        )
        manager.active_remap_profile = lambda: (
            gdm_manager.GDMGamepadManager.active_remap_profile(manager)
        )

        devices = gdm_manager.GDMGamepadManager.remap_devices(manager)
        manager.remap_devices = lambda: devices

        self.assertEqual(
            [(key, name) for key, _joystick, name, _stable in devices],
            [
                ("hid:connected", "Xbox Controller"),
                (disconnected_key, "DualSense Wireless Controller"),
            ],
        )
        self.assertIsNone(devices[1][1])
        self.assertEqual(
            gdm_manager.GDMGamepadManager.active_remap_profile(manager),
            (disconnected_key, True, "DualShock 4", mapping),
        )
        self.assertEqual(
            gdm_manager.GDMGamepadManager.profile_led_color_for_device(
                manager,
                None,
            ),
            "purple",
        )
        self.assertTrue(
            gdm_manager.GDMGamepadManager.profile_auto_start_for_device(
                manager,
                None,
            )
        )
        gdm_manager.GDMGamepadManager.set_active_profile_preference(
            manager,
            "auto_start",
            False,
        )
        self.assertFalse(disconnected_profile["auto_start"])
        self.assertFalse(
            gdm_manager.GDMGamepadManager.profile_auto_start_for_device(
                manager,
                None,
            )
        )

    def test_load_settings_restores_custom_auto_start_and_led_color(self):
        with tempfile.TemporaryDirectory() as directory:
            settings_path = Path(directory) / "settings.json"
            settings_path.write_text(
                json.dumps({
                    "virtual_output_auto_start": False,
                    "playstation_led_color": "blue",
                    "device_profiles": {
                        "custom": {
                            "enabled": True,
                            "output": "DualShock 4",
                            "mapping": default_profile_mapping(),
                            "name": "DualSense Wireless Controller",
                            "vendor_id": 0x054C,
                            "product_id": 0x0CE6,
                            "auto_start": True,
                            "led_color": "purple",
                            "shutdown_enabled": True,
                            "shutdown_method": "combo",
                            "shutdown_buttons": ["a", "b", "invalid", 3],
                            "shutdown_action": "unpair",
                            "shutdown_idle_enabled": True,
                            "shutdown_idle_minutes": 3,
                        },
                        "legacy-custom": {
                            "enabled": True,
                            "output": "Xbox 360",
                            "mapping": default_profile_mapping(),
                        },
                    },
                }),
                encoding="utf-8",
            )
            manager = SimpleNamespace(
                settings_path=settings_path,
                virtual_output_auto_start=False,
                playstation_led_color=None,
                disconnect_on_home_hold=True,
                home_hold_action="disconnect",
                default_profile_output="auto",
                default_profile_mapping=default_profile_mapping(),
                device_profiles={},
                hidhide_type_overrides={},
                window_size=(1180, 800),
            )
            manager.load_mapping_values = (
                gdm_manager.GDMGamepadManager.load_mapping_values
            )

            gdm_manager.GDMGamepadManager.load_settings(manager)

        self.assertTrue(manager.device_profiles["custom"]["auto_start"])
        self.assertEqual(manager.device_profiles["custom"]["led_color"], "purple")
        self.assertTrue(manager.device_profiles["custom"]["shutdown_enabled"])
        self.assertEqual(manager.device_profiles["custom"]["shutdown_method"], "combo")
        self.assertEqual(
            manager.device_profiles["custom"]["shutdown_buttons"],
            ["a", "b"],
        )
        self.assertEqual(manager.device_profiles["custom"]["shutdown_action"], "unpair")
        self.assertTrue(manager.device_profiles["custom"]["shutdown_idle_enabled"])
        self.assertEqual(manager.device_profiles["custom"]["shutdown_idle_minutes"], 3)
        self.assertEqual(
            manager.device_profiles["custom"]["name"],
            "DualSense Wireless Controller",
        )
        self.assertEqual(manager.device_profiles["custom"]["vendor_id"], 0x054C)
        self.assertFalse(manager.device_profiles["legacy-custom"]["auto_start"])
        self.assertEqual(manager.device_profiles["legacy-custom"]["led_color"], "blue")
        self.assertTrue(manager.device_profiles["legacy-custom"]["shutdown_enabled"])
        self.assertFalse(manager.device_profiles["legacy-custom"]["shutdown_idle_enabled"])
        self.assertEqual(manager.device_profiles["legacy-custom"]["shutdown_idle_minutes"], 1)
        self.assertEqual(
            manager.device_profiles["legacy-custom"]["shutdown_action"],
            "disconnect",
        )

    def test_shutdown_combo_requires_every_selected_button(self):
        pressed = {gdm_manager.REMAP_SDL_BUTTONS["a"]: True}
        controller = SimpleNamespace(
            get_button=lambda button: pressed.get(button, False),
        )
        manager = SimpleNamespace(controller_for=lambda _joystick: controller)

        self.assertTrue(
            gdm_manager.GDMGamepadManager.shutdown_button_combo_pressed(
                manager,
                object(),
                ["a"],
            )
        )
        self.assertFalse(
            gdm_manager.GDMGamepadManager.shutdown_button_combo_pressed(
                manager,
                object(),
                ["a", "b"],
            )
        )
        self.assertFalse(
            gdm_manager.GDMGamepadManager.shutdown_button_combo_pressed(
                manager,
                object(),
                [],
            )
        )

    def test_find_bluetooth_device_matches_generic_name_without_transport_prefix(self):
        joystick = SimpleNamespace(
            get_name=lambda: "Bluetooth Gamepad",
            get_vendor_id=lambda: 0,
            get_product_id=lambda: 0,
        )
        bluetooth_device = {
            "id": "gamepad-id",
            "name": "Gamepad",
            "gamepad": True,
            "connected": True,
        }
        manager = SimpleNamespace(
            bluetooth_devices={"gamepad-id": bluetooth_device},
            normalize_device_name=(
                gdm_manager.GDMGamepadManager.normalize_device_name
            ),
            model_name=lambda name, vendor=0: (
                gdm_manager.GDMGamepadManager.model_name(
                    None,
                    name,
                    vendor,
                )
            ),
            preferred_bluetooth_match=(
                gdm_manager.GDMGamepadManager.preferred_bluetooth_match
            ),
        )

        self.assertIs(
            gdm_manager.GDMGamepadManager.find_bluetooth_device(
                manager,
                joystick,
            ),
            bluetooth_device,
        )

    def test_find_bluetooth_device_does_not_guess_between_generic_name_matches(self):
        joystick = SimpleNamespace(
            get_name=lambda: "Bluetooth Gamepad",
            get_vendor_id=lambda: 0,
            get_product_id=lambda: 0,
        )
        bluetooth_devices = {
            "first": {
                "id": "first",
                "name": "Gamepad",
                "gamepad": True,
                "connected": True,
            },
            "second": {
                "id": "second",
                "name": "Wireless Gamepad",
                "gamepad": True,
                "connected": True,
            },
        }
        manager = SimpleNamespace(
            bluetooth_devices=bluetooth_devices,
            normalize_device_name=(
                gdm_manager.GDMGamepadManager.normalize_device_name
            ),
            model_name=lambda name, vendor=0: (
                gdm_manager.GDMGamepadManager.model_name(
                    None,
                    name,
                    vendor,
                )
            ),
            preferred_bluetooth_match=(
                gdm_manager.GDMGamepadManager.preferred_bluetooth_match
            ),
        )

        self.assertIsNone(
            gdm_manager.GDMGamepadManager.find_bluetooth_device(
                manager,
                joystick,
            )
        )

    def test_find_bluetooth_device_collapses_classic_and_le_records_for_one_pad(self):
        joystick = SimpleNamespace(
            get_name=lambda: "Bluetooth Gamepad",
            get_vendor_id=lambda: 0,
            get_product_id=lambda: 0,
        )
        classic_device = {
            "id": "classic-id",
            "name": "Gamepad",
            "gamepad": True,
            "connected": True,
            "container_id": "controller-container",
            "transport": "Bluetooth clásico",
        }
        le_device = {
            "id": "le-id",
            "name": "Gamepad",
            "gamepad": True,
            "connected": True,
            "container_id": "controller-container",
            "transport": "Bluetooth LE",
        }
        manager = SimpleNamespace(
            bluetooth_devices={
                classic_device["id"]: classic_device,
                le_device["id"]: le_device,
            },
            normalize_device_name=(
                gdm_manager.GDMGamepadManager.normalize_device_name
            ),
            model_name=gdm_manager.GDMGamepadManager.model_name,
            preferred_bluetooth_match=(
                gdm_manager.GDMGamepadManager.preferred_bluetooth_match
            ),
        )

        match = gdm_manager.GDMGamepadManager.find_bluetooth_device(
            manager,
            joystick,
            prefer_classic=True,
        )
        self.assertEqual(match["id"], classic_device["id"])

    def test_dualshock_name_matches_sony_bluetooth_record_and_prefers_classic(self):
        joystick = SimpleNamespace(
            get_name=lambda: "PS4 Controller",
            get_vendor_id=lambda: 0x054C,
            get_product_id=lambda: 0x05C4,
        )
        classic_device = {
            "id": "classic-ds4",
            "name": "DualSense Wireless Controller",
            "gamepad": True,
            "connected": True,
            "paired": True,
            "hardware_ids": ["BTHENUM\\VID_054C&PID_09CC"],
            "container_id": "dualshock-container",
            "transport": "Bluetooth clásico",
        }
        le_device = {
            **classic_device,
            "id": "le-ds4",
            "transport": "Bluetooth LE",
        }
        manager = SimpleNamespace(
            bluetooth_devices={
                classic_device["id"]: classic_device,
                le_device["id"]: le_device,
            },
            normalize_device_name=(
                gdm_manager.GDMGamepadManager.normalize_device_name
            ),
            model_name=lambda name, vendor=0: (
                gdm_manager.GDMGamepadManager.model_name(None, name, vendor)
            ),
            bluetooth_device_model=lambda device: (
                gdm_manager.GDMGamepadManager.model_name(
                    None,
                    device.get("name", ""),
                )
            ),
            preferred_bluetooth_match=(
                gdm_manager.GDMGamepadManager.preferred_bluetooth_match
            ),
        )

        match = gdm_manager.GDMGamepadManager.find_bluetooth_device(
            manager,
            joystick,
            prefer_classic=True,
        )
        self.assertEqual(match["id"], classic_device["id"])
        self.assertEqual(match["display_name"], "DualShock 4")

    def test_ps4_controller_name_does_not_match_a_dualsense_record(self):
        joystick = SimpleNamespace(
            get_name=lambda: "PS4 Controller",
            get_vendor_id=lambda: 0,
            get_product_id=lambda: 0,
        )
        ds4_record = {
            "id": "wireless-controller",
            "name": "Wireless Controller",
            "gamepad": True,
            "connected": True,
            "paired": True,
            "container_id": "ds4-container",
            "transport": "Bluetooth clásico",
        }
        ds5_record = {
            "id": "dualsense",
            "name": "DualSense Wireless Controller",
            "gamepad": True,
            "connected": True,
            "paired": True,
            "hardware_ids": ["BTHENUM\\VID_054C&PID_0CE6"],
            "container_id": "ds5-container",
            "transport": "Bluetooth clásico",
        }
        manager = SimpleNamespace(
            bluetooth_devices={
                ds4_record["id"]: ds4_record,
                ds5_record["id"]: ds5_record,
            },
            normalize_device_name=(
                gdm_manager.GDMGamepadManager.normalize_device_name
            ),
            model_name=lambda name, vendor=0: (
                gdm_manager.GDMGamepadManager.model_name(
                    None,
                    name,
                    vendor,
                )
            ),
            bluetooth_device_model=lambda device: (
                gdm_manager.GDMGamepadManager.model_name(
                    None,
                    device.get("name", ""),
                )
            ),
            preferred_bluetooth_match=(
                gdm_manager.GDMGamepadManager.preferred_bluetooth_match
            ),
        )

        match = gdm_manager.GDMGamepadManager.find_bluetooth_device(
            manager,
            joystick,
            prefer_classic=True,
        )

        self.assertEqual(match["id"], ds4_record["id"])
        self.assertEqual(match["display_name"], "DualShock 4")

    def test_home_button_uses_hardware_guide_indices_when_sdl_does_not_report_it(self):
        for model, button_index, name in (
            ("Xbox 360", 8, "Xbox 360 Controller"),
            ("PlayStation", 5, "PS4 Controller"),
        ):
            with self.subTest(model=model):
                buttons = {button_index}
                joystick = SimpleNamespace(
                    get_name=lambda name=name: name,
                    get_vendor_id=lambda: 0,
                    get_numbuttons=lambda: 16,
                    get_button=lambda index: index in buttons,
                )
                controller = SimpleNamespace(
                    get_button=lambda _button: False,
                )
                manager = SimpleNamespace(
                    controller_for=lambda _joystick: controller,
                    model_name=lambda name, vendor=0: (
                        gdm_manager.GDMGamepadManager.model_name(
                            None,
                            name,
                            vendor,
                        )
                    ),
                    pressed=gdm_manager.GDMGamepadManager.pressed,
                    write_console=lambda *_args: None,
                )

                self.assertTrue(
                    gdm_manager.GDMGamepadManager.home_button_pressed(
                        manager,
                        joystick,
                    )
                )

    def test_xbox_one_guide_uses_xinput_get_state_ex(self):
        joystick = SimpleNamespace(
            get_name=lambda: "XInput Controller #2",
            get_vendor_id=lambda: 0x045E,
            get_product_id=lambda: 0x02EA,
            get_numbuttons=lambda: 16,
            get_button=lambda index: index == 10,
        )
        controller = SimpleNamespace(get_button=lambda _button: False)
        manager = SimpleNamespace(
            controller_for=lambda _joystick: controller,
            model_name=lambda name, vendor=0: (
                gdm_manager.GDMGamepadManager.model_name(None, name, vendor)
            ),
            pressed=gdm_manager.GDMGamepadManager.pressed,
            write_console=lambda *_args: None,
        )
        called_user_indices = []

        def read_state(user_index, state_pointer):
            called_user_indices.append(user_index)
            state_pointer._obj.gamepad.buttons = gdm_manager.XINPUT_GAMEPAD_GUIDE
            return 0

        with patch(
            "gdm_manager.xinput_guide_api",
            return_value=(object(), read_state, None),
        ):
            self.assertTrue(
                gdm_manager.GDMGamepadManager.home_button_pressed(
                    manager,
                    joystick,
                )
            )

        self.assertEqual(called_user_indices, [1])

    def test_xbox_one_without_xinput_guide_does_not_use_raw_button_ten(self):
        joystick = SimpleNamespace(
            get_name=lambda: "Xbox One S Controller",
            get_vendor_id=lambda: 0,
            get_product_id=lambda: 0,
            get_numbuttons=lambda: 16,
            get_button=lambda index: index == 10,
        )
        manager = SimpleNamespace(
            controller_for=lambda _joystick: None,
            model_name=lambda name, vendor=0: (
                gdm_manager.GDMGamepadManager.model_name(None, name, vendor)
            ),
            pressed=gdm_manager.GDMGamepadManager.pressed,
            write_console=lambda *_args: None,
        )

        with patch(
            "gdm_manager.xinput_guide_pressed_for_joystick",
            return_value=False,
        ):
            self.assertFalse(
                gdm_manager.GDMGamepadManager.home_button_pressed(
                    manager,
                    joystick,
                )
            )

    def test_xinput_slot_matching_refuses_ambiguous_identical_devices(self):
        joystick = SimpleNamespace(
            get_name=lambda: "Xbox One S Controller",
            get_vendor_id=lambda: 0x045E,
            get_product_id=lambda: 0x02EA,
        )

        def capabilities_for_two_slots(_reserved, user_index, _flags, pointer):
            if user_index in (0, 1):
                pointer._obj.vendor_id = 0x045E
                pointer._obj.product_id = 0x02EA
                return 0
            return 1167

        self.assertIsNone(
            gdm_manager.xinput_user_index_for_joystick(
                joystick,
                capabilities_for_two_slots,
            )
        )

    def test_xinput_slot_matching_uses_unique_vendor_and_product(self):
        joystick = SimpleNamespace(
            get_name=lambda: "Xbox One S Controller",
            get_vendor_id=lambda: 0x045E,
            get_product_id=lambda: 0x02EA,
        )

        def capabilities_for_one_slot(_reserved, user_index, _flags, pointer):
            if user_index == 3:
                pointer._obj.vendor_id = 0x045E
                pointer._obj.product_id = 0x02EA
                return 0
            return 1167

        self.assertEqual(
            gdm_manager.xinput_user_index_for_joystick(
                joystick,
                capabilities_for_one_slot,
            ),
            3,
        )

    def test_xbox_one_does_not_treat_button_eight_as_home(self):
        joystick = SimpleNamespace(
            get_name=lambda: "Xbox One S Controller",
            get_vendor_id=lambda: 0,
            get_product_id=lambda: 0,
            get_numbuttons=lambda: 16,
            get_button=lambda index: index == 8,
        )
        manager = SimpleNamespace(
            controller_for=lambda _joystick: None,
            model_name=lambda name, vendor=0: (
                gdm_manager.GDMGamepadManager.model_name(None, name, vendor)
            ),
            pressed=gdm_manager.GDMGamepadManager.pressed,
            write_console=lambda *_args: None,
        )

        with patch(
            "gdm_manager.xinput_guide_pressed_for_joystick",
            return_value=False,
        ):
            self.assertFalse(
                gdm_manager.GDMGamepadManager.home_button_pressed(
                    manager,
                    joystick,
                )
            )

    def test_home_button_does_not_treat_right_shoulder_as_guide(self):
        joystick = SimpleNamespace(
            get_name=lambda: "Xbox One S Controller",
            get_vendor_id=lambda: 0,
            get_numbuttons=lambda: 16,
            get_button=lambda index: index == 5,
        )
        manager = SimpleNamespace(
            controller_for=lambda _joystick: None,
            model_name=lambda name, vendor=0: (
                gdm_manager.GDMGamepadManager.model_name(None, name, vendor)
            ),
            pressed=gdm_manager.GDMGamepadManager.pressed,
            write_console=lambda *_args: None,
        )

        with patch(
            "gdm_manager.xinput_guide_pressed_for_joystick",
            return_value=False,
        ):
            self.assertFalse(
                gdm_manager.GDMGamepadManager.home_button_pressed(
                    manager,
                    joystick,
                )
            )

    def test_virtual_outputs_are_independent_per_physical_controller(self):
        first = SimpleNamespace(
            key="generic",
            get_init=lambda: True,
            get_instance_id=lambda: 1,
            get_name=lambda: "Bluetooth Gamepad",
        )
        second = SimpleNamespace(
            key="xbox",
            get_init=lambda: True,
            get_instance_id=lambda: 2,
            get_name=lambda: "Xbox Controller",
        )
        created_bridges = []

        class FakeBridge:
            status = "Desactivado"

            def __init__(self):
                self.updates = []
                self.closed = False
                created_bridges.append(self)

            def update(self, enabled, output_type, joystick, controller, mapping):
                self.updates.append((enabled, output_type, joystick, mapping))
                self.status = f"Mando virtual {output_type} activo."
                return self.status

            def close(self):
                self.closed = True

        manager = SimpleNamespace(
            virtual_outputs={},
            virtual_output_statuses={},
            virtual_output_status="Desactivado",
            joysticks=[first, second],
            remap_device_identity=lambda joystick: (joystick.key, True),
            remap_profile_for_device=lambda joystick: (
                joystick.key,
                True,
                "Xbox 360",
                {"source": joystick.key},
            ),
            controller_for=lambda _joystick: None,
            write_console=lambda *_args: None,
            active_remap_device=lambda: ("generic", first, "Generic", True),
        )

        with patch("gdm_manager.VirtualGamepadBridge", FakeBridge):
            gdm_manager.GDMGamepadManager.start_virtual_output_for_device(
                manager,
                first,
            )
            gdm_manager.GDMGamepadManager.start_virtual_output_for_device(
                manager,
                second,
            )
            gdm_manager.GDMGamepadManager.update_virtual_gamepad(manager)

            self.assertEqual(len(created_bridges), 2)
            self.assertIsNot(
                manager.virtual_outputs["generic"],
                manager.virtual_outputs["xbox"],
            )
            self.assertEqual(created_bridges[0].updates[0][1:], (
                "Xbox 360",
                first,
                {"source": "generic"},
            ))
            self.assertEqual(created_bridges[1].updates[0][1:], (
                "Xbox 360",
                second,
                {"source": "xbox"},
            ))

            gdm_manager.GDMGamepadManager.stop_virtual_output_for_device(
                manager,
                "generic",
            )

        self.assertTrue(created_bridges[0].closed)
        self.assertFalse(created_bridges[1].closed)
        self.assertEqual(set(manager.virtual_outputs), {"xbox"})

    def test_custom_shutdown_combo_executes_after_two_seconds(self):
        joystick = object()
        match = {"id": "bluetooth-device", "name": "Gamepad"}
        times = iter((100, 2100))
        executed = []
        manager = SimpleNamespace(
            pending_home_action=None,
            home_action_joystick=lambda: joystick,
            check_shutdown_idle_timers=lambda: None,
            shutdown_settings_for_device=lambda _joystick: {
                "custom": True,
                "enabled": True,
                "method": "combo",
                "buttons": ["a", "b"],
                "action": "unpair",
            },
            shutdown_button_combo_pressed=lambda _joystick, buttons: (
                buttons == ["a", "b"]
            ),
            home_hold_started=None,
            home_hold_triggered=False,
            find_bluetooth_device=lambda _joystick, **_kwargs: match,
            execute_home_action=lambda *args, **kwargs: executed.append((args, kwargs)),
            request_shutdown_action=lambda _joystick, settings: executed.append(
                (
                    (settings["action"], match),
                    {"retry_auto_pair": not settings["custom"]},
                )
            ),
            write_console=lambda *_args: None,
        )

        with patch(
            "gdm_manager.pygame.time.get_ticks",
            side_effect=lambda: next(times),
        ):
            gdm_manager.GDMGamepadManager.check_home_hold(manager)
            gdm_manager.GDMGamepadManager.check_home_hold(manager)

        self.assertEqual(
            executed,
            [
                (
                    ("unpair", match),
                    {"retry_auto_pair": False},
                )
            ],
        )

    def test_idle_timeout_uses_configured_unpair_action_only_once(self):
        joystick = SimpleNamespace(
            get_init=lambda: True,
            get_instance_id=lambda: 17,
            get_name=lambda: "Gamepad",
        )
        settings = {
            "custom": True,
            "enabled": False,
            "idle_enabled": True,
            "idle_minutes": 1,
            "action": "unpair",
        }
        requests = []
        manager = SimpleNamespace(
            joysticks=[joystick],
            pending_home_action=None,
            shutdown_idle_last_activity={17: 100.0},
            shutdown_idle_triggered=set(),
            shutdown_idle_input_errors=set(),
            is_virtual_gamepad=lambda _joystick: False,
            shutdown_settings_for_device=lambda _joystick: settings,
            gamepad_has_activity=lambda _joystick: False,
            request_shutdown_action=lambda _joystick, action_settings: requests.append(
                action_settings["action"]
            ),
            write_console=lambda *_args: None,
        )

        with patch("gdm_manager.time.monotonic", return_value=160.0):
            gdm_manager.GDMGamepadManager.check_shutdown_idle_timers(manager)
            gdm_manager.GDMGamepadManager.check_shutdown_idle_timers(manager)

        self.assertEqual(requests, ["unpair"])
        self.assertEqual(manager.shutdown_idle_triggered, {17})

    def test_idle_timeout_resets_when_controller_is_active(self):
        joystick = SimpleNamespace(
            get_init=lambda: True,
            get_instance_id=lambda: 18,
            get_name=lambda: "Gamepad",
        )
        manager = SimpleNamespace(
            joysticks=[joystick],
            pending_home_action=None,
            shutdown_idle_last_activity={18: 100.0},
            shutdown_idle_triggered=set(),
            shutdown_idle_input_errors=set(),
            is_virtual_gamepad=lambda _joystick: False,
            shutdown_settings_for_device=lambda _joystick: {
                "custom": True,
                "enabled": True,
                "idle_enabled": True,
                "idle_minutes": 1,
                "action": "disconnect",
            },
            gamepad_has_activity=lambda _joystick: True,
            request_shutdown_action=lambda *_args: self.fail(
                "activity should restart the inactivity timer"
            ),
            write_console=lambda *_args: None,
        )

        with patch("gdm_manager.time.monotonic", return_value=160.0):
            gdm_manager.GDMGamepadManager.check_shutdown_idle_timers(manager)

        self.assertEqual(manager.shutdown_idle_last_activity[18], 160.0)

    def test_gamepad_activity_detects_buttons_sticks_triggers_and_hat(self):
        cases = (
            (1, 0, lambda index: index == 0, lambda _index: 0.0, lambda _index: (0, 0)),
            (0, 1, lambda _index: False, lambda index: 0.35 if index == 0 else 0.0, lambda _index: (0, 0)),
            (0, 5, lambda _index: False, lambda index: 0.35 if index == 4 else 0.0, lambda _index: (0, 0)),
            (0, 0, lambda _index: False, lambda _index: 0.0, lambda _index: (1, 0)),
        )
        manager = SimpleNamespace(
            shutdown_idle_input_errors=set(),
            write_console=lambda *_args: None,
        )

        for button_count, axis_count, button, axis, hat in cases:
            joystick = SimpleNamespace(
                get_instance_id=lambda: 19,
                get_numbuttons=lambda: button_count,
                get_button=button,
                get_numaxes=lambda: axis_count,
                get_axis=axis,
                get_numhats=lambda: 1,
                get_hat=hat,
            )
            with self.subTest(button_count=button_count, axis_count=axis_count):
                self.assertTrue(
                    gdm_manager.GDMGamepadManager.gamepad_has_activity(
                        manager,
                        joystick,
                    )
                )

    def test_request_shutdown_action_matches_button_action_path(self):
        joystick = object()
        match = {"id": "bluetooth-device", "name": "Gamepad"}
        executed = []
        manager = SimpleNamespace(
            find_bluetooth_device=lambda _joystick, **kwargs: (
                match if kwargs["prefer_classic"] is False else None
            ),
            execute_home_action=lambda *args, **kwargs: executed.append((args, kwargs)),
            write_console=lambda *_args: None,
        )

        gdm_manager.GDMGamepadManager.request_shutdown_action(
            manager,
            joystick,
            {
                "custom": True,
                "action": "unpair",
            },
        )

        self.assertEqual(
            executed,
            [
                (
                    ("unpair", match),
                    {"retry_auto_pair": False},
                )
            ],
        )


if __name__ == "__main__":
    unittest.main()
