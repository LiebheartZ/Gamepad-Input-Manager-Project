"""Optional ViGEmBus output bridge for Xbox 360 and DualShock 4 virtual pads."""

from __future__ import annotations

from typing import Any, Optional

import pygame


PROFILE_NAMES = ("PlayStation", "Xbox", "Genérico")
OUTPUT_TYPES = ("Xbox 360", "DualShock 4")
BUTTONS = (
    ("a", "A"),
    ("b", "B"),
    ("x", "X"),
    ("y", "Y"),
    ("back", "Select / Share"),
    ("guide", "Home / PS"),
    ("start", "Start / Options"),
    ("left_stick", "Click stick izq."),
    ("right_stick", "Click stick der."),
    ("left_shoulder", "L1 / LB"),
    ("right_shoulder", "R1 / RB"),
    ("dpad_up", "Cruceta arriba"),
    ("dpad_down", "Cruceta abajo"),
    ("dpad_left", "Cruceta izquierda"),
    ("dpad_right", "Cruceta derecha"),
    ("touchpad", "Click touchpad"),
)
BUTTON_IDS = tuple(key for key, _ in BUTTONS)

RAW_BUTTONS = {
    "a": 0,
    "b": 1,
    "x": 2,
    "y": 3,
    "back": 4,
    "guide": 5,
    "start": 6,
    "left_stick": 7,
    "right_stick": 8,
    "left_shoulder": 9,
    "right_shoulder": 10,
    "dpad_up": 11,
    "dpad_down": 12,
    "dpad_left": 13,
    "dpad_right": 14,
    "touchpad": 17,
}
SDL_BUTTONS = {
    "a": pygame.CONTROLLER_BUTTON_A,
    "b": pygame.CONTROLLER_BUTTON_B,
    "x": pygame.CONTROLLER_BUTTON_X,
    "y": pygame.CONTROLLER_BUTTON_Y,
    "back": pygame.CONTROLLER_BUTTON_BACK,
    "guide": pygame.CONTROLLER_BUTTON_GUIDE,
    "start": pygame.CONTROLLER_BUTTON_START,
    "left_stick": pygame.CONTROLLER_BUTTON_LEFTSTICK,
    "right_stick": pygame.CONTROLLER_BUTTON_RIGHTSTICK,
    "left_shoulder": pygame.CONTROLLER_BUTTON_LEFTSHOULDER,
    "right_shoulder": pygame.CONTROLLER_BUTTON_RIGHTSHOULDER,
    "dpad_up": pygame.CONTROLLER_BUTTON_DPAD_UP,
    "dpad_down": pygame.CONTROLLER_BUTTON_DPAD_DOWN,
    "dpad_left": pygame.CONTROLLER_BUTTON_DPAD_LEFT,
    "dpad_right": pygame.CONTROLLER_BUTTON_DPAD_RIGHT,
    "touchpad": 20,
}


def default_profile_outputs() -> dict[str, str]:
    return {
        "PlayStation": "DualShock 4",
        "Xbox": "Xbox 360",
        "Genérico": "Xbox 360",
    }


def default_profile_mappings() -> dict[str, dict[str, Optional[str]]]:
    return {
        profile: {button_id: button_id for button_id in BUTTON_IDS}
        for profile in PROFILE_NAMES
    }


def button_target_labels(output_type: str) -> dict[str, str]:
    labels = dict(BUTTONS)
    if output_type == "DualShock 4":
        labels.update({
            "a": "Cruz",
            "b": "Círculo",
            "x": "Cuadrado",
            "y": "Triángulo",
            "back": "Share",
            "guide": "PS",
            "start": "Options",
            "left_shoulder": "L1",
            "right_shoulder": "R1",
        })
    else:
        labels.update({
            "back": "View / Select",
            "guide": "Xbox",
            "left_shoulder": "LB",
            "right_shoulder": "RB",
        })
        labels.pop("touchpad", None)
    return labels


