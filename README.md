# GDM â€” Gamepad Device Manager

GDM es una aplicaciÃ³n portable para Windows que detecta mandos fÃ­sicos, los identifica de forma persistente y permite **crear salidas virtuales** (Xbox 360 o DualShock 4) mediante HIDMaestro, con **remapeo de controles**, **ocultamiento de dispositivos fÃ­sicos** con HidHide y **auto-actualizaciÃ³n** desde GitHub Releases.

> Interfaz completamente en espaÃ±ol. No requiere instalaciÃ³n: es un ejecutable portable.

## Â¿QuÃ© hace?

- **DetecciÃ³n de mandos** conectados (USB y Bluetooth) mediante SDL2, con identificaciÃ³n persistente por serie/VID-PID/ruta.
- **Salida virtual**: convierte tu mando fÃ­sico en un Xbox 360 o DualShock 4 virtual que los juegos reconocen nativamente.
- **Remapeo completo**: perfiles con botones, ejes, zonas muertas, inversiÃ³n de ejes y **combos de botones** (p. ej. mantener X+Y para cambiar el tipo de salida o apagar el mando).
- **Ocultamiento con HidHide**: esconde el mando fÃ­sico a otras aplicaciones (para evitar doble entrada) manteniendo el acceso desde GDM, con restauraciÃ³n automÃ¡tica al cerrar.
- **Hot-plug**: detecta conexiones y desconexiones en tiempo real, con inicio automÃ¡tico de salidas al conectar.
- **Tester**: vista en vivo de botones y ejes del mando seleccionado.
- **Extras para Sony**: control del LED del DualShock 4 / DualSense (color por jugador, color manual, segÃºn mando virtual) y **apagado por software** del mando Bluetooth
- **Auto-actualizaciÃ³n**: descarga e instala nuevas versiones desde GitHub Releases con un clic (o automÃ¡ticamente al iniciar, configurable).

## Requisitos

- Windows 10/11 (x64)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (necesario para ejecutar la app)
- Driver **HIDMaestro** y **HidHide** instalados por separado (GDM no los redistribuye; los detecta y guÃ­a si faltan)

## InstalaciÃ³n y uso

1. Descarga el `.zip` del Ãºltimo [release](https://github.com/LiebheartZ/Gamepad-Input-Manager-Project/releases).
2. Extrae el contenido en la carpeta que quieras (la app es portable).
3. Ejecuta `GDM.exe`. La primera vez que actives una salida virtual, HIDMaestro instalarÃ¡ su driver y Windows pedirÃ¡ confirmaciÃ³n UAC.
4. En **Perfiles**, asigna un perfil a tu mando guardado, elige el tipo de mando virtual y pulsa iniciar. El mando fÃ­sico puede ocultarse automÃ¡ticamente.

## Actualizaciones

GDM consulta los releases del repositorio de forma silenciosa al iniciar (opciÃ³n configurable en `Ajustes > Actualizaciones`). Cuando hay una versiÃ³n nueva muestra un aviso con opciones de ignorar o descargar e instalar; la instalaciÃ³n descarga el `.zip`, cierra la app y se reinstala sola con `robocopy`.

- **Canal EstÃ¡ndar**: solo versiones con nuevo nÃºmero de funciÃ³n.
- **Canal Nightly**: cualquier versiÃ³n mÃ¡s reciente, incluidos *prerelease*.

## Estructura del proyecto

- `Views` / `ViewModels`: interfaz WPF y lÃ³gica de presentaciÃ³n.
- `Models`: modelos de datos (mandos, perfiles, ajustes, combos).
- `Services`: detecciÃ³n SDL2, salida virtual HIDMaestro, HidHide, LED Sony, apagado Bluetooth, persistencia JSON y actualizador.
- `ThirdParty`: runtime SDL2 2.32.10 y SDK HIDMaestro 2.1.0 con sus licencias.

Las notas tÃ©cnicas detalladas del desarrollo se conservan en `Otros/README-TECNICO.md` (carpeta de trabajo local, no incluida en el repo pÃºblico).

## CrÃ©ditos

- [SDL2](https://www.libsdl.org/) â€” detecciÃ³n y lectura de mandos.
- [HIDMaestro](https://github.com/nefarius/HIDMaestro) â€” mandos virtuales Xbox 360 / DualShock 4.
- [HidHide](https://github.com/ViGEm/HidHide) (ViGEm) â€” ocultamiento de dispositivos HID.

## Licencia

Proyecto personal en desarrollo activo. El cÃ³digo y los binarios de terceros (SDL2, HIDMaestro, HidHide) se distribuyen con sus respectivas licencias en `ThirdParty/`.

---

**VersiÃ³n actual: 0.16.23** â€” ver `Ajustes > Acerca de` dentro de la app.
