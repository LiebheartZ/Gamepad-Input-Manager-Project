# Traspaso completo de contexto: GDM

Este documento existe para que una nueva sesión de trabajo pueda continuar el proyecto con el mayor contexto posible y sin repetir decisiones ya tomadas. Es un resumen técnico e histórico, no una copia literal de la conversación. La fuente de verdad para el estado del código siempre es el árbol de trabajo actual.

## Instrucción para la próxima sesión

1. Lee este documento y `README.md` antes de proponer o realizar cambios.
2. Inspecciona los archivos actuales relacionados con la tarea. Este documento puede quedar desactualizado con nuevos cambios.
3. Conserva los comportamientos que el usuario ha validado, especialmente el hotplug SDL y el diseño actual del mando visual.
4. El usuario suele querer que se implemente el siguiente paso, no recibir solo una propuesta, salvo que pida explícitamente análisis o explicación.
5. Comunícate en español. Para cambios de UI, utiliza las capturas del usuario como referencia directa y valida visualmente cuando haya una captura disponible.
6. Ejecuta las pruebas con una carpeta de salida separada cuando una instancia de GDM pueda estar abierta o cuando el ejecutable habitual esté bloqueado.

## Identidad y objetivo del producto

GDM significa **Gamepad Device Manager**. Es una aplicación de escritorio para Windows que está siendo reconstruida desde cero, basada en los documentos de arquitectura y de la pestaña Mandos entregados por el usuario al inicio del proyecto. El objetivo es administrar mandos, persistir sus registros/configuración y facilitar su diagnóstico.

El usuario valora especialmente:

- Detección de conectar/desconectar en caliente sin reiniciar.
- Guardar y recuperar correctamente el registro/configuración de un mando.
- No atribuir una identidad física incorrecta ni cargar configuración en otro dispositivo por una coincidencia débil.
- Una interfaz en español y usable, con tema configurable.
- La pestaña `Tester` con un mando ilustrado cuyas entradas se destaquen en tiempo real.

## Tecnologías y organización actual

- Sistema objetivo: Windows x64.
- UI: WPF/XAML.
- Framework: .NET 10 (`net10.0-windows`).
- Lenguaje: C# con nullable e implicit usings habilitados.
- SDL: paquete NuGet `Sayers.SDL2.Core` 1.0.11, con destino x64.
- Persistencia: JSON con `System.Text.Json`; no se usa SQLite.
- Proyecto de producción único, organizado por responsabilidad, y un proyecto de pruebas xUnit.

Archivos y carpetas principales:

- `GDM.sln`: solución.
- `GDM.csproj`: aplicación WPF, `WinExe`, .NET 10 Windows y x64.
- `App.xaml`, `App.xaml.cs`: recursos globales, temas, composición inicial de servicios y manejo global de errores.
- `Views/MainWindow.xaml`: shell de navegación superior y contenido de las secciones.
- `Views/MainWindow.xaml.cs`: ciclo de vida de ventana, integración de eventos UI y confirmaciones.
- `ViewModels/MainWindowViewModel.cs`: navegación, refresco de dispositivos, persistencia coordinada, logging y ciclo de Tester.
- `ViewModels/ControllerListItem.cs`: elemento unificado que representa mando conectado/guardado en Mandos.
- `ViewModels/SettingsViewModel.cs`: estado/comandos de Ajustes.
- `ViewModels/TesterViewModel.cs`: selección, snapshot SDL, estado y propiedades para el mando visual.
- `Models/`: `AppSettings`, `ConnectedGamepad`, `ControllerRecord`, `DiagnosticLogEntry`, `GamepadInputSnapshot`.
- `Services/Abstractions/`: contratos de logging, settings, mandos, detección, lectura de entrada y almacenamiento.
- `Services/Persistence/`: logger, repositorios JSON, escritura JSON atómica y resolución de rutas locales.
- `Services/Sdl2GamepadDetectionService.cs`: integración nativa SDL de detección y lectura de entradas.
- `tests/`: pruebas de persistencia, ViewModels, Settings y Tester.
- `README.md`: orientación y descripción de características actuales.
- `CONTEXTO_SESION_GDM.md`: este traspaso detallado.

Los documentos originales de especificación se consultaron al inicio del trabajo. Actualmente no aparecen con el patrón `Document*.md` en la raíz activa del proyecto; si hace falta releerlos en otra sesión, buscar primero en la carpeta padre o en el ZIP existente, sin asumir que están en esta carpeta.

## Historia del trabajo y decisiones tomadas

### 1. Análisis inicial y creación

- El usuario pidió leer las especificaciones completas y presentar análisis/arquitectura antes de programar. Se propuso WPF, .NET 10, SDL2 para mandos conectados y persistencia JSON local.
- Tras su aprobación se creó la solución y la base de la aplicación.
- La organización inicial separaba capas/proyectos; a petición del usuario se simplificó a un proyecto de producción con carpetas `Views`, `ViewModels`, `Models` y `Services`, conservando `tests`.
- La interfaz pasó a navegación superior con logo, etiqueta de build e icono `cover.ico`.
- El tamaño inicial de ventana es 800×600 y se guarda/restaura el tamaño normal.

### 2. Detección de dispositivos y HidHide

- La detección usa SDL2 para dispositivos conectados/accesibles, no para descubrir dispositivos Bluetooth cercanos ni todos los emparejados de Windows.
- En el caso de DualShock 4, SDL inicialmente no mostraba el mando porque DS4Windows/HidHide podía ocultarlo a GDM. El usuario comprobó que el dispositivo aparecía al desactivar el software que lo ocultaba.
- Se añadió una explicación que aconseja permitir GDM en HidHide en lugar de desactivar la protección global.
- El usuario confirmó repetidamente que la detección en caliente funciona: el mando desaparece al desconectarlo y aparece al conectarlo sin reiniciar. Este comportamiento es esencial. No modificar la arquitectura/ciclo de vida SDL de manera incidental.

