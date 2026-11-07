# GDM — Gamepad Device Manager

Nueva base de GDM para Windows, creada con .NET 10 y WPF. La aplicación usa un único proyecto de producción; el proyecto de pruebas permanece separado.

## Estructura

- `App.xaml` y `App.xaml.cs`: inicio y recursos globales de WPF.
- `Views`: ventanas y vistas XAML.
- `ViewModels`: estado y navegación que consume la interfaz.
- `Models`: modelos y reglas de los datos de GDM.
- `Services/Abstractions`: contratos entre la aplicación y sus servicios.
- `Services/Persistence`: almacenamiento local JSON.
- `Services/Sdl2GamepadDetectionService.cs`: detección de dispositivos conectados mediante SDL2.
- `tests`: pruebas automatizadas.
- `bin`: ejecutable compilado, dentro de `Debug` o `Release`.

La interfaz marca explícitamente como pendientes las funciones que todavía no están implementadas. SDL2 detecta y actualiza la lista de dispositivos conectados y accesibles al abrir Mandos y mediante reconciliación periódica. Si HidHide oculta un mando a GDM, la vista lo indica y recomienda permitir GDM sin desactivar la protección global. La persistencia JSON de ajustes y mandos está implementada, pero guardar mandos todavía no está conectado a una acción de usuario. Perfiles reutilizables y la consulta de dispositivos emparejados/cercanos de Windows siguen pendientes.

## Compilar y probar

Desde esta carpeta:

```powershell
dotnet build GDM.sln
dotnet test GDM.sln
dotnet run --project GDM.csproj
```

El ejecutable se genera en `bin/Debug/GDM.exe` al compilar en modo Debug.

## Decisiones iniciales

- UI: WPF; ventanas y lógica de presentación se organizan en carpetas distintas.
- Organización: un solo proyecto de producción con carpetas por categoría, más un proyecto de pruebas.
- Framework: .NET 10 LTS.
- Persistencia prevista: JSON con `System.Text.Json`; no se usa SQLite.
- SDL: SDL2 mediante `Sayers.SDL2.Core` 1.0.11, con binarios nativos de Windows x64 incluidos por el paquete. Esta dependencia se utiliza para enumerar mandos conectados; no enumera emparejados o dispositivos Bluetooth cercanos.
- El paquete SDL proporciona binarios nativos x64; la aplicación actual se compila para Windows x64.
- Datos de usuario: `%LocalAppData%\\GDM`, bajo `Settings` y `Controllers`.
- La pestaña `Registro` muestra errores y eventos de ejecución; el archivo se guarda en `%LocalAppData%\GDM\Logs\gdm.log`. Los errores de compilación deben consultarse en la salida de compilación de VS Code, ya que ocurren antes de iniciar la aplicación.
- La ventana inicia en 800 × 600 y guarda/restaura su último tamaño normal al cerrar y volver a abrir.
- La persistencia versiona los esquemas, escribe mediante un archivo temporal y reporta JSON dañado o versiones incompatibles en lugar de reemplazarlos silenciosamente.
- Cada mando guardado tiene un JSON individual y el catálogo usa nombres de archivo derivados del ID interno, no del nombre visible.

La identidad de un mando entre USB y Bluetooth depende de los identificadores expuestos por el dispositivo y Windows. GDM no debe asociar ni aplicar perfiles automáticamente cuando la evidencia no sea suficientemente fiable.
