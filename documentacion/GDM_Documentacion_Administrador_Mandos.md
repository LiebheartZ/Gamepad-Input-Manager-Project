# GDM Input Manager — Especificación para rehacer el administrador de mandos

**Versión:** 1.0  
**Alcance:** rediseño del subsistema de lista, detección, identidad, estado y persistencia de mandos físicos.  
**Aplicación:** C# / WinForms / .NET 10 Windows; SDL2 ya está integrado.  
**Objetivo futuro:** servir como base a Tester, remapeo, mandos virtuales y funciones HidHide, sin implementar esas funciones en este alcance.

---

## 1. Objetivo y criterios de diseño

Rehacer el administrador para que sea fiable, mantenible y ampliable. La lista debe representar mandos físicos detectados y mandos que el usuario ha guardado, sin confundir ambas entidades.

Principios obligatorios:

1. **Separar identidad persistente y conexión viva.** Un registro guardado no equivale a un dispositivo conectado.
2. **No identificar un dispositivo solo por VID/PID.** VID/PID describe principalmente un modelo/familia; varias unidades pueden compartirlos.
3. **La interfaz no detecta hardware ni accede a JSON.** La UI presenta estado y envía comandos a la capa de aplicación.
4. **Un solo coordinador administra el estado.** Evitar varios temporizadores, escaneos o servicios que actualicen la misma lista de forma independiente.
5. **Eventos y concurrencia controlados.** Las notificaciones de hardware pueden llegar repetidas o en ráfagas.
6. **La persistencia no decide qué está conectado.** El detector es la fuente del estado físico actual; el repositorio es la fuente de registros guardados.
7. **Diseñar contratos para futuras funciones, no acoplarlas ahora.** Tester/remapeo/virtual/HidHide se conectarán después por interfaces.
8. **Errores parciales no deben tumbar la lista.** Un fallo de Bluetooth no debe impedir enumerar SDL; un archivo JSON inválido no debe impedir cargar los demás.

## 2. Estado actual observado en el proyecto

Archivos relacionados que existen en el proyecto:

- `Models/Gamepad.cs`
- `Services/IGamepadDetector.cs`
- `Services/SDL2GamepadDetector.cs`
- `Services/GamepadDatabaseService.cs`
- `Services/WindowsBluetoothConnectionService.cs`
- `Services/WindowsUsbConnectionService.cs`
- `Data/JsonDataManager.cs`
- `Controls/MandosControl.cs`

`MandosControl.cs` contiene responsabilidades mezcladas: inicialización, detección/refresh, reconciliación con registros guardados, búsqueda Bluetooth, escaneo Bluetooth, notificaciones Windows (`WndProc`/device notification), tarjetas UI, sprites, acciones Bluetooth y persistencia. Es el principal punto que debe adelgazar.

`SDL2GamepadDetector` actualmente devuelve objetos `Gamepad`, además de identificar modelos y resolver conexión con ayuda de servicios Windows. Esa implementación mezcla enumeración, clasificación y enriquecimiento de transporte.

`GamepadDatabaseService` administra archivos y también ofrece búsquedas por hardware, lista de conectados y lógica de eliminación. Debe quedar limitado al repositorio/registro persistido; no puede ser la fuente de conexión activa.

`Gamepad` combina campos de registro, datos de hardware, perfil/configuración y `IsConnected`. En el nuevo diseño, el registro guardado y el dispositivo conectado deben ser modelos separados. No eliminar campos del JSON sin migración compatible.

> Nota de revisión: la especificación define la arquitectura objetivo. Antes de sustituir archivos, revisar todos los usos de `Gamepad`, enums, eventos y métodos en el resto del proyecto para mantener compatibilidad o actualizar consumidores deliberadamente.

## 3. Capas y responsabilidades

### 3.1 Presentación — `Controls`

Responsabilidad: representar estado y emitir acciones del usuario.

- `MandosControl`: mostrar lista, estado vacío, errores visibles, botón refrescar, seleccionar/guardar/renombrar/eliminar registro.
- Convierte `GamepadListItem` en tarjetas/filas visuales.
- Suscribe eventos del `IGamepadManager` al inicializar; desuscribe al destruir.
- Ejecuta cambios de UI en el hilo WinForms (`Invoke`/`BeginInvoke` o un dispatcher abstraído).
- No llama a SDL, HID, Windows Bluetooth, `JsonDataManager` ni `GamepadDatabaseService`.
- No hace matching de hardware, clasificación por VID/PID ni deduplicación.
- No mantiene su propia lista paralela como autoridad.

