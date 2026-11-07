# GDM Gamepad Device Manager

Especificación funcional y técnica --- Pestaña «Mandos»

Documento para implementar o refactorizar la pestaña mediante GitHub
Copilot. Objetivo: describir el comportamiento esperado, las capas, las
clases, los eventos, el modelo de datos, los estados y las pruebas sin
asumir una biblioteca de detección específica antes de revisar el
proyecto.

## 1. Objetivo y alcance

La pestaña Mandos debe centralizar la visualización y administración de
controles. Debe presentar dispositivos emparejados por Windows,
dispositivos guardados en la base de datos, dispositivos Bluetooth
cercanos disponibles para conexión y dispositivos actualmente
conectados. Estos conjuntos pueden solaparse: no se deben crear tarjetas
duplicadas por cada origen.

-   Mostrar una lista que se actualice sin reiniciar la aplicación.

-   Permitir actualizar la detección manualmente y activar/desactivar el
    emparejamiento automático.

-   Permitir mostrar u ocultar dispositivos que no estén conectados.

-   Ofrecer acciones por dispositivo: conectar/desconectar, abrir
    configuración y ver información.

-   Separar la detección del hardware, la lógica de negocio, el
    almacenamiento y la interfaz.

-   Esta especificación no obliga a implementar todavía remapeo, mandos
    virtuales ni operaciones HidHide; la tarjeta puede ofrecer accesos a
    esas funciones existentes.

## 2. Conceptos y estados: no confundirlos

| Concepto \| Significado \| Persistencia \|

| --- \| --- \| --- \|

| Emparejado por Windows \| Windows conserva la relación de
  emparejamiento, por ejemplo Bluetooth. No implica que el mando esté
  encendido o conectado. \| Windows \|

| Guardado \| Existe un registro del mando en la base de datos de GDM,
  con perfil, preferencias u otros datos. \| Base de datos GDM \|

| Disponible para conectar \| Un dispositivo Bluetooth está siendo
  anunciado/descubierto y puede ser candidato a conexión. No implica que
  ya esté emparejado. \| Temporal durante búsqueda \|

| Conectado \| El sistema operativo o la API de entrada confirma que el
  dispositivo está actualmente presente y operativo. \| Estado en tiempo
  real \|

| Desconectado \| El dispositivo guardado o emparejado no tiene una
  conexión activa. \| Derivado de detección + datos guardados \|

Un mismo mando puede estar guardado, emparejado y conectado
simultáneamente. Representar estas condiciones como indicadores
independientes, no como una única enumeración excluyente.

## 3. Diseño de interfaz

La composición visual se basa en la referencia proporcionada: encabezado
y controles superiores; debajo, un panel/lista desplazable con tarjetas.

## 3.1 Zona superior

-   Interruptor «Emparejar mandos automáticamente al detectarlos»:
    controla la política de conexión/emparejamiento automático. No debe
    iniciar una operación silenciosa que Windows no permita; explicar o
    solicitar confirmación cuando el sistema requiera intervención.

-   Casilla «Mostrar todos los dispositivos»: activada muestra
    conectados, desconectados, guardados y emparejados; desactivada
    prioriza conectados y dispositivos detectados actualmente. Definir y
    mantener este filtro de forma consistente.

-   Botón «Dispositivos de Juego»: abre la herramienta de Windows o la
    sección ya existente para administrar dispositivos de juego. No
    fingir una acción si no hay una implementación.

-   Botón «Buscar dispositivos»: inicia una búsqueda Bluetooth temporal
    y actualiza el listado. Mientras busca, mostrar estado visual y
    deshabilitar búsquedas duplicadas.

-   Etiqueta de sección: por ejemplo «Dispositivos conectados» o
    «Dispositivos», adaptada al filtro.

## 3.2 Tarjeta de mando

Cada tarjeta debe mostrar, como mínimo:

-   Icono genérico o icono de marca/tipo, con fallback si no existe
    imagen.

-   Nombre legible del mando.

-   Tipo de conexión: Bluetooth, USB, receptor inalámbrico u otro, solo
    si se conoce.

-   Identificador técnico enmascarado (por ejemplo, dirección Bluetooth
    o identificador de dispositivo); nunca mostrar datos sensibles
    innecesarios.