### 3. Registro de diagnóstico

- Se creó un registro de ejecución para errores y eventos de runtime.
- `AppLogService` escribe en `%LocalAppData%\GDM\Logs\gdm.log`, mantiene un buffer en memoria de hasta 1000 entradas y emite eventos.
- Se añadieron `DiagnosticLogEntry`, `IAppLogService` y `LogFilePath` al contrato de almacenamiento.
- `App.xaml.cs` conecta excepciones no controladas de Dispatcher/AppDomain/tareas y registra errores de inicialización/operación.
- La pestaña `Registro` muestra información de runtime.
- Importante: un error de compilación ocurre antes de iniciar la app y no puede aparecer en su registro de runtime. Para esos errores hay que revisar la salida de compilación de VS Code/MSBuild.

### 4. Guardado y recuperación de mandos

- Los registros se cargan al iniciar y los mandos conectados/guardados se presentan en una lista unificada sin duplicados.
- Los mandos guardados siguen apareciendo cuando están desconectados.
- El usuario pidió mantener el hotplug existente y no arriesgar la detección SDL.
- HidHide puede provocar que SDL elimine y vuelva a enumerar una instancia del mando. El identificador SDL cambia aunque la ruta física HID sea la misma; en ese caso se debe reanudar la misma salida virtual con la nueva clave y dar tiempo a la reconciliación periódica antes de declarar perdida la entrada.
- Con salidas virtuales activas, la reconciliación de dispositivos usa un intervalo de 500 ms para reducir carreras de hotplug. Tras HidHide, el mismo mando se puede reconocer por ruta física, serial+VID/PID o por el modelo exacto si ya fue asociado inequívocamente al registro guardado.
- La categoría `AutoHide diagnóstico` del registro incluye identidad SDL/HID completa en cada cambio de dispositivos, lectura inicial, confirmaciones temporizadas de entrada/presencia, resultado del ocultamiento y decisiones de reasociación. Las comprobaciones de entrada están limitadas por intervalo para no registrar cada tick del bucle de entrada.
- La identidad persistente prioriza serial único y compatibilidad de VID/PID.
- El índice/ID de instancia SDL es temporal, no una identidad persistente.
- Si no hay serial, nombre y VID/PID solo identifican potencialmente el modelo, no una unidad física. No se debe fingir certeza y asociar registros indiscriminadamente.
- El matching sin serial es conservador: solo asociaciones inequívocas y, cuando se trata de la misma sesión, el vínculo SDL de sesión se considera temporal; no se persiste como identidad.
- Antes de guardar se vuelve a comprobar que el mando continúa conectado.
- Eliminación de registro pide confirmación; no elimina ni modifica el emparejamiento de Windows.
- Los errores de catálogo/JSON se informan y registran; no sobrescribir silenciosamente datos dañados o esquemas incompatibles.
- La escritura JSON se hace mediante archivo temporal y movimiento atómico.

Rutas de almacenamiento habituales:

- `%LocalAppData%\GDM\Settings\app-settings.json`
- `%LocalAppData%\GDM\Controllers\controllers-index.json`
- `%LocalAppData%\GDM\Controllers\controller_<id>.json`
- `%LocalAppData%\GDM\Profiles\profiles-index.json`
- `%LocalAppData%\GDM\Profiles\profile_<id>.json`
- `%LocalAppData%\GDM\Logs\gdm.log`

La pestaña `Perfiles` implementa alta, edición de nombre/descripción y mapas de botones/ejes, duplicado, borrado y asignación a mandos guardados. `ControllerProfile` almacena mapas origen→destino; las entradas no editadas conservan el mapeo SDL estándar y se aplican únicamente a la salida virtual asociada al mando que tiene asignado ese perfil. El `ProfileId` se persiste en `ControllerRecord`. El borrado se impide mientras el perfil está en uso. Cada mando guardado dispone de su propia salida virtual Xbox 360, inicio/detención independiente y una preferencia `AutoStartVirtualGamepad` en `ControllerSettings`; varias salidas pueden ejecutarse a la vez. Los fallos se comunican en la pestaña y se registran. La lógica de perfiles y salidas reside en `ProfilesViewModel` para facilitar el rediseño futuro.

### 5. Versionado

- La versión inicial contabilizada fue `0.1.20`: 20 correcciones de problemas distintas documentadas en el historial disponible. La versión actual es `0.16.20`.
- `0.1.31` agrega evidencia de diagnóstico para whitelist/lista negra de HidHide y limita la repetición de confirmaciones HID exitosas. `0.2.0` incorpora un acceso desde Mandos a `joy.cpl` para probar los mandos con la ventana de Dispositivos de juego de Windows. `0.3.0` añade por mando la opción de mantener oculto el dispositivo al cerrar GDM y preservar esa selección en el diario de recuperación para los siguientes inicios.
- **REGLA DE VERSIONADO (definida por el usuario, no cambiar automáticamente):** el primer componente cambia ante una renovación drástica del programa; el segundo (minor) SOLO lo incrementa el usuario cuando él lo indique explícitamente, y en ese caso el tercero se reinicia a 0; el tercero (patch) aumenta en 1 por CADA función nueva o arreglo que se realice. El asistente nunca debe subir el segundo componente por iniciativa propia.
- La interfaz muestra `VERSIÓN`, un ID corto derivado del identificador del módulo compilado y la fecha/hora local del artefacto.
- El ID y la fecha identifican el ejecutable compilado; no modifican el número de versión.
- Una instancia nueva inicia en Inicio; la reactivación de una instancia existente muestra primero un aviso en el ejecutable secundario y luego solicita la restauración de la ventana sin cambiar la pestaña seleccionada. Si la solicitud no se puede completar, muestra un aviso adicional de error.

