import unittest
from types import SimpleNamespace
from unittest.mock import patch

import gdm_manager


class KeyboardNavigationTests(unittest.TestCase):
    def test_arrow_keys_move_focus_between_main_tabs_and_enter_activates(self):
        manager = SimpleNamespace(
            active_tab="devices",
            keyboard_focus_key="nav:devices",
            nav_rects={
                "devices": gdm_manager.pygame.Rect(10, 10, 80, 30),
                "tester": gdm_manager.pygame.Rect(100, 10, 80, 30),
                "options": gdm_manager.pygame.Rect(190, 10, 80, 30),
            },
            devices_rects={},
            activate_navigation_tab=lambda tab: setattr(manager, "active_tab", tab),
        )
        manager.keyboard_focusable_rects = lambda: (
            gdm_manager.GDMGamepadManager.keyboard_focusable_rects(manager)
        )

        self.assertTrue(
            gdm_manager.GDMGamepadManager.move_keyboard_focus(manager, "right")
        )
        self.assertEqual(manager.keyboard_focus_key, "nav:tester")
        self.assertTrue(
            gdm_manager.GDMGamepadManager.move_keyboard_focus(manager, "right")
        )
        self.assertEqual(manager.keyboard_focus_key, "nav:options")
        self.assertTrue(
            gdm_manager.GDMGamepadManager.activate_keyboard_focus(manager)
        )
        self.assertEqual(manager.active_tab, "options")

    def test_options_focus_scrolls_into_view_when_navigating_down(self):
        manager = SimpleNamespace(
            active_tab="options",
            keyboard_focus_key="options:theme:dark",
            nav_rects={},
            options_rects={
                "section_general": gdm_manager.pygame.Rect(10, 10, 80, 30),
                "section_interface": gdm_manager.pygame.Rect(100, 10, 80, 30),
                "theme:dark": gdm_manager.pygame.Rect(10, 100, 80, 30),
                "startup_options": gdm_manager.pygame.Rect(10, 350, 80, 30),
            },
            options_scroll=0,
            options_max_scroll=300,
            options_viewport=gdm_manager.pygame.Rect(0, 100, 250, 100),
        )
        manager.keyboard_focusable_rects = lambda: (
            gdm_manager.GDMGamepadManager.keyboard_focusable_rects(manager)
        )

        self.assertTrue(
            gdm_manager.GDMGamepadManager.move_keyboard_focus(manager, "down")
        )
        self.assertEqual(manager.keyboard_focus_key, "options:startup_options")
        self.assertEqual(manager.options_scroll, 180)

    def test_keyboard_activation_calls_current_options_handler(self):
        handled_positions = []
        selected = gdm_manager.pygame.Rect(30, 70, 100, 32)
        manager = SimpleNamespace(
            keyboard_focus_key="options:theme:light",
            options_rects={"theme:light": selected},
            handle_options_click=lambda position: (
                handled_positions.append(position) or True
            ),
        )

        self.assertTrue(
            gdm_manager.GDMGamepadManager.activate_keyboard_focus(manager)
        )
        self.assertEqual(handled_positions, [selected.center])

    def test_tester_focus_activates_the_next_controller_button(self):
        next_button = gdm_manager.pygame.Rect(90, 20, 32, 32)
        manager = SimpleNamespace(
            active_tab="tester",
            keyboard_focus_key="tester:next",
            nav_rects={},
            tester_rects={"next": next_button},
            handle_tester_click=lambda position: position == next_button.center,
        )

        self.assertTrue(
            gdm_manager.GDMGamepadManager.activate_keyboard_focus(manager)
        )

    def test_keydown_arrows_are_routed_to_ui_navigation(self):
        moved = []
        manager = SimpleNamespace(
            welcome_dialog_open=False,
            escape_dialog_open=False,
            shutdown_dialog_open=False,
            process_bluetooth_events=lambda: None,
            process_dashboard_battery_events=lambda: None,
            process_hidhide_events=lambda: None,
            process_startup_events=lambda: None,
            process_dependency_events=lambda: None,
            process_tray_events=lambda: None,
            move_keyboard_focus=lambda direction: moved.append(direction),
            check_home_hold=lambda: None,
            _device_refresh_deadline=None,
            save_window_size_if_due=lambda: None,
        )
        event = SimpleNamespace(
            type=gdm_manager.pygame.KEYDOWN,
            key=gdm_manager.pygame.K_DOWN,
        )

        with patch("gdm_manager.pygame.event.get", return_value=[event]):
            gdm_manager.GDMGamepadManager.handle_events(manager)

        self.assertEqual(moved, ["down"])


if __name__ == "__main__":
    unittest.main()