-   Estado: Conectado, Emparejado, Guardado, Disponible o Desconectado.
    Si aplica, mostrar varios indicadores.

-   Perfil asignado y nombre del perfil; usar «Por defecto» cuando
    corresponda.

-   Batería como porcentaje si la fuente ofrece un valor válido. Si no,
    mostrar «N/A»; no inventar ni asumir 100%.

-   Botón de acción contextual: «Desconectar» si está conectado;
    «Conectar» si está emparejado y desconectado; «Emparejar/Conectar»
    si es un dispositivo Bluetooth descubierto y Windows permite esa
    operación.

-   Botón de configuración (engranaje) que abre la configuración del
    dispositivo seleccionado.

La tarjeta completa no debe cambiar el estado del mando por sí sola. Los
controles de acción deben tener eventos separados. Las operaciones
asíncronas deben mostrar progreso y errores.

## 4. Reglas de agrupación y duplicados

1.  Recopilar los dispositivos de las fuentes disponibles:
    Windows/entrada, base de datos GDM y búsqueda Bluetooth.

2.  Normalizar cada resultado a un modelo común ControllerDevice.

3.  Intentar relacionar resultados mediante identificador estable del
    dispositivo (Device Instance ID, identificador de contenedor,
    dirección Bluetooth u otro identificador proporcionado por la API).

4.  Usar nombre + tipo únicamente como coincidencia secundaria y
    ambigua; nunca fusionar dos mandos solo porque tengan el mismo
    nombre.

5.  Combinar los indicadores de estado y los datos persistidos en un
    registro visual único.

6.  Ordenar: conectados primero; después emparejados/guardados
    desconectados; al final, disponibles durante el escaneo. Dentro de
    cada grupo, ordenar por nombre.

7.  Al terminar el escaneo Bluetooth, retirar los resultados temporales
    que ya no estén anunciados, pero conservar registros guardados y
    emparejados.

## 5. Arquitectura por capas

| Capa \| Responsabilidad \| No debe hacer \|

| --- \| --- \| --- \|

| Presentation / UI \| Dibujar lista y tarjetas; enlazar controles;
  mostrar carga, vacío y error; enviar comandos al ViewModel. \|
  Consultar APIs Bluetooth/Windows o ejecutar SQL directamente. \|

| ViewModel / Controller \| Mantener estado de pantalla, filtros,
  selección y comandos; transformar modelos en datos de presentación. \|
  Contener código específico de Bluetooth o detalles del framework de
  base de datos. \|

| Application / Services \| Coordinar escaneo, refresco,
  conexión/desconexión, sincronización y persistencia. \| Manipular
  directamente controles visuales. \|

| Domain / Models \| Definir ControllerDevice, estados, perfil asociado
  y reglas de combinación. \| Depender de WPF/WinForms, Windows UI o
  base de datos. \|

| Infrastructure \| Implementar detección Windows/Bluetooth, lectura de
  batería, conexión y repositorio de datos. \| Decidir cómo se presenta
  visualmente la tarjeta. \|

Antes de crear archivos, Copilot debe inspeccionar el framework del
proyecto (WPF, WinForms u otro), el patrón existente (MVVM u otro), las
bibliotecas instaladas y el sistema de persistencia. Reutilizar
convenciones existentes; no migrar de framework ni agregar paquetes sin
justificarlo.

## 6. Clases e interfaces sugeridas

## 6.1 Modelo ControllerDevice

Crear o adaptar un modelo equivalente con estas propiedades (los nombres
pueden ajustarse al estilo del proyecto):

-   string StableId: identificador normalizado utilizado para fusionar
    registros.

-   string? WindowsDeviceId: identificador de dispositivo de Windows.

-   string DisplayName: nombre para la interfaz.

-   string? Manufacturer / ProductName: información opcional.

-   ControllerConnectionType ConnectionType: Bluetooth, USB,
    WirelessReceiver, Unknown.

-   bool IsConnected; bool IsPaired; bool IsSaved; bool
    IsAvailableForPairing.

-   int? BatteryPercentage: null si se desconoce; validar rango 0--100.

-   string? ProfileId; string? ProfileName.

-   DateTimeOffset? LastSeen; DateTimeOffset LastUpdated.