### 6. Temas y controles WPF

- La pestaña `Ajustes` permite Sistema, Claro y Oscuro, guardar preferencia y restaurar valores predeterminados.
- Sistema sigue la preferencia del sistema operativo; ante ausencia de un valor, cae a tema claro.
- Cambiar temas modificando/congelando pinceles ya provocó una excepción al iniciar. La solución fue reemplazar recursos de pincel en lugar de mutarlos.
- Se corrigieron estilos de botones y ComboBox, incluida una plantilla propia para hacer que el selector tenga fondo/texto adecuados y que toda el área del control responda al clic.
- El usuario confirmó que el selector finalmente quedó bien.
- No revertir el reemplazo dinámico de recursos ni la plantilla de ComboBox sin comprender sus motivos.

### 7. Pestaña Tester y mando visual (estado más reciente)

- El usuario pidió una pestaña llamada exactamente `Tester`, no “Pruebas”, con mandos a la izquierda e información/entradas a la derecha.
- SDL ofrece lectura en vivo; Tester usa polling aproximadamente cada 50 ms cuando no hay salida virtual, y el sondeo de todas las salidas virtuales activas pasa a 8 ms. La reconciliación de dispositivos conectados tiene su propio temporizador (aproximadamente cada 3 s), que permanece activo cuando hay mandos configurados para iniciar automáticamente.
- Las llamadas SDL se hacen desde el hilo propietario del servicio; el `DispatcherTimer` mantiene las lecturas en el hilo UI.
- Si SDL reconoce un GameController, se muestran ejes/botones normalizados y un diagrama mapeado. Si no, se presentan índices raw sin inventar etiquetas semánticas.
- En desconexión/Dispose se deben liberar tanto handles GameController como Joystick. Cuidar el propietario/hilo de SDL.
- En la lista de mandos de Tester la selección ocurre solo al presionar y soltar en el mismo elemento; mantener el botón y arrastrar sobre otras filas no debe cambiar la selección. Se interceptan `PreviewMouseLeftButtonDown` y `PreviewMouseLeftButtonUp` en `MainWindow.xaml` y `MainWindow.xaml.cs`, seleccionando en MouseUp solo si el elemento inicial y final coinciden.
- La UI muestra GUID, VID/PID, ruta y serial si SDL los proporciona. Firmware y batería se indican como no disponibles por la API actual.
- El diagrama se implementa en XAML, no usa el sprite de referencia. El usuario prefirió finalmente esta ilustración vectorial y confirmó: **“Ahora si se ve perfecto”**.
- No rehacer ni mover controles del diagrama a menos que el usuario lo solicite expresamente.
- El dibujo se encuentra en `Views/MainWindow.xaml`, sección `Entradas en tiempo real`; el cuerpo es un `Path`, los controles son `Border`/`Ellipse`, las posiciones están en coordenadas `Canvas.Left`/`Canvas.Top`.
- Tamaño de Viewbox/Canvas exterior: 460×350; el Canvas del diagrama tiene 460×330 y está en `Canvas.Top=8`. No volver a aplicar escalado vertical: antes hizo que la silueta se viera estirada.
- La silueta actual corresponde a la referencia de gamepad simétrico con hombros, asas inferiores, cruceta izquierda, dos sticks y botones ABXY a la derecha.
- La última solicitud visual fue: cruceta y ABXY más arriba y acercados al centro, cruceta alineada verticalmente con el centro del grupo ABXY; sticks algo más juntos/arriba; botones Select/Start/Home más abajo. El usuario luego confirmó que quedó perfecto.
- Últimas posiciones relevantes (coordenadas en el Canvas interno; verificar código antes de tocar):
  - Cruceta: centro aproximado `(102, 111)`; piezas entre `Canvas.Left=66..102`, `Canvas.Top=75..135`.
  - ABXY: centro aproximado `(360, 111)`; centros con `Y=75, 111, 147, 111` y X según el botón.
  - Stick izquierdo: centro aproximado `(172, 186)`; stick derecho `(288, 186)`.
  - Puntos móviles se calculan en `TesterViewModel`: `LeftStickDotLeft = 166 + LeftStickX * 24`, `LeftStickDotTop = 180 + LeftStickY * 24`, derecho `282 + X*24`, `180 + Y*24`. Deben permanecer centrados en los discos al cambiar su posición.
  - Controles centrales: `Select` y `Start` en torno a Y=99; `Home` en torno a Y=63.
