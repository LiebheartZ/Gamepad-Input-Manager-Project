import queue
import subprocess
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import gdm_manager


class DependencyManagerTests(unittest.TestCase):
    def test_dependency_scan_reports_missing_python_and_external_tools(self):
        available_modules = {"pygame", "PIL"}
        manager = SimpleNamespace(
            dependency_items={},
            hidhide_service=SimpleNamespace(cli_path=None),
            python_module_available=lambda module: module in available_modules,
            vigembus_installed=lambda: False,
            write_console=lambda *_args: None,
        )

        with patch(
            "gdm_manager.HidHideCLIService.find_cli",
            return_value=None,
        ):
            gdm_manager.GDMGamepadManager.refresh_dependency_status(manager)

        self.assertFalse(manager.dependency_items["python"]["installed"])
        self.assertIn("hidapi", manager.dependency_items["python"]["status"])
        self.assertFalse(manager.dependency_items["vgamepad"]["installed"])
        self.assertFalse(manager.dependency_items["vigembus"]["installed"])
        self.assertFalse(manager.dependency_items["hidhide"]["installed"])

    def test_non_windows_scan_does_not_require_winrt_packages(self):
        manager = SimpleNamespace(
            dependency_items={},
            hidhide_service=SimpleNamespace(cli_path=Path("HidHideCLI.exe")),
            python_module_available=lambda module: not module.startswith("winrt."),
            vigembus_installed=lambda: None,
            write_console=lambda *_args: None,
        )

        with (
            patch("gdm_manager.platform.system", return_value="Linux"),
            patch(
                "gdm_manager.HidHideCLIService.find_cli",
                return_value=Path("HidHideCLI.exe"),
            ),
        ):
            gdm_manager.GDMGamepadManager.refresh_dependency_status(manager)

        self.assertTrue(manager.dependency_items["python"]["installed"])
        self.assertTrue(manager.dependency_items["hidhide"]["installed"])
        self.assertIsNone(manager.dependency_items["vigembus"]["installed"])

    def test_windows_scan_includes_all_winrt_requirements(self):
        missing_modules = {
            "winrt.windows.foundation",
            "winrt.windows.foundation.collections",
        }
        manager = SimpleNamespace(
            dependency_items={},
            hidhide_service=SimpleNamespace(cli_path=None),
            python_module_available=lambda module: module not in missing_modules,
            vigembus_installed=lambda: None,
            write_console=lambda *_args: None,
        )

        with (
            patch("gdm_manager.platform.system", return_value="Windows"),
            patch("gdm_manager.HidHideCLIService.find_cli", return_value=None),
        ):
            gdm_manager.GDMGamepadManager.refresh_dependency_status(manager)

        python_status = manager.dependency_items["python"]["status"]
        self.assertIn("winrt-Windows.Foundation", python_status)
        self.assertIn("winrt-Windows.Foundation.Collections", python_status)

    def test_refresh_detects_hidhide_installed_while_app_is_open(self):
        manager = SimpleNamespace(
            dependency_items={},
            hidhide_service=SimpleNamespace(cli_path=None),
            python_module_available=lambda _module: True,
            vigembus_installed=lambda: None,
            write_console=lambda *_args: None,
        )
        cli_path = Path("HidHideCLI.exe")

        with patch(
            "gdm_manager.HidHideCLIService.find_cli",
            side_effect=(None, cli_path),
        ):
            gdm_manager.GDMGamepadManager.refresh_dependency_status(manager)
            self.assertFalse(manager.dependency_items["hidhide"]["installed"])

            gdm_manager.GDMGamepadManager.refresh_dependency_status(manager)
            self.assertTrue(manager.dependency_items["hidhide"]["installed"])
            self.assertEqual(manager.hidhide_service.cli_path, cli_path)

    def test_vgamepad_install_uses_pip_without_running_its_driver_installer(self):
        events = queue.Queue()
        commands = []

        class InlineThread:
            def __init__(self, target, **_kwargs):
                self.target = target

            def start(self):
                self.target()

        manager = SimpleNamespace(
            dependency_installing=None,
            dependency_status="",
            dependency_events=events,
            write_console=lambda *_args: None,
        )
        result = subprocess.CompletedProcess([], 0, "Successfully installed vgamepad", "")

        def run(command, **kwargs):
            commands.append((command, kwargs))
            return result

        with (
            patch("gdm_manager.threading.Thread", InlineThread),
            patch("gdm_manager.subprocess.run", side_effect=run),
        ):
            gdm_manager.GDMGamepadManager.start_dependency_install(
                manager,
                "vgamepad",
            )

        command, options = commands[0]
        self.assertEqual(command[-3:], ["pip", "install", "vgamepad"])
        self.assertEqual(
            options["env"]["VGAMEPAD_SKIP_VIGEMBUS_INSTALL"],
            "true",
        )
        self.assertEqual(events.get_nowait(), (
            "vgamepad",
            True,
            "Successfully installed vgamepad",
        ))

    def test_python_install_targets_project_requirements_file(self):
        events = queue.Queue()
        commands = []

        class InlineThread:
            def __init__(self, target, **_kwargs):
                self.target = target

            def start(self):
                self.target()

        manager = SimpleNamespace(
            dependency_installing=None,
            dependency_status="",
            dependency_events=events,
            write_console=lambda *_args: None,
        )
        result = subprocess.CompletedProcess([], 0, "installed", "")

        with (
            patch("gdm_manager.threading.Thread", InlineThread),
            patch(
                "gdm_manager.subprocess.run",
                side_effect=lambda command, **_kwargs: (
                    commands.append(command) or result
                ),
            ),
        ):
            gdm_manager.GDMGamepadManager.start_dependency_install(
                manager,
                "python",
            )

        self.assertEqual(commands[0][1:4], ["-m", "pip", "install"])
        self.assertEqual(commands[0][-2], "-r")
        self.assertTrue(Path(commands[0][-1]).is_file())
        self.assertTrue(events.get_nowait()[1])

    def test_welcome_dialog_opens_dependency_section(self):
        manager = SimpleNamespace(
            welcome_dialog_rects={"dependencies": gdm_manager.pygame.Rect(10, 10, 100, 30)},
            welcome_dialog_open=True,
            active_tab="devices",
            options_section="general",
            options_scroll=200,
        )

        gdm_manager.GDMGamepadManager.handle_welcome_dialog_click(
            manager,
            (20, 20),
        )

        self.assertFalse(manager.welcome_dialog_open)
        self.assertEqual(manager.active_tab, "options")
        self.assertEqual(manager.options_section, "dependencies")
        self.assertEqual(manager.options_scroll, 0)

    def test_interface_checkbox_restores_welcome_dialog_on_next_launch(self):
        manager = SimpleNamespace(
            options_section="interface",
            options_rects={
                "welcome_dialog": gdm_manager.pygame.Rect(10, 10, 240, 26),
            },
            welcome_dialog_dismissed=True,
            save_settings=lambda: None,
        )

        handled = gdm_manager.GDMGamepadManager.handle_options_click(manager, (20, 20))

        self.assertTrue(handled)
        self.assertFalse(manager.welcome_dialog_dismissed)


if __name__ == "__main__":
    unittest.main()