-   string? DeviceInstancePath o identificador técnico, si lo
    proporciona la fuente.

Los indicadores IsConnected, IsPaired, IsSaved e IsAvailableForPairing
son independientes. No definirlos como estados mutuamente excluyentes.

## 6.2 Contratos de servicios

Crear interfaces que permitan probar la lógica sin hardware real.
Nombres orientativos:

-   IControllerDiscoveryService: EnumerateConnectedAsync(),
    EnumeratePairedAsync(), ScanBluetoothAsync(duration,
    cancellationToken), y evento/stream de cambios si la API lo soporta.

-   IControllerConnectionService: ConnectAsync(deviceId),
    DisconnectAsync(deviceId), PairAsync(deviceId) cuando la API y los
    permisos lo permitan.

-   IControllerRepository: GetSavedControllersAsync(),
    SaveOrUpdateAsync(controller), RemoveAsync(id), GetProfileAsync(id).

-   IControllerBatteryService: GetBatteryAsync(deviceId) -\> int?;
    devolver null si no es compatible o no se conoce.

-   IControllerListService: RefreshAsync(), SearchAvailableAsync(),
    MergeAndSort(...) para coordinar las fuentes.

Los nombres son una propuesta, no una exigencia de crear todos los
servicios si el proyecto ya cuenta con equivalentes. No duplicar
servicios existentes.

## 7. Flujo de carga inicial

1.  Al abrir la pestaña, mostrar indicador de carga y solicitar la lista
    guardada a IControllerRepository.

2.  Consultar dispositivos conectados y emparejados mediante el servicio
    de detección.

3.  Combinar los resultados con los registros guardados usando StableId.

4.  Actualizar la colección observable del ViewModel en el hilo de UI.

5.  Aplicar el filtro «Mostrar todos» y ordenar según las reglas
    anteriores.

6.  Ocultar el indicador de carga y mostrar la lista, un estado vacío o
    un mensaje de error recuperable.

Si una fuente falla, mostrar los resultados válidos de las otras fuentes
y comunicar que la información puede estar incompleta. No borrar la
lista ni asumir que todos los mandos se desconectaron.

## 8. Refresco en tiempo real y manual

El refresco debe ser idempotente: dos solicitudes cercanas no deben
producir duplicados ni estados contradictorios.

-   El botón de refresco debe consultar el estado actual; no limitarse a
    volver a dibujar la colección anterior.

-   Escuchar eventos de llegada/salida de dispositivos cuando la API
    elegida los proporcione.

-   Si no hay eventos fiables, usar un temporizador moderado y
    configurable, cancelable al cerrar la pestaña. Evitar sondeos muy
    frecuentes.

-   Al desconectar un mando, actualizar IsConnected solo tras
    confirmación de la fuente o reconciliarlo en el siguiente refresco.

-   Al desaparecer un dispositivo conectado, conservarlo como
    guardado/emparejado si corresponde; no eliminarlo de la base de
    datos.

-   Cancelar operaciones y suscripciones al cerrar la vista o destruir
    el ViewModel.

## 9. Búsqueda Bluetooth

1.  Al pulsar «Buscar dispositivos», iniciar el escaneo con
    CancellationToken y duración definida.

2.  Mostrar «Buscando...» y permitir cancelar si el framework lo admite.

3.  Recibir resultados parciales y agregarlos/actualizarlos sin duplicar
    tarjetas.

4.  Marcar resultados como IsAvailableForPairing cuando la fuente
    realmente indique que son descubribles.

5.  No presentar todos los dispositivos emparejados como si estuvieran
    actualmente disponibles para emparejar.

6.  Al finalizar, marcar el escaneo como terminado y retirar los
    resultados temporales que ya no sean válidos.

7.  Si Bluetooth está apagado, faltan permisos o el hardware no soporta
    escaneo, mostrar un mensaje claro y mantener la lista previa.

El escaneo Bluetooth y la enumeración de mandos conectados son
operaciones diferentes. Un mando conectado por USB no debe desaparecer
cuando termina la búsqueda Bluetooth.

## 10. Acciones de la tarjeta

## Conectar

Validar que el dispositivo esté emparejado o que el flujo de
emparejamiento esté permitido. Ejecutar la operación en segundo plano,
mostrar progreso y luego refrescar el estado real. Si Windows gestiona
la conexión por sí mismo, abrir el flujo del sistema en vez de simular
una conexión.

