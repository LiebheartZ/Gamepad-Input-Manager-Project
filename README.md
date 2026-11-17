# GDM — Gamepad Device Manager

GDM es una aplicación portable para Windows que detecta mandos físicos, los identifica de forma persistente y permite **crear salidas virtuales** (Xbox 360 o DualShock 4) mediante HIDMaestro, con **remapeo de controles**, **ocultamiento de dispositivos físicos** con HidHide y **auto-actualización** desde GitHub Releases.

> Interfaz completamente en español. No requiere instalación: es un ejecutable portable.

## ¿Qué hace?

- **Detección de mandos** conectados (USB y Bluetooth) mediante SDL2, con identificación persistente por serie/VID-PID/ruta.
- **Salida virtual**: convierte tu mando físico en un Xbox 360 o DualShock 4 virtual que los juegos reconocen nativamente.
- **Remapeo completo**: perfiles con botones, ejes, zonas muertas, inversión de ejes y **combos de botones** (p. ej. mantener X+Y para cambiar el tipo de salida o apagar el mando).
- **Ocultamiento con HidHide**: esconde el mando físico a otras aplicaciones (para evitar doble entrada) manteniendo el acceso desde GDM, con restauración automática al cerrar.
- **Hot-plug**: detecta conexiones y desconexiones en tiempo real, con inicio automático de salidas al conectar.
- **Tester**: vista en vivo de botones y ejes del mando seleccionado.
- **Extras para Sony**: control del LED del DualShock 4 / DualSense (color por jugador, color manual, según mando virtual) y **apagado por software** del mando Bluetooth
- **Auto-actualización**: descarga e instala nuevas versiones desde GitHub Releases con un clic (o automáticamente al iniciar, configurable).

## Requisitos

- Windows 10/11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (necesario para ejecutar la app)
- Driver **HIDMaestro** y **HidHide** instalados por separado (GDM no los redistribuye; los detecta y guía si faltan)

## Instalación y uso

1. Descarga el `.zip` del último [release](https://github.com/LiebheartZ/Gamepad-Input-Manager-Project/releases).
2. Extrae el contenido en la carpeta que quieras (la app es portable).
3. Ejecuta `GDM.exe`. La primera vez que actives una salida virtual, HIDMaestro instalará su driver y Windows pedirá confirmación UAC.
4. En **Perfiles**, asigna un perfil a tu mando guardado, elige el tipo de mando virtual y pulsa iniciar. El mando físico puede ocultarse automáticamente.

## Actualizaciones

GDM consulta los releases del repositorio de forma silenciosa al iniciar (opción configurable en `Ajustes > Actualizaciones`). Cuando hay una versión nueva muestra un aviso con opciones de ignorar o descargar e instalar; la instalación descarga el `.zip`, cierra la app y se reinstala sola con `robocopy`.

- **Canal Estándar**: solo versiones con nuevo número de función.
- **Canal Nightly**: cualquier versión más reciente, incluidos *prerelease*.

## Estructura del proyecto

- `Views` / `ViewModels`: interfaz WPF y lógica de presentación.
- `Models`: modelos de datos (mandos, perfiles, ajustes, combos).
- `Services`: detección SDL2, salida virtual HIDMaestro, HidHide, LED Sony, apagado Bluetooth, persistencia JSON y actualizador.
- `ThirdParty`: runtime SDL2 2.32.10 y SDK HIDMaestro 2.1.0 con sus licencias.

Las notas técnicas detalladas del desarrollo se conservan en `Otros/README-TECNICO.md` (carpeta de trabajo local, no incluida en el repo público).

## Créditos

- [SDL2](https://www.libsdl.org/) — detección y lectura de mandos.
- [HIDMaestro](https://github.com/nefarius/HIDMaestro) — mandos virtuales Xbox 360 / DualShock 4.
- [HidHide](https://github.com/ViGEm/HidHide) (ViGEm) — ocultamiento de dispositivos HID.

## Licencia

Proyecto personal en desarrollo activo. El código y los binarios de terceros (SDL2, HIDMaestro, HidHide) se distribuyen con sus respectivas licencias en `ThirdParty/`.

---

**Versión actual: 0.16.22** — ver `Ajustes > Acerca de` dentro de la app.