Mantener en UI la carga de sprites, formato visual, tooltips y diseño de tarjetas. Mover fuera la resolución de identidad, conexiones y lógica de datos.

### 3.2 Aplicación — `Services` o proyecto `Application`

Responsabilidad: orquestar casos de uso y mantener el estado actual.

Componentes:
- `GamepadManager`: API pública y coordinador de la lista.
- `GamepadDetectionService`: ciclo de enumeración, refresh y reacción a cambios.
- `GamepadIdentityService`: reglas de correlación entre detecciones y registros guardados.
- `GamepadListPresenter` opcional: transforma estado de aplicación a `GamepadListItem`; puede omitirse si el manager expone DTOs.

No conoce controles WinForms ni APIs nativas. Usa interfaces de dominio/infraestructura.

### 3.3 Dominio — `Models` / `Domain`

Responsabilidad: tipos y reglas independientes de Windows, SDL y JSON.

Entidades/valores:
- `GamepadRecord`: registro persistente y personalizable.
- `GamepadDevice`: observación de dispositivo detectado.
- `GamepadConnectionState`: estado de conexión.
- `GamepadIdentity`: identidad/candidatos de identidad.
- `GamepadListItem`: proyección para mostrar registro + estado.
- `GamepadType`, `ConnectionType`: enums normalizados.

### 3.4 Infraestructura — `Infrastructure`

Responsabilidad: hablar con APIs concretas.

- `SDL2GamepadProvider`: enumerar dispositivos mediante SDL2.
- `WindowsUsbDeviceProvider`: aportar información USB si está disponible.
- `WindowsBluetoothDeviceProvider`: aportar información Bluetooth si está disponible.
- `JsonGamepadRepository`: leer/escribir registros JSON.
- `WindowsDeviceChangeMonitor`: notificar cambios PnP; no realiza matching ni construye tarjetas.

El enriquecimiento USB/Bluetooth debe ser opcional. Si no puede obtenerse el transporte o dirección, el dispositivo continúa apareciendo con `Unknown`.

## 4. Estructura de carpetas sugerida

La reorganización puede hacerse dentro del proyecto actual, sin obligar a dividirlo en varios `.csproj` inmediatamente.

```text
Models/
  GamepadRecord.cs
  GamepadDevice.cs
  GamepadIdentity.cs
  GamepadListItem.cs
  GamepadType.cs
  ConnectionType.cs
  GamepadConnectionState.cs

Interfaces/
  IGamepadProvider.cs
  IGamepadRepository.cs
  IGamepadManager.cs
  IDeviceChangeMonitor.cs

Services/
  GamepadManager.cs
  GamepadDetectionService.cs
  GamepadIdentityService.cs

Infrastructure/
  SDL2/
    SDL2GamepadProvider.cs
  Windows/
    WindowsDeviceChangeMonitor.cs
    WindowsUsbDeviceProvider.cs
    WindowsBluetoothDeviceProvider.cs
  Persistence/
    JsonGamepadRepository.cs

Controls/
  MandosControl.cs        # UI únicamente

Data/
  JsonDataManager.cs      # utilitario genérico, o retirarlo si el repositorio lo reemplaza
```

No se debe duplicar lógica en carpetas antiguas y nuevas. Al terminar la migración, eliminar clases obsoletas o convertirlas en adaptadores temporales con una fecha de retirada.

## 5. Modelos y contratos

### 5.1 `GamepadRecord` — dato guardado

Representa el mando reconocido/guardado por el usuario, no una sesión activa.

Campos recomendados:
- `RecordId: Guid` — identificador interno estable.
- `DisplayName: string` — nombre personalizable.
- `GamepadType: GamepadType` — tipo/modelo conocido o `Unknown`.
- `ModelName: string` — etiqueta de modelo.
- `Identity: GamepadIdentity` — identificadores observados que ayudan a reencontrarlo.
- `CreatedAtUtc`, `LastSeenAtUtc: DateTimeOffset?`.
- `ActiveProfileId: string?` — conservar como referencia futura.
- `EnabledFeatures`, `Settings` — conservar solo si siguen siendo parte del formato actual; validar tipos JSON.
- `SchemaVersion: int` — versión del formato de persistencia.