## Desconectar

Solicitar desconexión mediante la API compatible. No eliminar el
registro guardado ni desemparejarlo automáticamente. Si el sistema no
permite desconectar ese tipo de dispositivo, explicar la limitación.

## Configuración

Abrir la página o diálogo existente para el dispositivo. Pasar
StableId/WindowsDeviceId, no el índice de la tarjeta. Si la pantalla
todavía no existe, dejar un comando explícito no implementado y no
mostrar éxito falso.

## 11. Persistencia y perfiles

-   Guardar solo información propia de GDM: alias, perfil seleccionado,
    preferencias y metadatos que el usuario haya decidido conservar.

-   No intentar almacenar ni reemplazar el emparejamiento administrado
    por Windows.

-   Guardar la asociación de perfil mediante un identificador estable,
    no por nombre visible.

-   Si un mando conectado no tiene registro, permitir crear su registro
    cuando el usuario configure/guarde el mando o según la política
    existente.

-   Si la base de datos no está disponible, la detección en tiempo real
    debe seguir funcionando; informar que los datos de perfiles no
    pudieron cargarse.

## 12. Estados de la interfaz

| Situación \| Comportamiento esperado \|

| --- \| --- \|

| Carga inicial \| Mostrar indicador; evitar tarjetas falsas o parpadeo
  innecesario. \|

| Lista vacía \| Texto útil: no se detectaron mandos; sugerir conectar o
  buscar. \|

| Búsqueda activa \| Mostrar progreso; botón bloqueado o transformado en
  Cancelar. \|

| Error de detección \| Mensaje recuperable; conservar resultados
  válidos. \|

| Mando desconectado \| Actualizar indicador y acción contextual sin
  reiniciar. \|

| Batería desconocida \| Mostrar N/A; no convertir null en cero. \|

| Perfil no encontrado \| Mostrar Por defecto o Sin perfil, según reglas
  del proyecto. \|

| Dispositivo duplicado \| Fusionar los registros compatibles en una
  tarjeta. \|

## 13. Manejo de errores y seguridad de concurrencia

-   Usar async/await para I/O y operaciones de dispositivo; nunca
    bloquear el hilo de UI con .Wait() o .Result.

-   Usar CancellationToken en escaneo, refresco y operaciones largas.

-   Capturar errores en los límites del servicio; registrar detalles
    técnicos y mostrar mensajes entendibles.

-   Evitar dos escaneos simultáneos. Evitar que un refresco antiguo
    sobrescriba un resultado más reciente.

-   Comprobar que la vista/ViewModel sigue activo antes de actualizar
    UI.

-   Validar los identificadores recibidos; no usar nombre de dispositivo
    como clave primaria.

-   Los logs no deben incluir información innecesaria de identificadores
    únicos.

## 14. Requisitos de implementación para Copilot

1.  Primero inspecciona la solución y localiza la pestaña Mandos, su
    vista, ViewModel/controlador, modelos, repositorios, base de datos y
    servicios de detección existentes.

2.  Resume qué archivos reutilizarás y qué archivos modificarás. No
    cambies otras pestañas ni el diseño global sin necesidad.

3.  Implementa primero el modelo común y la combinación de resultados,
    con pruebas unitarias.

4.  Implementa después la enumeración de conectados/emparejados y la
    carga de registros guardados.

5.  Conecta la colección observable a la UI y reproduce el diseño de
    referencia: controles superiores + lista de tarjetas desplazable.

6.  Agrega búsqueda Bluetooth y las acciones
    conectar/desconectar/configuración de forma incremental.

7.  Usa las APIs y paquetes compatibles con el framework/versión de
    Windows del proyecto. No inventes métodos de una biblioteca.

8.  Al terminar, compila la solución, corrige errores y entrega un
    resumen de archivos modificados, dependencias agregadas,
    limitaciones de la API y pasos de prueba.

## 15. Criterios de aceptación

-   Un mando emparejado por Windows aparece aunque esté
    apagado/desconectado, si se activa Mostrar todos.

-   Un mando guardado aparece aunque no esté conectado ni presente en un
    escaneo.