- Los estados del diagrama se derivan de nombres SDL normalizados. Botones especiales: `Atrás / Share`, `Guía`, `Inicio / Options`. Verifica los nombres de mapeo antes de alterar bindings.
- La lectura mapeada consulta `SDL_GameControllerHasAxis/HasButton` para enumerar únicamente controles lógicos presentes en el mapping; SDL nativo incluido por Sayers 1.0.11 es 2.0.20. La lectura física cuenta ejes y botones mediante `SDL_JoystickNumAxes/NumButtons`, incluso en mandos mapeados.
- El snapshot expone `PhysicalAxisCount`/`PhysicalButtonCount`. La sección Entradas en tiempo real debe enseñar este análisis junto con los conteos de controles mapeados y listas de ejes/botones.
- Se ampliaron los botones lógicos reconocidos hasta `SDL_CONTROLLER_BUTTON_TOUCHPAD` (índice 20): Misc1, cuatro palas y clic del panel táctil; las listas se filtran por controles realmente mapeados. Un mando raw sigue mostrando sus botones físicos como `Botón N` sin asignación semántica.
- El diagrama ahora debe reflejar L3/R3 (pulsación de sticks), L2/R2 con la magnitud analógica de sus ejes, L1/R1, PS/Guide, Select/Back, Start/Options y panel táctil/Misc1 cuando SDL los mapea. No se puede afirmar significado Touchpad si SDL solo ofrece una entrada raw sin mapping.
- Las salidas virtuales se controlan por mando físico guardado en `Perfiles`; cada salida usa HIDMaestro 2.1.0. El tipo de mando virtual (`xbox360` o `dualshock4`, definido en `Models/VirtualGamepadTypes.cs`) se guarda en `ControllerProfile.VirtualGamepadType`; los mandos sin perfil y las salidas de `Tester` usan `AppSettings.DefaultVirtualGamepadType` (Ajustes > Mandos). HIDMaestro resuelve `xbox360`→`xbox-360-wired` (o `xbox-360-type2` si `AppSettings.Xbox360ProfileVariant` = `type2`, elegible en Ajustes > Mandos y consultado por `HidMaestroVirtualGamepadService` en cada inicio vía callback) y `dualshock4`→`dualshock-4-v2`. Cambiar el tipo reinicia las salidas activas afectadas; los destinos de mapeo incompatibles se filtran en `GamepadControlCatalog` y se descartan al emitir el estado. Solo se inician con un mapeo estándar SDL, los mapas del perfil asignado se aplican a botones/ejes y los controles sin override conservan el mapeo estándar. La opción de inicio automático se guarda por mando y opera cuando la monitorización de conexiones está activa.
- `MainWindowViewModel.IsVirtualGamepadBusy` agrega `ProfilesViewModel.IsAnyVirtualGamepadBusy` (cualquier `VirtualGamepadControllerViewModel.IsStarting`, notificado al padre vía suscripción a `PropertyChanged`) y `TesterViewModel.IsStartingVirtualGamepad`; `SwitchVirtualGamepadTypeAsync` también marca `SetStarting` durante el reinicio por combo. El panel superior de `MainWindow.xaml` muestra una barra indeterminada y `VirtualGamepadBusyMessage` (nombre del mando en proceso o la fuente del Tester) mientras dura cualquier operación de HIDMaestro.
- `ControllerProfile.TypeMappings` guarda un `ControllerProfileMapping` (botones, ejes, inversiones, zonas muertas) por tipo de mando virtual; los campos planos `ButtonMappings`/`AxisMappings`/`AxisInversions`/`DeadZones` se conservan como espejo del tipo principal del perfil y como fallback para perfiles antiguos sin `TypeMappings` (`GetMappingsFor` devuelve la config plana para cualquier tipo mientras `TypeMappings` esté vacío). `WithMappingsFor(type)` devuelve un clon del perfil con los campos planos sustituidos por los del tipo pedido; el sondeo (`ReadVirtualGamepadInputsAsync`), `StartVirtualGamepadAsync` y `SwitchVirtualGamepadTypeAsync` lo usan con el tipo en ejecución (`GetRunningVirtualGamepadType`), así los cambios de remapeo se aplican en vivo y cada tipo conserva su configuración al cambiar. En el editor, `_typeMappingDrafts` retiene ediciones no guardadas por tipo: `DraftVirtualGamepadType` captura las filas del tipo anterior (`CaptureMappingDraft`) y carga las del nuevo (`LoadControlMappingRows`); `SaveAsync` escribe ambos tipos en `TypeMappings` y refleja el tipo principal en los campos planos. `AssignAsync` reinicia la salida en ejecución si el tipo del perfil recién asignado difiere del tipo activo.
- `AppSettings.DefaultProfileId` (Ajustes > Mandos, «Sin perfil» por defecto) se asigna automáticamente al `ProfileId` de cada mando recién guardado en `SaveControllerAsync`; si el perfil ya no existe al guardar, el mando queda sin perfil. La asignación manual desde Perfiles no se ve afectada. Al completar el inicio, `ProfilesViewModel.EnsureBuiltinProfileAsync` crea el perfil «Predeterminado» si no existe (nombre comparado sin mayúsculas) y, solo en ese caso y si `DefaultProfileId` es null, `SettingsViewModel.SetDefaultProfileIdAsync` lo fija como predeterminado persistido.
- Los perfiles pueden definir combos de botones (`ControllerProfile.Combos`, editados en Perfiles > Combos de botones). `GamepadComboEvaluator` evalúa cada tick de `ReadVirtualGamepadInputsAsync` solo para salidas virtuales con perfil asignado: dispara al instante o tras mantener los botones 1-10000 ms, una vez por pulsación. Las acciones `buttons` agregan destinos extra al estado (`SubmitInputSnapshot` con `additionalTargetButtons` → `HidMaestroGamepadMapper`), `stopOutput` detiene la salida como el botón manual, y `switchType` reinicia la sesión con `VirtualGamepadTypes.Other(tipo)` sin restaurar el mando oculto. El tipo en ejecución se consulta con `IVirtualGamepadService.GetRunningVirtualGamepadType`.
- Los mandos virtuales creados por HIDMaestro se identifican con `ConnectedGamepad.IsVirtualGamepad`, calculado en `Sdl2GamepadDetectionService` mediante `HidMaestroDeviceIdentifier`: la ruta HID de SDL (`\\?\HID#<enum>#<instancia>#{GUID}`) se convierte en instance ID (`HID\<enum>\<instancia>`) y se lee `HardwareID` en `HKLM\SYSTEM\CurrentControlSet\Enum\<instanceId>`; los virtuales incluyen el ID `HID\HIDMaestro` que HIDMaestro inyecta y los físicos no. La ruta SDL no contiene `HIDMAESTRO` y el devnode `SWD\HIDMAESTRO` no es ancestro del devnode HID (es hermano), por eso no sirve ni el texto de la ruta ni el árbol de padres PnP. Aparecen en `Mandos` y `Tester` con la etiqueta `Mando virtual`, pero `MainWindowViewModel` los excluye de los matchers de mandos guardados (`FindUniqueSerialMatch`, `FindSessionDeviceMatch`, `FindUniqueModelMatch`), de `SaveControllerAsync` (también `ControllerListItem.CanSave`), de las fuentes de salida de Tester y de `HidHide.UpdateDevices`. `AppSettings.ShowVirtualGamepadsInAllTabs` (Ajustes > Debug) los muestra de nuevo en las listas filtradas; al cambiarlo se refrescan HidHide y los items sin reiniciar la app.
- La pestaña `Perfiles` se organizó en subpestañas por mando guardado (`TabControl` sobre `Profiles.VirtualGamepads`, cabecera con `DisplayName` + estado de conexión). Cada pestaña de mando contiene un `TabControl` anidado con tres categorías: `Mando virtual` (tarjeta de estado, Iniciar/Detener e interruptores de inicio automático/ocultar/mantener oculto), `Perfil` (selector de asignación + `ProfileEditorView`) y `Otros` (marcador para opciones futuras por mando). El editor vive en `Views/ProfileEditorView` (DataContext = `ProfilesViewModel`) para reutilizarlo también en el estado vacío «Sin mandos guardados». `ProfilesViewModel.SelectedVirtualGamepad` sincroniza la pestaña con `SelectedController`; al elegir un perfil en el combo también se carga en `SelectedProfile` para editarlo. `HasVirtualGamepads`/`ShowNoVirtualGamepads` controlan el estado vacío.
- El servicio virtual no lee SDL desde un hilo de fondo: `Sdl2GamepadDetectionService` comprueba afinidad al hilo propietario. El sondeo se ejecuta en Dispatcher y remite a cada salida únicamente el snapshot de su mando físico fuente, junto con el perfil asignado. La lectura continúa también al navegar a otra pestaña; se detiene por salida al desconectar su mando, y globalmente al cerrar GDM.
- Crear cada controlador requiere administrador. Si GDM no está elevado, se pide confirmación y UAC, se inicia otra instancia con la identidad del dispositivo y se cierra la instancia normal. La nueva instancia intenta reencontrar el dispositivo por ruta, serial o combinación GUID/VID/PID/nombre; si el matching no es único, no elige otro mando al azar.
- GDM se mantiene como aplicación portable, sin instalador. Distribuir la carpeta completa de salida del build junto con `HIDMaestro.Core.dll`, SDL2 y los archivos de licencia/avisos; el equipo debe instalar por separado .NET 10 Desktop Runtime x64. HIDMaestro instala su controlador al activar la emulación por primera vez y no lo elimina al borrar la carpeta portable.
- La emulación básica no oculta ni desconecta el mando físico; algunos juegos pueden enumerar ambos dispositivos. Salida de vibración/sensores, edición de mappings y aplicación de perfiles siguen pendientes. No se ha instalado el driver ni validado el flujo con hardware real en este entorno.