No guardar `IsConnected` como verdad persistente. La conexión es runtime. Si el JSON existente lo contiene, ignorarlo durante la lectura o mantenerlo como campo legacy hasta completar migración.

### 5.2 `GamepadDevice` — dispositivo detectado

Observación de hardware en el ciclo actual:
- `DeviceKey: string` — clave runtime del provider, válida para esa sesión/proveedor.
- `Provider: string` — por ejemplo `SDL2`.
- `ProviderIndex: int?` — índice SDL solo informativo, no identidad persistente.
- `Name: string`.
- `Vid`, `Pid: ushort?`.
- `SerialNumber: string?`.
- `BluetoothAddress: string?` — si el sistema lo expone.
- `InstancePath: string?` — identificador PnP de Windows, si disponible.
- `ConnectionType: ConnectionType`.
- `GamepadType: GamepadType`.
- `IsConnected: bool` — estado físico observado en el refresh actual.
- `IsPaired: bool` — estado de emparejamiento informado por Windows; no implica conexión activa.
- `CanUnpair: bool` — indica si Windows permite quitar el vínculo Bluetooth.
- `FirstSeenUtc`, `LastSeenUtc: DateTimeOffset`.
- `Capabilities`: datos opcionales, no necesarios para la primera fase.

No almacenar handles nativos en el modelo de UI ni serializarlos.

### 5.3 `GamepadIdentity`

Separar atributos de identidad y atributos de modelo:

- Identidad fuerte, cuando está disponible: InstancePath/ContainerId/serial/MAC accesible.
- Identidad de sesión: `Provider + DeviceKey`.
- Identidad de modelo: VID/PID, nombre normalizado, tipo.
- La identidad fuerte puede cambiar entre conexión USB/Bluetooth o entre drivers; tratarla como evidencia, no asumir que siempre es universal.

No crear un ID permanente determinista solo a partir del nombre, VID/PID o índice SDL.

### 5.4 `GamepadListItem`

DTO de UI calculado por aplicación:
- `RecordId?`
- `DisplayName`
- `ModelName`
- `GamepadType`
- `ConnectionType`
- `IsConnected`
- `IsPaired` — estado de Windows separado de la conexión activa.
- `ConnectionStatus` (Connected/Disconnected/Unknown)
- `DeviceKey?`
- `CanRename`, `CanRemove`
- `LastSeenUtc?`

Un dispositivo detectado pero no guardado puede aparecer como “nuevo/no guardado”. Un registro guardado sin dispositivo activo aparece “desconectado”. No generar una tarjeta duplicada si la detección se asocia a un registro existente.

### 5.5 Enums

`GamepadType`: conservar valores actuales y añadir `Unknown`/`Generic` si no existen. No cambiar valores numéricos existentes si están serializados.

`ConnectionType`: `Usb`, `Bluetooth`, `WirelessProprietary`, `Unknown`. Mantener compatibilidad de nombres JSON existentes (`USB`, etc.) con conversores o migración.

`GamepadConnectionState`: `Connected`, `Disconnected`, `Unknown`.

## 6. Interfaces requeridas

### 6.1 Provider de detección

Contrato conceptual:

```csharp
public interface IGamepadProvider : IDisposable
{
    string Name { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<GamepadDevice>> EnumerateAsync(
        CancellationToken cancellationToken);
}
```

Reglas:
- El provider no escribe JSON.
- No crea controles visuales.
- No decide qué registro guardado coincide.
- `EnumerateAsync` devuelve una instantánea; no muta la colección del manager.
- Manejar errores con excepción tipada/log y permitir que el manager conserve el último estado válido.
- SDL puede usar internamente llamadas síncronas, pero debe ejecutarse en una estrategia segura de hilo y no bloquear la UI.

### 6.2 Repositorio

```csharp
public interface IGamepadRepository
{
    Task<IReadOnlyList<GamepadRecord>> GetAllAsync(CancellationToken ct);
    Task<GamepadRecord?> GetByIdAsync(Guid id, CancellationToken ct);
    Task AddAsync(GamepadRecord record, CancellationToken ct);
    Task UpdateAsync(GamepadRecord record, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}
```