-   Un mando conectado aparece como conectado y no se duplica con su
    registro guardado.

-   Los dispositivos Bluetooth descubiertos aparecen temporalmente con
    una acción apropiada.

-   Al desconectar físicamente un mando, el estado cambia sin reiniciar
    la aplicación.

-   Al pulsar Buscar, los resultados se actualizan; los mandos
    USB/conectados no desaparecen.

-   El filtro Mostrar todos modifica la lista de forma predecible.

-   La batería desconocida se presenta como N/A y los perfiles se
    muestran correctamente.

-   Los errores de Bluetooth o base de datos no cierran la aplicación ni
    borran datos válidos.

-   Las acciones de conectar/desconectar muestran progreso, resultado y
    error real.

## 16. Pruebas manuales recomendadas

| Prueba \| Resultado esperado \|

| --- \| --- \|

| Bluetooth emparejado, apagado \| Aparece como emparejado/desconectado
  con Mostrar todos. \|

| Bluetooth emparejado, encendido \| Se detecta como conectado; una sola
  tarjeta. \|

| Mando USB conectado \| Aparece conectado; no depende del escaneo
  Bluetooth. \|

| Mando guardado sin conexión \| Aparece con perfil guardado al mostrar
  todos. \|

| Mando Bluetooth nuevo en modo descubrimiento \| Aparece durante
  búsqueda con acción compatible. \|

| Desconectar desde Windows o apagar mando \| La lista cambia sin
  reiniciar. \|

| Desconectar desde tarjeta \| Estado se reconcilia con la detección
  real. \|

| Batería no soportada \| Muestra N/A. \|

| Bluetooth apagado/permisos denegados \| Mensaje claro; lista anterior
  permanece. \|

| Dos mandos con el mismo nombre \| Se mantienen como dispositivos
  distintos si sus IDs difieren. \|

## 17. Nota sobre APIs de Windows

La detección Bluetooth, el emparejamiento y la desconexión no son
equivalentes a leer mandos de juego conectados. La API concreta depende
de si el proyecto usa Windows.Devices.Enumeration, GameInput, XInput,
HID/Raw Input u otra biblioteca. Copilot debe revisar el código y la
compatibilidad de destino antes de elegir. Algunas APIs enumeran solo
dispositivos de su propia categoría y algunas operaciones de conexión
son gestionadas por Windows; no se debe prometer soporte universal sin
verificar el mando y la API.

## 18. Instrucción final para pegar en Copilot

Implementa la pestaña Mandos de GDM siguiendo este documento. Antes de
editar, inspecciona la solución y detecta el framework, patrón
arquitectónico, APIs de entrada y persistencia ya existentes. Reutiliza
los componentes y servicios compatibles. La lista debe combinar mandos
conectados, emparejados por Windows, guardados en la base de datos y
Bluetooth descubiertos sin duplicados; mantener estados independientes;
actualizarse tras conectar/desconectar y al refrescar; y presentar una
UI con opciones arriba y tarjetas abajo. No inventes APIs ni simules
operaciones exitosas. Trabaja por etapas, compila y prueba cada etapa,
limita los cambios a la pestaña y reporta los archivos y limitaciones.

## 19. Integración obligatoria de SDL

SDL (Simple DirectMedia Layer) será la biblioteca principal para la
enumeración y el acceso a los mandos de juego que estén conectados y
disponibles para la aplicación. La integración debe reutilizar la
versión y los paquetes ya presentes en la solución. Si todavía no existe
SDL, Copilot debe comprobar el framework de destino y elegir SDL2 o SDL3
de forma explícita, sin mezclar APIs de ambas versiones.

### 19.1 Responsabilidades de SDL y de Windows

| Función \| SDL \| Windows / GDM \|

| --- \| --- \| --- \|

| Enumerar mandos conectados \| Fuente principal mediante SDL Gamepad
  API. \| Windows aporta los dispositivos que SDL puede abrir. \|

| Leer botones, ejes, gatillos y hats \| Sí, para mandos abiertos por
  SDL. \| No reemplazar lectura SDL con consultas de UI. \|

| Detectar conexión/desconexión \| SDL puede notificar eventos de
  dispositivo; refrescar enumeración para reconciliar. \| Windows es el
  sistema operativo que conecta/desconecta el dispositivo. \|