## Identidad y límites técnicos

- Un serial vacío o no expuesto por SDL no debe reemplazarse con nombre/VID/PID como si fuera identidad de unidad.
- VID/PID y nombre pueden identificar un modelo, pero dos unidades iguales pueden compartirlos.
- SDL enumera dispositivos conectados y accesibles; no es una API de dispositivos emparejados/cercanos.
- La pestaña HidHide usa las IOCTL 2050/2051 de la lista negra persistente para funcionar con el controlador estable. Antes de agregar entradas, escribe un diario de recuperación bajo `%LocalAppData%\GDM\HidHide`; al cerrar normalmente restaura solo las entradas agregadas por GDM, y al siguiente inicio recupera las pendientes. No elimina entradas preexistentes ni cambia el filtro global. Si la restauración falla, informa y pide confirmación antes de cerrar, conservando el diario para el siguiente intento. Un cierre forzado puede dejar el mando oculto hasta el siguiente inicio. Para que GDM pueda seguir leyendo el mando, se agrega la ruta del ejecutable a la whitelist persistente si no estaba. No se instala ni redistribuye HidHide.
- En la pestaña HidHide, GDM conserva la fila de un mando que ocultó aunque SDL deje de enumerarlo, para que se pueda volver a mostrar desde la aplicación. En Perfiles, `AutoHidePhysicalGamepad` es una preferencia por mando: solo se aplica luego del inicio exitoso de la salida virtual, y GDM muestra de nuevo el mando al detener esa salida. `AppSettings.RestoreGamepadsOnExit` (Ajustes > Mandos, activado por defecto) decide si los mandos ocultados se restauran al cerrar; desactivado, todos permanecen ocultos. `GetAutoHiddenDeviceExitPolicies` combina ambas opciones (`porMando || !global`).
- Si SDL deja de enumerar una fuente mientras su salida sigue activa y GDM la ocultó automáticamente, Perfiles comprueba la presencia PnP del devnode HID con `CM_Locate_DevNodeW`. Mientras Windows reporte presencia, mantiene la salida/asociación; tras tres comprobaciones ausentes separadas por al menos 250 ms, detiene la salida y limpia el ocultamiento. Así se diferencia la pérdida de visibilidad de HidHide de la desconexión física.
- SDL 2.0.20 (provisto por Sayers.SDL2.Core 1.0.11) no exportaba `SDL_JoystickPath` — API introducida en SDL 2.24.0 — y el servicio silenciaba el `EntryPointNotFoundException`, dejando `ConnectedGamepad.DevicePath` vacío. Se reemplaza solo el `SDL2.dll` del paquete por el runtime oficial SDL 2.32.10 x64, se copia al output después del contenido NuGet, se incluye el aviso oficial de distribución y la consulta de ruta falla explícitamente si falta el export.
- Se implementó CRUD, asignación de perfiles, editor de remapeo botón/eje, inversión de ejes de sticks y zonas muertas configurables para ambos sticks. Los cambios se aplican solo a la salida virtual correspondiente; la entrada física se conserva. La pestaña HidHide y su recuperación por diario están implementadas; la operación de IOCTL y compatibilidad con interfaces HID compuestas requieren validación con HidHide instalado y hardware real.
- No se exponen firmware y batería con el acceso SDL actual.
- La UI solo está disponible en español; no presentar actualización automática como funcional si no hay servicio asociado.
- LED de mandos Sony: `ControllerSettings.Led` (`ControllerLedSettings`: `Mode` off/player/manual/virtual, `ManualColor` RRGGBB, `Brightness` 0-255, `PlayerNumber` 1-4, `PlayerColors` por jugador) se edita en la subpestaña `Otros` de cada mando y se aplica con `ISonyControllerLedService` (`SonyControllerLedService`). La aplicación resuelve el RGB efectivo en `ProfilesViewModel.ApplyLedStateAsync` (modo virtual = verde Xbox / azul DS4 según `GetRunningVirtualGamepadType`) y la deduplica por firma en el servicio. Soporte por VID/PID en `SonyControllerLedSupport`: DS4 `054C:05C4/09CC` (informe USB `0x05` por WriteFile, BT `0x11` por HidD_SetOutputReport) y DualSense `054C:0CE6/0DF2` (USB `0x02`, BT `0x31` + secuencia + CRC32 zlib con semilla `0xA2`, igual que hid-playstation.c). USB vs BT se decide por `InputReportByteLength == 64` vía `HidP_GetCaps`. Pendiente validar con hardware real ambos transportes.
- Apagado de mandos físicos: `ControllerSettings.PowerOffIdleSeconds` (0=off, presets 1min-2h en `ControllerPowerOff`) se configura en `Otros` junto al botón manual y al combo `powerOff` (`ComboActionTypes.PowerOff`, acción "Apagar el mando físico"). `IControllerPowerOffService`/`ControllerPowerOffService` localiza el dispositivo parseando la ruta SDL `\\?\HID#A#B#{guid}` → devnode `HID\A\B` (SetupDiGetClassDevs + SetupDiGetDeviceInstanceId), sube padres con `CM_Get_Parent` hasta un ancestro `BTHENUM\`/`BTHLEDEVICE\` y extrae la MAC de 12 hex del ID de instancia (patrones `DEV_`, `&0&`, `REV&xxxx_`). Desconexión real con `IOCTL_BTH_DISCONNECT_DEVICE` (0x41000C) sobre el handle de `BluetoothFindFirstRadio`: payload = `BitConverter.GetBytes(ulong)` de la MAC canónica (equivale al BTH_ADDR de DsHidMini `{0,0}+mac` invertido). No desempareja; el mando se suspende y reconecta al encenderlo. Solo Bluetooth; USB devuelve `UnsupportedDevice`, enlace inactivo `NotConnected` (error Win32 1167). `SupportsPowerOffAsync` cachea 30 s por `DevicePath`. El sondeo de inactividad (`PollControllerIdlePowerOffAsync`, timer `DispatcherTimer` de 1 s en `MainWindowViewModel` mientras `_isMonitoring`) compara snapshots de `ReadInputAsync` por `ControllerId` con epsilon 0.001; reinicia al cambiar `DeviceKey` (reconexión), y `PowerOffTriggered` evita re-disparos hasta nueva actividad. Antes de apagar se detiene la salida virtual si está activa (`StopVirtualGamepadAsync`). Pendiente validar con hardware: formato de ruta SDL para Xbox BT, que IOCTL funcione con mando realmente conectado y que el sueño/reconexión sea el esperado en DS4/DualSense/génericos.
- Actualizador por GitHub con dos canales (`Models/UpdateChannels.cs`: `Standard = "standard"` y `Nightly = "nightly"`; `AppSettings.UpdateChannel` persistido y validado en `JsonAppSettingsRepository`, editable en Ajustes > Actualizaciones): canal `standard` usa `releases/latest` y solo ofrece versiones con minor (segundo componente) mayor — un patch como 0.16.6 NO actualiza un 0.16.x; canal `nightly` usa `releases?per_page=1` (último release publicado, incluidos prereleases marcados en GitHub) y ofrece cualquier versión mayor (0.16.0→0.16.5, 0.17.x, 0.18.x). `IAppUpdateService.GetLatestReleaseAsync(channel)` recibe el canal; `AppReleaseInfo.IsPrerelease` refleja el flag de GitHub. `GitHubUpdateService` consulta `api.github.com/repos/LiebheartZ/Gamepad-Input-Manager-Project` (User-Agent `GDM-UpdateChecker/<ver>`; 404 = sin releases o repo privado/inalcanzable — la URL de releases proporcionada por el usuario es `github.com/LiebheartZ/Gamepad-Input-Manager-Project/releases`). El asset `.zip` (preferencia nombre con "gdm") se descarga a `%LocalAppData%\GDM\Updates` y se extrae a `staging-*` (`ResolveAppDirectory` acepta raíz plana o una única carpeta). `UpdateViewModel` (expuesto como `MainWindowViewModel.Updates`, canal leído en vivo vía `Func<string>` desde `SettingsViewModel.UpdateChannel`) compara `tag_name` (sin `v`) con la versión del ensamblado según el canal; `CheckForUpdatesOnStartup` (`AppSettings`, editable en Ajustes > Actualizaciones, persistido con Guardar ajustes) dispara `CheckAsync(silent:true)` tras cargar mandos (`KickoffStartupUpdateCheck`). La instalación pide confirmación, descarga con progreso, genera `%TEMP%\gdm-update-*.cmd` vía `UpdateInstaller.PrepareApplyScript` (espera el PID con `tasklist`, `robocopy /E` staging→`AppContext.BaseDirectory`, relanza `GDM.exe`, auto-borrado) y llama `App.RequestProgrammaticExit(skipExitConfirmation:true)` — misma vía de cierre que la X pero omitiendo solo el diálogo de confirmación (HidHide y ajustes se guardan igual). Pendiente: el repo `LiebheartZ/Gamepad-Input-Manager-Project` es PRIVADO (confirmado por el usuario, la API devolvía 404 sin auth). Soporte añadido: `GitHubUpdateService` usa la constante `EmbeddedAccessToken` (PAT fine-grained oficial del propietario con `Contents: read` sobre el repo, pegado en el código) que añade `Authorization: Bearer` a todas las peticiones (API de releases y descarga del asset). Se eliminó el token manual de Ajustes a petición del usuario: el token embebido es permanente hasta que el proyecto pase a público o se decida revocarlo; el ctor aún acepta `accessTokenProvider` opcional pero nadie lo usa.
- Cierre de ventana: `AppSettings.CloseButtonBehavior` (`tray`/`close`, en `CloseButtonBehaviors`) decide si la X oculta a la bandeja o solicita salida completa. Toda salida completa pasa por `Views/ExitConfirmationWindow` salvo que `AppSettings.ShowExitConfirmation` sea false o la instancia sea el relevo elevado (`IsElevatedRelaunch`). La casilla "No volver a mostrar" persiste la supresión vía `SettingsViewModel.SetShowExitConfirmationAsync` (o repositorio directo). Cancelar la confirmación o el aviso de HidHide llama `MainWindow.CancelExitRequest()` para limpiar `_isExitRequested`; el cierre real se reprograma con `BeginInvoke(window.Close)` tras `_allowWindowClose = true`.
- `Views/AppDialog.cs`: diálogo modal temático (mismo estilo que `ExitConfirmationWindow`: esquinas redondeadas, recursos del tema, icono circular de color según tipo) que reemplazó todos los `MessageBox.Show` de WPF. API estática: `Show(title, message, yesNo, icon, owner)`, `Confirm(...)` → bool, `ShowInfo/ShowWarning/ShowError(title, message, owner?)`. `AppDialogIcon`: None/Information/Warning/Error/Question. Los diálogos sin owner se centran en pantalla; con owner en `CenterOwner`. El `ColorDialog` de WinForms para el color LED manual se mantiene (es un selector del sistema).

## Pruebas y compilación

- Framework de pruebas: xUnit 2.9.3 con `Microsoft.NET.Test.Sdk` 17.14.1 (VSTest).
- Resultado más reciente: **49/49 pruebas pasaron**, incluyendo perfiles, lectura del Tester y mapeo HIDMaestro de botones, cruceta y ejes.
- La validación más reciente fue:

```powershell
dotnet test GDM.sln --no-restore
```

- La prueba recompiló el proyecto WPF y el proyecto de tests sin warnings ni errores.
- En cambios nuevos, usa una carpeta de salida fresca si `GDM.exe` está abierto, por ejemplo `bin\NombreDeValidacion\`. No borres carpetas de validación existentes sin inspeccionarlas.
- Comandos habituales documentados en `README.md`:

```powershell
dotnet build GDM.sln
dotnet test GDM.sln
dotnet run --project GDM.csproj
```

- El output normal suele ser `bin\Debug\GDM.exe`; con `OutputPath` personalizado queda en esa carpeta.

## Lista de pruebas existentes

- `tests/JsonPersistenceTests.cs`: persistencia JSON y recuperación.
- `tests/ProfileManagementTests.cs`: persistencia CRUD, catálogo corrupto, nombres duplicados, asignación y protección ante borrado de perfiles asignados.
- `tests/MainWindowViewModelTests.cs`: navegación, Mandos, guardado/recuperación, integración del Tester.
- `tests/SettingsViewModelTests.cs`: ajustes y temas.
- `tests/TesterViewModelTests.cs`: selección, actualización, lectura, desconexión y reporte de errores.

Antes de cambiar un servicio SDL, revisar cobertura y ciclo de vida de inicialización, polling, desconexión y Dispose; no basta con que compile.

## Criterios para trabajo futuro

- Hacer cambios completos y quirúrgicos, no arreglos cosméticos no solicitados.
- Preservar el hotplug y los identificadores actuales.
- Respetar los patrones existentes de ViewModel, comandos, servicios e inyección en `App.xaml.cs`.
- Informar explícitamente errores; no silenciar excepciones ni devolver éxitos falsos.
- Mantener documentación directamente relacionada actualizada.
- Validar con la prueba más pequeña que cubra el cambio y escalar a la suite completa cuando aplique.
- Cuando se mencione una limitación, explicarla con claridad, sin prometer capacidades que SDL o la aplicación no ofrecen.
- Si el usuario comparte captura, identificar con precisión las diferencias y editar las coordenadas del control pertinente; evitar retocar silueta ya aprobada cuando solo solicita mover controles.

## Prompt corto sugerido para iniciar la nueva sesión

> Lee `CONTEXTO_SESION_GDM.md` y `README.md` antes de cambiar nada. Continúa el proyecto GDM respetando decisiones y comportamientos ya confirmados, especialmente la detección SDL en caliente y el mando visual de Tester, que el usuario aprobó. Revisa el código actual y valida cualquier cambio con pruebas.
- Whitelist de HidHide: el driver compara la ruta NT del proceso (\Device\HarddiskVolumeN\...), NO la ruta DOS. HidHideService.EnsureCurrentApplicationIsWhitelisted convierte Environment.ProcessPath con QueryDosDevice (ToNtDevicePath) antes de escribirla via IOCTL 2049, y elimina entradas DOS obsoletas X:\...\GDM.exe (IsLegacyDosGdmPath). Antes se escribia C:\... y la auto-autorizacion nunca funcionaba; el usuario debia anadirla a mano en HidHide Client (que escribe formato NT).
- Actualizador: bug del script robocopy. AppContext.BaseDirectory termina en backslash y el .cmd generaba una comilla escapada al final de la ruta destino, robocopy devolvia ERROR 123 / codigo 16 y nunca copiaba. UpdateInstaller.PrepareApplyScript ahora hace TrimEnd en origen y destino antes de escribir el script.
- Ajustes > Debug: la pestana existia en SettingsViewModel (indice 6, IsDebugCategory) pero faltaba su TabItem en el TabControl del XAML, por eso no era visible. Anadido.
- UpdateViewModel.ForceInstallLatestCommand (boton "Forzar instalacion de la ultima version" en Ajustes > Debug): consulta releases?per_page=1 (canal nightly, ultimo release publicado) ignorando la comparacion de versiones y ejecuta el mismo flujo de descarga+instalacion. InstallAsync se refactorizo a InstallReleaseAsync(AppReleaseInfo) compartido.
- Aviso de actualizacion al inicio: si el chequeo silencioso de arranque detecta version nueva y AppSettings.SkipUpdateNotifications es false, MainWindowViewModel.PromptUpdateNotificationAsync muestra Views/UpdateAvailableWindow (mismo estilo que ExitConfirmationWindow): mensaje con version/tag, casilla "No volver a mostrar este aviso" (persiste via SettingsViewModel.SetSkipUpdateNotificationsAsync -> app-settings.json), boton "Ahora no" (ignora) y "Descargar e instalar" (UpdateViewModel.InstallAvailableUpdateAsync -> InstallReleaseAsync con skipConfirmation:true). UpdateViewModel expone AvailableUpdate.
- Rendimiento/CPU: medido ~60% de un nucleo con salida virtual activa. Causas: (1) DispatcherTimer de entrada a 8 ms (~125 ticks/s) hacia 2 ciclos completos SDL_JoystickUpdate+GameControllerUpdate+~30 P/Invoke por tick (salida virtual + Tester); (2) poll de dispositivos cada 500 ms con enumeracion SDL + IOCTLs HidHide + Clear/Add de ObservableCollections. Optimizaciones: OnTesterInputTimerTick solo llama Tester.ReadSelectedInputAsync cuando IsTesterSection o Tester.IsVirtualGamepadRunning (la entrada del Tester sigue siendo necesaria para reenvio de su salida virtual); RefreshDevicesAsync solo reconstruye ControllerItems cuando displayDevices cambia (devicesChanged); HidHideViewModel.UpdateDevices calcula estados primero y retorna sin tocar la coleccion si (Device, IsHiddenByGdm) no cambio.
- Rendimiento/CPU (2): HidMaestroVirtualGamepadService deduplica SubmitState — VirtualControllerSession.LastSubmittedState guarda el HMGamepadState anterior y StatesEqual (Buttons+Hat+Axes) omite la escritura al driver cuando el estado no cambio (antes ~125 escrituras HID/s incluso en reposo). El estado inicial de StartAsync tambien se registra para no reescribirlo en el primer tick.
- Ajustes > Debug: nuevo selector 'Intervalo de sondeo de entrada virtual' (AppSettings.VirtualInputPollIntervalMs, 8/16/33/50 ms, persistido via SettingsViewModel.PollIntervalOption). MainWindowViewModel.VirtualInputPollInterval reemplaza los TimeSpan.FromMilliseconds(8) fijos en los 3 puntos donde se activa el timer de entrada; el cambio se aplica en caliente via PropertyChanged si hay salida virtual activa. Mas intervalo = menos CPU, mas latencia.

## 0.16.21 - Ahorro de CPU con ventana oculta

Se revirtio el experimento de hilo dedicado SDL (estado congelado en RawInput). Se anadio IsAppWindowHidden en MainWindowViewModel: si la ventana esta minimizada/oculta y no hay salida virtual activa, el tick de entrada retorna sin trabajar, las lecturas del Tester se omiten y el sondeo de dispositivos (RefreshDevicesAsync) se salta; el reenvio fisico->virtual sigue activo siempre que haya salida virtual. BuildCheck75.