Responsabilidades:
- Serializar/deserializar.
- Validar archivos y recuperar registros válidos.
- Guardado atómico (archivo temporal + reemplazo/move).
- No detectar mandos ni conocer SDL.
- No usar VID/PID como clave única.
- No borrar archivos no relacionados si un JSON está corrupto.

### 6.3 Manager de aplicación

```csharp
public interface IGamepadManager
{
    IReadOnlyList<GamepadListItem> CurrentItems { get; }
    event EventHandler<GamepadListChangedEventArgs>? ListChanged;
    event EventHandler<GamepadErrorEventArgs>? ErrorOccurred;

    Task InitializeAsync(CancellationToken ct = default);
    Task RefreshAsync(CancellationToken ct = default);
    Task<GamepadRecord> SaveDetectedDeviceAsync(string deviceKey, CancellationToken ct = default);
    Task RenameAsync(Guid recordId, string displayName, CancellationToken ct = default);
    Task RemoveRecordAsync(Guid recordId, CancellationToken ct = default);
}
```

Si se requiere eliminar/olvidar el registro sin desinstalar/unpair el dispositivo, la operación debe llamarse `RemoveRecordAsync` o `ForgetAsync`; no debe confundirse con `UnpairBluetoothAsync`.

`CurrentItems` debe exponer una instantánea de solo lectura. No devolver una `List<T>` interna modificable.

### 6.4 Monitor de cambios de dispositivo

```csharp
public interface IDeviceChangeMonitor : IDisposable
{
    event EventHandler? DevicesChanged;
    void Start();
    void Stop();
}
```

El monitor solo avisa “algo cambió”. El manager programa un refresh con debounce. No ejecutar una enumeración por cada mensaje de Windows.

## 7. Responsabilidades detalladas por clase

### `GamepadManager`
- Inicializar repositorio/provider/monitor.
- Cargar registros persistidos.
- Mantener la instantánea de dispositivos activos y la lista combinada.
- Coordinar refreshes y evitar que se ejecuten concurrentemente.
- Aplicar la correlación `GamepadDevice` ↔ `GamepadRecord`.
- Publicar un evento únicamente cuando la lista realmente cambie.
- Implementar guardar, renombrar y olvidar.
- Actualizar `LastSeenUtc` de forma controlada.
- Cancelar tareas y liberar recursos en `DisposeAsync`/cierre.
- No conocer `Control`, `FlowLayoutPanel`, `Image`, `MessageBox`.

### `GamepadDetectionService`
- Inicializar y consultar providers.
- Capturar errores de cada provider por separado.
- Normalizar los resultados.
- Filtrar entradas inválidas y duplicados obvios dentro de una misma instantánea.
- No eliminar dispositivos del último estado si la enumeración falló por error.
- Diferenciar “enumeración correcta y cero dispositivos” de “enumeración fallida”.

### `GamepadIdentityService`
- Aplicar las reglas de matching del apartado 8.
- Producir una asociación con nivel de confianza y razón.
- No unir dos mandos solo porque tienen el mismo VID/PID.
- Si la asociación es ambigua, dejar el dispositivo como no asociado y pedir decisión al usuario, en vez de elegir arbitrariamente.

### `SDL2GamepadProvider`
- Inicializar SDL una sola vez por propietario de SDL.
- Enumerar game controllers/joysticks y obtener nombre, VID/PID y datos disponibles.
- Mantener coherencia entre índice de enumeración e instance ID de SDL.
- No abrir/cerrar dispositivos repetidamente si eso afecta a futuras lecturas; acordar la política de handles antes de implementar Tester.
- Liberar handles y SDL en cierre.
- No clasificar Bluetooth por coincidencia aproximada de nombres como dato definitivo.
- Los detalles de transporte se pueden enriquecer desde Windows; si no hay certeza, usar `Unknown`.

### `JsonGamepadRepository`
- Conservar la ubicación actual de datos del usuario si es conocida.
- Leer los formatos legacy.
- Validar `RecordId`, nombre, enums y valores opcionales.
- Guardar cada registro de manera segura.
- Informar errores con archivo y operación, sin exponer stack trace en UI.
- Migrar con backup y no destruir archivos originales si la migración falla.