| Mostrar emparejados pero desconectados \| No es una fuente completa
  para este estado. \| Consultar la enumeración de dispositivos
  emparejados de Windows. \|

| Buscar mandos Bluetooth anunciados cercanos \| No asumir que SDL
  ofrece un escáner Bluetooth o emparejamiento. \| Usar las APIs de
  Windows Bluetooth/Device Enumeration y sus permisos. \|

| Emparejar Bluetooth \| No atribuir esta operación a SDL. \| Windows
  administra el flujo de emparejamiento; invocar el flujo soportado por
  Windows. \|

| Base de datos y perfiles \| No es responsabilidad de SDL. \| GDM
  Repository guarda perfiles, alias y preferencias. \|

| Batería \| Usar la API SDL si la versión y el dispositivo informan un
  valor; puede no estar disponible. \| Mostrar N/A si no existe valor
  fiable; opcionalmente consultar otra fuente compatible. \|

### 19.2 SDL2 y SDL3: elegir una sola API

SDL2 utiliza principalmente SDL_GameController\* para mandos reconocidos
como game controllers y SDL_Joystick\* para acceso de joystick de menor
nivel. SDL3 utiliza SDL_Gamepad\* y SDL_Joystick\*; sus nombres, tipos,
eventos y funciones no son intercambiables con SDL2. Copilot debe
revisar la versión instalada y consultar sus encabezados/documentación
local. No escribir código SDL3 dentro de un proyecto SDL2 ni inventar
nombres de funciones.

En SDL3, la implementación debe considerar las funciones Gamepad de
SDL3, como SDL_GetGamepads, SDL_OpenGamepad, SDL_CloseGamepad,
SDL_GetGamepadName, SDL_GetGamepadGUID, SDL_GetGamepadJoystick y las
funciones de lectura de botones/ejes que correspondan a la versión
instalada. Los eventos de SDL3 incluyen eventos de añadido y retirado de
gamepad; confirmar los nombres exactos en la versión del proyecto.

En SDL2, utilizar la API SDL_GameController cuando SDL reconoce el mando
con un mapeo adecuado; usar SDL_Joystick como alternativa de
compatibilidad cuando corresponda. Verificar la versión exacta y los
eventos disponibles antes de codificar.

### 19.3 Servicio SDL propuesto

Implementar un adaptador dedicado, por ejemplo
SdlControllerDiscoveryService o SdlGamepadService. Su responsabilidad es
traducir resultados SDL al modelo común ControllerDevice; no debe
manipular controles visuales ni ejecutar SQL.

-   Inicializar los subsistemas SDL requeridos una sola vez en el ciclo
    de vida de la aplicación, o reutilizar el inicializador SDL
    existente.

-   Enumerar los gamepads SDL que estén conectados y construir un
    resultado por dispositivo.

-   Abrir el mando cuando se necesite leer entradas o consultar
    propiedades que requieran un handle abierto. Mantener un registro de
    handles abiertos por identificador SDL válido.

-   Cerrar los handles al retirar el dispositivo o al apagar el
    servicio. No cerrar un handle que otro componente siga usando.

-   Procesar los eventos SDL de conexión y desconexión, y reconciliar la
    colección con una enumeración actual para evitar estados obsoletos.

-   Obtener nombre, GUID, índice/ID SDL y tipo de conexión solo si SDL y
    la versión instalada proporcionan esos datos.

-   No utilizar el índice de enumeración como identidad persistente: los
    índices pueden cambiar al conectar o retirar dispositivos.

-   No asumir que GUID es un identificador único de unidad física: puede
    identificar un modelo/tipo compartido por muchos mandos. Combinarlo
    con identificadores de dispositivo apropiados cuando estén
    disponibles.

-   Si un mando no es reconocido como Gamepad por SDL, considerar el
    acceso Joystick como fallback, pero no prometer que todos los
    botones se mapearán semánticamente sin un mapping.

-   Si SDL no informa batería o tipo de conexión, conservar null/Unknown
    y mostrar N/A/Desconocido.

### 19.4 Flujo de detección integrado

1.  Al abrir la pestaña, el servicio SDL enumera los gamepads
    conectados.

2.  En paralelo, el servicio Windows consulta dispositivos emparejados y
    el repositorio consulta mandos guardados.