class VirtualGamepadBridge:
    def __init__(self, module: Any = None) -> None:
        self._module = module
        self._device: Any = None
        self._device_signature: Optional[tuple[int, str]] = None
        self._failed_signature: Optional[tuple[int, str]] = None
        self.status = "Desactivado"

    def close(self) -> None:
        device, self._device = self._device, None
        self._device_signature = None
        if device is None:
            return
        try:
            device.reset()
            device.update()
        except Exception as error:
            self.status = f"No se pudo reiniciar el mando virtual antes de cerrarlo: {error}"
        finally:
            del device

    def _load_module(self) -> Any:
        if self._module is None:
            try:
                import vgamepad
            except ImportError as error:
                raise RuntimeError(
                    "Falta vgamepad. Instálalo sin ejecutar el instalador del driver "
                    "y luego instala ViGEmBus por separado."
                ) from error
            self._module = vgamepad
        return self._module

    @staticmethod
    def _read_button(controller: Any, joystick: Any, button_id: str) -> bool:
        if controller is not None:
            try:
                return bool(controller.get_button(SDL_BUTTONS[button_id]))
            except pygame.error:
                return False
        index = RAW_BUTTONS[button_id]
        return index < joystick.get_numbuttons() and bool(joystick.get_button(index))

    @staticmethod
    def _read_axis(controller: Any, joystick: Any, axis: int) -> float:
        if controller is not None:
            value = controller.get_axis(axis)
            if isinstance(value, float) and -1.0 <= value <= 1.0:
                return max(-1.0, min(1.0, value))
            return max(-1.0, min(1.0, float(value) / 32767.0))
        if axis >= joystick.get_numaxes():
            return 0.0
        return max(-1.0, min(1.0, joystick.get_axis(axis)))

    @classmethod
    def _read_trigger(cls, controller: Any, joystick: Any, axis: int) -> float:
        if controller is not None:
            value = controller.get_axis(axis)
            if isinstance(value, float) and -1.0 <= value <= 1.0:
                return max(0.0, min(1.0, value))
            return max(0.0, min(1.0, float(value) / 32767.0))
        if axis >= joystick.get_numaxes():
            return 0.0
        return max(0.0, min(1.0, (joystick.get_axis(axis) + 1.0) / 2.0))

    def _new_device(self, output_type: str) -> Any:
        module = self._load_module()
        if output_type == "DualShock 4":
            return module.VDS4Gamepad()
        return module.VX360Gamepad()

    @staticmethod
    def _xbox_button(module: Any, button_id: str) -> Any:
        names = {
            "a": "XUSB_GAMEPAD_A",
            "b": "XUSB_GAMEPAD_B",
            "x": "XUSB_GAMEPAD_X",
            "y": "XUSB_GAMEPAD_Y",
            "back": "XUSB_GAMEPAD_BACK",
            "guide": "XUSB_GAMEPAD_GUIDE",
            "start": "XUSB_GAMEPAD_START",
            "left_stick": "XUSB_GAMEPAD_LEFT_THUMB",
            "right_stick": "XUSB_GAMEPAD_RIGHT_THUMB",
            "left_shoulder": "XUSB_GAMEPAD_LEFT_SHOULDER",
            "right_shoulder": "XUSB_GAMEPAD_RIGHT_SHOULDER",
            "dpad_up": "XUSB_GAMEPAD_DPAD_UP",
            "dpad_down": "XUSB_GAMEPAD_DPAD_DOWN",
            "dpad_left": "XUSB_GAMEPAD_DPAD_LEFT",
            "dpad_right": "XUSB_GAMEPAD_DPAD_RIGHT",
        }
        return getattr(module.XUSB_BUTTON, names[button_id])

    @staticmethod
    def _ds4_button(module: Any, button_id: str) -> tuple[Any, bool]:
        names = {
            "a": "DS4_BUTTON_CROSS",
            "b": "DS4_BUTTON_CIRCLE",
            "x": "DS4_BUTTON_SQUARE",
            "y": "DS4_BUTTON_TRIANGLE",
            "back": "DS4_BUTTON_SHARE",
            "start": "DS4_BUTTON_OPTIONS",
            "left_stick": "DS4_BUTTON_THUMB_LEFT",
            "right_stick": "DS4_BUTTON_THUMB_RIGHT",
            "left_shoulder": "DS4_BUTTON_SHOULDER_LEFT",
            "right_shoulder": "DS4_BUTTON_SHOULDER_RIGHT",
        }
        if button_id == "guide":
            return module.DS4_SPECIAL_BUTTONS.DS4_SPECIAL_BUTTON_PS, True
        if button_id == "touchpad":
            return module.DS4_SPECIAL_BUTTONS.DS4_SPECIAL_BUTTON_TOUCHPAD, True
        return getattr(module.DS4_BUTTONS, names[button_id]), False

    def _send_report(
        self,
        output_type: str,
        controller: Any,
        joystick: Any,
        button_mapping: dict[str, Optional[str]],
    ) -> None:
        module = self._load_module()
        pressed = {
            key: self._read_button(controller, joystick, key)
            for key in BUTTON_IDS
        }
        x_left = self._read_axis(controller, joystick, pygame.CONTROLLER_AXIS_LEFTX)
        y_direction = -1.0 if output_type == "Xbox 360" else 1.0
        y_left = y_direction * self._read_axis(controller, joystick, pygame.CONTROLLER_AXIS_LEFTY)
        x_right = self._read_axis(controller, joystick, pygame.CONTROLLER_AXIS_RIGHTX)
        y_right = y_direction * self._read_axis(controller, joystick, pygame.CONTROLLER_AXIS_RIGHTY)
        left_trigger = self._read_trigger(controller, joystick, pygame.CONTROLLER_AXIS_TRIGGERLEFT)
        right_trigger = self._read_trigger(controller, joystick, pygame.CONTROLLER_AXIS_TRIGGERRIGHT)

        if output_type == "DualShock 4":
            self._device.reset()
            mapped_buttons = {
                target
                for source, target in button_mapping.items()
                if target is not None and pressed[source]
            }
            dpad_x = int("dpad_right" in mapped_buttons) - int("dpad_left" in mapped_buttons)
            dpad_y = int("dpad_up" in mapped_buttons) - int("dpad_down" in mapped_buttons)
            directions = {
                (0, 0): "DS4_BUTTON_DPAD_NONE",
                (0, 1): "DS4_BUTTON_DPAD_NORTH",
                (1, 1): "DS4_BUTTON_DPAD_NORTHEAST",
                (1, 0): "DS4_BUTTON_DPAD_EAST",
                (1, -1): "DS4_BUTTON_DPAD_SOUTHEAST",
                (0, -1): "DS4_BUTTON_DPAD_SOUTH",
                (-1, -1): "DS4_BUTTON_DPAD_SOUTHWEST",
                (-1, 0): "DS4_BUTTON_DPAD_WEST",
                (-1, 1): "DS4_BUTTON_DPAD_NORTHWEST",
            }
            self._device.directional_pad(getattr(module.DS4_DPAD_DIRECTIONS, directions[(dpad_x, dpad_y)]))
            dpad_ids = {"dpad_up", "dpad_down", "dpad_left", "dpad_right"}
            for target in mapped_buttons:
                if target in dpad_ids:
                    continue
                button, is_special = self._ds4_button(module, target)
                if is_special:
                    self._device.press_special_button(button)
                else:
                    self._device.press_button(button)
            self._device.left_trigger_float(left_trigger)
            self._device.right_trigger_float(right_trigger)
            self._device.left_joystick_float(x_left, y_left)
            self._device.right_joystick_float(x_right, y_right)
        else:
            self._device.reset()
            for source, target in button_mapping.items():
                if source.startswith("dpad_"):
                    target_pressed = (
                        target is not None
                        and pressed[source]
                    )
                else:
                    target_pressed = target is not None and pressed[source]
                if target_pressed and target != "touchpad":
                    self._device.press_button(self._xbox_button(module, target))
            self._device.left_trigger_float(left_trigger)
            self._device.right_trigger_float(right_trigger)
            self._device.left_joystick_float(x_left, y_left)
            self._device.right_joystick_float(x_right, y_right)
        self._device.update()

    def update(
        self,
        enabled: bool,
        output_type: str,
        joystick: Any,
        controller: Any,
        button_mapping: dict[str, Optional[str]],
    ) -> str:
        if not enabled:
            self.close()
            self.status = "Desactivado"
            self._failed_signature = None
            return self.status
        if joystick is None:
            self.close()
            self.status = "Conecta un mando físico para iniciar la salida virtual."
            self._failed_signature = None
            return self.status

        signature = (joystick.get_instance_id(), output_type)
        if signature == self._failed_signature:
            return self.status
        try:
            if signature != self._device_signature:
                self.close()
                self._device = self._new_device(output_type)
                self._device_signature = signature
            self._send_report(output_type, controller, joystick, button_mapping)
            self.status = f"Mando virtual {output_type} activo."
            self._failed_signature = None
        except Exception as error:
            self.close()
            self._failed_signature = signature
            self.status = f"No se pudo crear o actualizar el mando virtual: {error}"
        return self.status