### `MandosControl`
- Recibir `IGamepadManager` por constructor/fábrica o mediante composición en `Main`.
- Renderizar `CurrentItems`.
- Al recibir `ListChanged`, actualizar UI en hilo WinForms.
- Delegar refresh, renombrar, guardar y eliminar al manager.
- Mantener solo estado visual local (selección, filtro, scroll).
- Registrar y desregistrar eventos en `Dispose`/ciclo de vida.
- No tener `_gamepadDetector`, `_databaseService`, escaneo Bluetooth ni lógica de identidad.
- La notificación Windows debe salir de este control y pasar a `WindowsDeviceChangeMonitor` o al host de aplicación.

## 8. Reglas de identidad y reconciliación

Aplicar orden de evidencia, con cuidado de no asociar erróneamente dos unidades:

1. **Clave runtime del provider** para identificar el mismo dispositivo entre snapshots del mismo ciclo/proveedor.
2. **InstancePath/ContainerId/serial** cuando sea fiable y estable en el contexto.
3. **Dirección Bluetooth** normalizada, si realmente corresponde al mando y está disponible.
4. **Coincidencia compuesta** de modelo + datos adicionales, solo si hay exactamente un candidato.
5. **VID/PID, nombre o tipo solos:** jamás suficientes para afirmar que es la misma unidad física.

Consecuencias:
- Dos mandos idénticos deben poder aparecer simultáneamente.
- La identidad de modelo sirve para icono, etiquetas y compatibilidad, no como primary key.
- La asociación puede ser “posible” y no “confirmada”.
- Si el usuario guarda un dispositivo nuevo, se crea un `RecordId` propio.
- La asociación entre un registro y la unidad detectada puede actualizarse si llega evidencia más fuerte.
- Si una dirección/serial está ausente, no inventarla.
- Normalizar MAC quitando separadores y usando un formato canónico; comparar sin distinguir mayúsculas.
- No usar el nombre Bluetooth como clave exclusiva: puede repetirse o cambiar.

### Reconciliación cada refresh

1. Enumerar dispositivos (snapshot).
2. Si enumeración falló, emitir error y conservar snapshot anterior.
3. Si fue correcta, normalizar y deduplicar la instantánea.
4. Asociar dispositivos con registros usando IdentityService.
5. Marcar como conectados los registros asociados.
6. Mantener registros guardados restantes como desconectados.
7. Mostrar dispositivos sin registro como nuevos/no guardados.
8. Ordenar de forma estable: conectados primero, luego guardados desconectados; mantener criterio consistente.
9. Comparar la lista nueva con la anterior.
10. Emitir `ListChanged` solo si cambió identidad, estado, etiqueta o información relevante.

No usar un “snapshot string” de propiedades concatenadas como mecanismo principal de comparación; utilizar igualdad explícita o una firma tipada y estable.

## 9. Ciclo de vida, eventos y concurrencia

- `InitializeAsync` debe ser idempotente: no inicializar dos veces SDL o monitores.
- Solo un refresh puede estar activo. Las llamadas adicionales se coalescen o esperan.
- Usar `SemaphoreSlim` o mecanismo equivalente para serializar refreshes.
- Usar `CancellationToken` en tareas y al cerrar.
- Debounce sugerido para notificaciones PnP: 250–500 ms; es configurable, no una garantía del sistema operativo.
- Un refresh manual puede solicitar ejecución inmediata, pero no debe solaparse con otro.
- Los eventos no se invocan bajo locks.
- Evitar `async void` salvo handlers de UI.
- Capturar errores por etapa y emitir diagnóstico; no convertir error en lista vacía.
- Si un servicio Bluetooth falla, seguir mostrando los dispositivos enumerados por SDL.
- Al cerrar: detener monitor/timer, cancelar refresh, desuscribir eventos, liberar provider y handles nativos.

## 10. Persistencia y migración JSON

### Formato recomendado
Un archivo de colección versionado o archivos individuales por registro pueden funcionar. Mantener el esquema actual durante la primera iteración reduce riesgo. Elegir una única estrategia y documentarla.