3.  El servicio de aplicación normaliza las tres fuentes a
    ControllerDevice y fusiona coincidencias con identificadores
    fiables.

4.  La UI muestra una tarjeta por dispositivo físico coincidente, con
    indicadores independientes Connected, Paired y Saved.

5.  Al recibir un evento SDL de conexión, actualizar el mando y volver a
    reconciliar las fuentes.

6.  Al recibir un evento SDL de retirada, marcarlo desconectado;
    mantenerlo en la lista si está emparejado o guardado.

7.  La búsqueda Bluetooth se realiza mediante Windows. Los resultados
    descubiertos se muestran como temporales y no deben presentarse como
    mandos SDL conectados hasta que SDL los enumere.

8.  Después de conectar/emparejar desde Windows, volver a enumerar SDL y
    Windows para actualizar la tarjeta.

### 19.5 Hilo de ejecución y ciclo de eventos

No bloquear el hilo de UI al inicializar SDL, enumerar dispositivos o
procesar eventos. Confirmar los requisitos de thread-safety de la
versión SDL usada. Si el proyecto ya tiene un bucle SDL, integrarse con
él; no crear un segundo bucle de eventos que compita por los mismos
eventos. Si la aplicación es una interfaz WPF/WinForms y no tiene bucle
SDL, crear un servicio de eventos controlado, con cancelación y ciclo de
vida claro, y publicar cambios al ViewModel mediante el mecanismo de
dispatch del framework. Evitar sondeo intensivo y detener el servicio al
cerrar la aplicación.

### 19.6 Alcance de SDL en la pestaña Mandos

SDL debe alimentar la información de los mandos conectados. La pestaña
también necesita las fuentes Windows y base de datos para cumplir todos
los estados solicitados.

-   SDL: detección de gamepads conectados, datos compatibles, eventos de
    llegada/salida y disponibilidad de lectura.

-   Windows: dispositivos emparejados desconectados, búsqueda Bluetooth,
    emparejamiento y operaciones de conexión gestionadas por el sistema.

-   GDM: perfiles, alias, preferencias, registros guardados,
    presentación y acciones de interfaz.

-   No implementar un botón «Conectar» que llame a una función SDL
    inexistente. Abrir el flujo de Windows cuando la conexión no sea
    controlable directamente por SDL.

### 19.7 Pruebas específicas SDL

-   Un mando USB compatible aparece en SDL y en la lista GDM.

-   Un mando Xbox/PlayStation conectado por Bluetooth y reconocido por
    SDL aparece como conectado.

-   Un mando emparejado pero apagado no se pierde: aparece desde Windows
    si Mostrar todos está activado.

-   Al desconectar físicamente un mando, el evento SDL o la
    reconciliación actualiza su estado sin reiniciar.

-   Conectar dos mandos del mismo modelo no produce una única tarjeta si
    son dispositivos distintos.

-   Retirar y volver a conectar un mando no deja handles SDL abiertos ni
    registros duplicados.

-   Un dispositivo no reconocido como SDL Gamepad se maneja con fallback
    Joystick si está implementado, o se reporta como no compatible sin
    cerrar la aplicación.

-   La batería no disponible se muestra como N/A y no como 0%.

-   Cerrar la aplicación detiene el procesamiento de eventos y libera
    los recursos SDL.

## 20. Instrucción SDL adicional para GitHub Copilot

Integra SDL como la fuente principal de mandos conectados en la pestaña
Mandos. Primero identifica si el proyecto usa SDL2 o SDL3 y reutiliza
esa versión; no mezcles APIs. Crea o adapta un servicio SDL que enumere
gamepads conectados, traduzca los dispositivos a ControllerDevice,
gestione eventos de conexión/desconexión y libere correctamente los
handles. SDL no debe ser tratado como el sistema de emparejamiento ni
como un escáner Bluetooth: usa Windows para dispositivos emparejados
desconectados, descubrimiento y emparejamiento Bluetooth. Combina SDL,
Windows y la base de datos sin duplicados, mantén los estados
independientes y actualiza la UI sin reiniciar. Revisa la
documentación/headers de la versión SDL instalada, compila y reporta
cualquier limitación real de detección, mapping, batería o conexión.