Campos que requieren migración/compatibilidad:
- `Id` string actual → `RecordId` Guid (validar y conservar si es GUID).
- `Name` → `DisplayName`.
- `Type`, `ConnectionType`, `Vid`, `Pid`, `MacAddress`, `SerialNumber`, `ModelName`.
- `LastUsed` → `LastSeenUtc` solo si su significado es equivalente; no reinterpretar sin verificar.
- `ActiveProfileId`, `EnabledFeatures`, `Settings` si siguen utilizándose.
- `IsConnected` debe ignorarse como estado actual; no confiar en él tras reiniciar.

### Reglas de seguridad de datos
- Backup antes de migración.
- Guardar temporalmente y reemplazar atómicamente.
- Validar por registro: uno malo no debe impedir recuperar todos los demás.
- No eliminar un registro por ausencia temporal del dispositivo.
- “Olvidar registro” es una acción explícita.
- Registrar migración, versión origen/destino y errores.
- No guardar datos sensibles ni direcciones de hardware innecesarias; limitar persistencia a lo requerido.

## 11. Clasificación de mandos y transporte

Separar estas dos preguntas:

- **¿Qué modelo/familia parece ser?** `GamepadType`, basado en VID/PID, SDL mapping y otros datos confiables.
- **¿Cómo está conectado ahora?** `ConnectionType`, basado en datos actuales de Windows/SDL.

Reglas:
- VID/PID puede identificar familias conocidas de Sony/Microsoft, pero no siempre el modelo exacto o el modo de conexión.
- “Xbox compatible” no garantiza que todos los dispositivos sean XInput.
- PlayStation por Bluetooth puede presentar datos diferentes de USB según driver/stack.
- Genéricos y dispositivos no reconocidos deben aparecer como `Generic`/`Unknown`, no ser descartados.
- Evitar “Bluetooth” inferido únicamente por nombre si se puede mostrar `Unknown`.
- La clasificación no debe ser condición para aparecer en la lista.

## 12. API de acciones de lista

Operaciones de usuario que el manager debe soportar:

- `RefreshAsync`: actualizar instantánea.
- `SaveDetectedDeviceAsync(deviceKey)`: guardar una detección no guardada; comprobar que sigue existiendo.
- `RenameAsync(recordId, displayName)`: validar nombre, trim, longitud máxima y unicidad solo si el producto la requiere.
- `RemoveRecordAsync(recordId)`: quitar registro persistente; no desconectar físicamente ni desparear.
- `GetByRecordId` / selección: resolver registro con ID estable, nunca por índice de tarjeta.
- `GetByDeviceKey`: solo runtime y mientras la conexión exista.

Validaciones:
- ID vacío/no válido.
- Nombre nulo/vacío, espacios, longitud excesiva.
- El dispositivo se desconectó entre el click y la acción.
- Registro ya eliminado.
- Conflicto de escritura.
- Error de provider/repository.

## 13. UI: contrato de estados

La UI debe distinguir al menos:

- **Conectado:** detección actual exitosa y dispositivo asociado.
- **Desconectado:** registro guardado, no presente en la enumeración exitosa.
- **Nuevo/no guardado:** dispositivo presente sin registro asociado.
- **Estado no actualizado:** último refresh falló; conservar la información anterior y mostrar aviso.
- **Sin mandos:** enumeración exitosa y lista vacía.
- **Cargando:** refresh en curso.

No presentar “sin mandos” si la detección falló. Evitar que un refresh reconstruya todas las tarjetas si solo cambió un estado; para primera versión se acepta repintado completo si la lista es pequeña, pero conservar selección y scroll.

## 14. Logs y diagnóstico

Registrar:
- Inicio/cierre del manager.
- Provider seleccionado e inicialización.
- Duración y resultado de enumeración.
- Número de dispositivos encontrados.
- Asociaciones de identidad y nivel de confianza (sin exponer información personal innecesaria).
- Cambios conectado/desconectado.
- Errores de lectura/escritura/migración.
- Refreshes coalescidos o cancelados.

No escribir en log cada tick ni cada frame. Nunca mostrar stack trace al usuario final; guardar detalle en log y ofrecer mensaje comprensible.

## 15. Plan de implementación por etapas

### Etapa 0 — inventario y respaldo
- Crear branch/copia de seguridad.
- Buscar todas las referencias a `Gamepad`, `IGamepadDetector`, `GamepadDatabaseService`, métodos de Bluetooth y `IsConnected`.
- Anotar consumidores de `ActiveProfileId`, `Settings`, `EnabledFeatures`.
- Confirmar formato y ubicación de JSON real.

### Etapa 1 — contratos y modelos
- Añadir modelos nuevos sin borrar todavía `Gamepad`.
- Definir interfaces provider, repository, manager y change monitor.
- Escribir tests de identidad con dispositivos duplicados.

### Etapa 2 — persistencia
- Implementar `JsonGamepadRepository`.
- Leer formato actual.
- Añadir migración/backup y tests de archivos corruptos.
- Asegurar que guardar/renombrar/eliminar no altere el estado conectado.

### Etapa 3 — provider SDL2
- Adaptar `SDL2GamepadDetector` a `SDL2GamepadProvider`.
- Extraer clasificación y transporte.
- No acoplar SDL a los modelos persistentes.
- Verificar dos mandos idénticos conectados simultáneamente.

### Etapa 4 — manager y reconciliación
- Implementar `GamepadDetectionService`, `GamepadIdentityService`, `GamepadManager`.
- Implementar serialización de refreshes, debounce, eventos y snapshots.
- Crear pruebas para llegada/salida y fallos de enumeración.

### Etapa 5 — integrar UI
- Rehacer `MandosControl` como vista.
- Mantener diseño de tarjetas y sprites.
- Sacar Win32 notification/PInvoke del control.
- Reemplazar operaciones directas a DB por comandos del manager.

### Etapa 6 — retirar código anterior
- Eliminar lógica duplicada y métodos obsoletos.
- Retirar interfaces/clases legacy cuando no tengan consumidores.
- Revisar la solución completa y compilar en configuración limpia.

## 16. Pruebas obligatorias / criterios de aceptación

### Detección
- Conectar un Xbox por USB: aparece una sola vez.
- Conectar un DualShock 4 o DualSense por USB y Bluetooth: aparece con datos disponibles y no se bloquea si faltan datos.
- Conectar un mando genérico desconocido: aparece como Generic/Unknown.
- Conectar dos mandos del mismo modelo/VID/PID: aparecen como dos unidades separadas.
- Desconectar uno de dos mandos iguales: solo ese queda desconectado.
- Desconectar/reconectar el mismo dispositivo: no crea registros duplicados automáticamente.
- Enumeración vacía correcta: muestra estado vacío.
- Enumeración fallida: conserva último estado y muestra error/estado no actualizado.

### Persistencia
- Reiniciar aplicación: registros siguen disponibles como desconectados.
- Renombrar: persiste y se conserva después de reiniciar.
- Olvidar registro: elimina solo ese registro.
- JSON corrupto: el resto de registros sigue cargando.
- Migración legacy: conserva IDs y configuraciones cuando son válidos.
- No se elimina registro al desconectar el hardware.

### Concurrencia / UI
- Refresh repetido rápido: no genera carreras ni tarjetas duplicadas.
- Notificaciones de Windows repetidas: coalescen.
- Cerrar app durante refresh: no hay excepción ni fuga de recursos.
- La UI nunca se actualiza desde un hilo de fondo.
- Selección y scroll se conservan en refresh.
- Un error Bluetooth no bloquea SDL.

### Calidad
- Compilación limpia.
- No hay llamadas a SDL/JSON/Windows PnP dentro de `MandosControl`.
- No hay lógica de identidad en la UI.
- No se usa índice de lista como ID.
- No se usa VID/PID solo para identidad de unidad.

## 17. Riesgos y decisiones que deben documentarse

1. **SDL thread safety y handles:** SDL debe tener un propietario y una política clara para inicialización, enumeración, lectura y cierre. El futuro Tester podría requerir handles persistentes; diseñar sin abrir/cerrar continuamente.
2. **Identidad entre USB y Bluetooth:** puede no ser posible demostrar que ambos transportes son la misma unidad. No fusionar automáticamente sin evidencia.
3. **Dispositivos con datos incompletos:** deben aparecer aunque falte serial/MAC/transport.
4. **Múltiples providers:** si en el futuro SDL y HID enumeran el mismo mando, la deduplicación entre providers requiere evidencia compartida; no fusionar solo por VID/PID.
5. **Virtuales:** cuando se agreguen, etiquetar origen `Physical`/`Virtual` y excluir virtuales de la lista de físicos si el usuario selecciona ese filtro. No confundirlos con los mandos reales.
6. **HidHide:** controlarlo mediante una integración separada; no hacer que la lista dependa de que HidHide esté instalado o activo.
7. **Compatibilidad del JSON:** no romper perfiles ni preferencias existentes.

## 18. Qué queda fuera de esta reconstrucción

No implementar todavía:
- Lectura de botones/ejes a alta frecuencia.
- Tester y visualización de inputs.
- Motor de remapeo.
- Salida a mando virtual (ViGEm u otra tecnología).
- Reglas de ocultación HidHide.
- Instalación/configuración de drivers.

Sí se deben dejar interfaces y modelos extensibles para esas funciones, evitando acoplarlas al listado.

## 19. Definición de terminado

El subsistema está terminado cuando:
- detecta y presenta unidades físicas sin duplicados;
- diferencia registros guardados de conexiones activas;
- funciona con mandos Xbox, PlayStation y genéricos soportados por SDL;
- soporta ausencia de identificadores secundarios;
- la UI no contiene lógica de detección/persistencia;
- los refreshes y eventos son seguros;
- el JSON legacy se conserva/migra;
- los tests de aceptación pasan;
- el proyecto compila y el sistema puede extenderse sin modificar la UI para añadir un provider.

---

## Apéndice A — Mapeo de archivos actuales a destino

| Archivo actual | Acción | Destino / motivo |
|---|---|---|
| `Models/Gamepad.cs` | Sustituir gradualmente; migrar consumidores | Separar `GamepadRecord` y `GamepadDevice`; conservar datos legacy y enums compatibles |
| `Services/IGamepadDetector.cs` | Reemplazar por contrato nuevo o adaptar | `IGamepadProvider` devuelve observaciones, no registros persistentes |
| `Services/SDL2GamepadDetector.cs` | Refactorizar | `Infrastructure/SDL2/SDL2GamepadProvider.cs`; enumeración SDL aislada |
| `Services/GamepadDatabaseService.cs` | Retirar o reducir a adaptador temporal | `JsonGamepadRepository` persiste; `GamepadManager` combina estado |
| `Data/JsonDataManager.cs` | Reutilizar como helper o reemplazar | Repositorio implementa validación, versionado y guardado atómico |
| `Services/WindowsBluetoothConnectionService.cs` | Mantener como backend de enriquecimiento, revisar API | No debe determinar identidad solo por nombre ni controlar lista |
| `Services/WindowsUsbConnectionService.cs` | Mantener como backend de enriquecimiento, revisar API | Transporte/identificadores PnP opcionales |
| `Controls/MandosControl.cs` | Refactorización principal | UI; quitar SDL, DB, escaneo y matching |
| `Controls/MandosControl.Designer.cs` | Mantener salvo cambios visuales | No mezclar lógica de negocio en Designer |
| `Controls/TesterControl.cs` | No implementar en esta etapa | Consumirá después un servicio de lectura de inputs |
| `Controls/HidHideControl.cs` | No acoplar a manager | Integración HidHide separada |
| `Models/AppSettings.cs` | Revisar | Preferencias de lista/auto-refresh si existen; no guardar estado físico |
| `Services/LoggerService.cs` | Reutilizar | Añadir eventos/diagnóstico del manager y providers |

## Apéndice B — Reglas de implementación que no deben omitirse

- No usar `async void` en servicios.
- No usar `Task.Run` indiscriminadamente para SDL; documentar el hilo propietario.
- No emitir eventos mientras se mantiene un lock.
- No mutar colecciones expuestas a UI.
- No comparar listas por índice.
- No usar nombre del dispositivo como clave primaria.
- No asumir que SDL index y SDL instance ID son lo mismo.
- No confundir fallo de detección con cero dispositivos.
- No borrar un mando guardado porque ya no está conectado.
- No ejecutar unpair Bluetooth al olvidar un registro.
- No realizar llamadas P/Invoke desde la vista.
- No permitir que una excepción de una tarjeta impida mostrar las demás.
- Validar y normalizar los datos que vienen de hardware antes de persistirlos.
