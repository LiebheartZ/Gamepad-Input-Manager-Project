# Instrucciones para corregir la detección y actualización de mandos
## Proyecto: GDM Input Manager (C# / WinForms)

### Objetivo

Corregir los problemas actuales sin rediseñar la interfaz:

- Los mandos emparejados en Windows dejan de aparecer en la lista.
- Al desconectar un mando, la aplicación no actualiza su estado.
- Al pulsar **Actualizar**, un mando puede seguir apareciendo como conectado hasta reiniciar el programa.

La corrección debe centrarse en la detección, la reconciliación de dispositivos y el ciclo de actualización. Mantener el diseño visual y los controles existentes, salvo que sea imprescindible añadir un indicador de estado.

---

## 1. Separar los conceptos de dispositivo

No tratar estos estados como equivalentes:

- **Emparejado/registrado en Windows:** Windows conoce el dispositivo Bluetooth.
- **Conectado ahora:** el dispositivo mantiene una conexión activa.
- **Detectado por SDL2:** SDL2 lo expone como un gamepad utilizable.
- **Guardado en la base de datos de la aplicación:** existe una ficha local del dispositivo.

La presencia de un registro de Windows o de una ficha guardada no demuestra que el mando esté conectado. Del mismo modo, que SDL2 no lo detecte no debe eliminar un mando emparejado de la lista de dispositivos disponibles.

Representar por separado, según corresponda, `IsPaired`, `IsConnected`, `IsDetectedBySDL` y `IsSaved`. Si el proyecto ya tiene propiedades equivalentes, reutilizarlas en lugar de duplicarlas.

## 2. Revisar la enumeración de Bluetooth de Windows

Archivo/zona a revisar: `WindowsBluetoothGamepadProvider`.

1. Enumerar los dispositivos Bluetooth de Windows, incluidos los emparejados que estén desconectados.
2. No depender de SDL2 para decidir si un dispositivo emparejado debe aparecer en la lista.
3. No descartar dispositivos solo porque `IsConnected` sea falso.
4. Verificar que los filtros de tipo de dispositivo, perfil o nombre no excluyan mandos válidos.
5. Distinguir una enumeración exitosa que devuelve cero dispositivos de una excepción o fallo del provider.
6. Cuando el provider falle, devolver un resultado de error explícito; no convertir el fallo en una lista vacía.

Revisar también el estado de `_includeAvailableBluetooth` y su uso en `GamepadManager.ReconcileAsync`. La opción debe controlar la visibilidad de los dispositivos emparejados/disponibles de forma consistente y no cambiar inadvertidamente durante una actualización.

## 3. Corregir la reconciliación entre Windows y SDL2

Zona a revisar: `GamepadManager.ReconcileAsync` y los providers de detección.

1. Crear primero una lista de dispositivos Windows y otra de dispositivos SDL2.
2. Asociar ambas fuentes solo cuando exista una identidad suficientemente fiable.
3. No emparejar dispositivos únicamente por nombre, familia o VID/PID cuando pueda haber más de una unidad coincidente.
4. Si la asociación es ambigua, mantener los registros separados o marcar la coincidencia como incierta; no fusionarlos automáticamente.
5. Si Windows indica que un dispositivo está desconectado, no conservar `IsConnected = true` solo porque exista un objeto SDL2 anterior.
6. Si SDL2 deja de enumerar un mando, retirar su estado de conexión activa en la nueva instantánea.
7. Conservar la ficha guardada del mando aunque ya no esté conectado.

La reconciliación debe producir una instantánea nueva del estado actual. No debe limitarse a añadir los dispositivos recién detectados a la lista anterior.

## 4. Hacer que cada refresco reemplace el estado actual

Zona a revisar: `GamepadManager.RefreshAsync`.

En cada actualización manual o automática:

1. Consultar de nuevo los providers correspondientes.
2. Construir una instantánea completa y consistente.
3. Calcular el estado conectado/desconectado a partir de los resultados actuales.
4. Reemplazar la instantánea anterior con la nueva cuando la consulta haya sido válida.
5. Publicar el evento de cambio cuando cambien los dispositivos o sus estados, no solo cuando cambie el número de elementos.
6. Asegurar que los cambios de `IsConnected`, `IsPaired` y otros estados relevantes formen parte de la comparación.
7. Evitar que una lista antigua o una referencia mutable mantenga un mando conectado después de desconectarlo.

Si la enumeración falla, conservar la última instantánea válida y comunicar el error. No interpretar el fallo como una desconexión masiva ni como una lista vacía.

## 5. Revisar el monitor de conexión y desconexión

Zona a revisar: `WindowsDeviceChangeMonitor`.

1. Confirmar que los eventos de llegada y retirada de dispositivos se reciben correctamente.
2. Verificar que ambos eventos programen una actualización; no atender solo la llegada.
3. Aplicar una pequeña agrupación/debounce de eventos para evitar múltiples refrescos consecutivos.
4. Ejecutar la enumeración fuera del hilo de interfaz.
5. Asegurar que los eventos del monitor no se pierdan por una excepción.
6. Comprobar que el monitor se inicia una sola vez y se libera correctamente al cerrar la aplicación.
7. Si el evento de Windows no se recibe, la actualización manual debe seguir pudiendo reconstruir el estado desde cero.

No confiar exclusivamente en el monitor de eventos: debe ser una señal para volver a enumerar, no la fuente definitiva del estado.

## 6. Corregir el botón «Actualizar»

Seguir el flujo completo desde el evento del botón de la interfaz hasta `RefreshAsync`.

El botón debe:

- Solicitar una enumeración nueva de Windows y SDL2.
- No reutilizar la lista visual como fuente de datos.
- No limitarse a volver a dibujar los elementos actuales.
- Mostrar el resultado actualizado, incluso si el número de mandos no cambió.
- Evitar refrescos simultáneos que terminen aplicándose fuera de orden.
- Mostrar o registrar un error si la consulta falla, sin borrar los últimos datos válidos.

Si hay varias solicitudes de actualización simultáneas, usar un mecanismo de exclusión o cancelación para que una consulta antigua no sobrescriba el resultado de una más reciente.

## 7. Eventos y actualización de la interfaz

La interfaz debe reflejar los datos que publica el manager.

1. Confirmar que `ListChanged` se suscribe una sola vez.
2. Desuscribir los eventos al liberar el control o cerrar la ventana.
3. Actualizar los controles de WinForms desde el hilo de UI (`Invoke`/`BeginInvoke` o el mecanismo ya usado por el proyecto).
4. No actualizar únicamente cuando se agregue o quite un elemento: también actualizar al cambiar su estado.
5. Evitar que un control mantenga una copia independiente y obsoleta de la lista del manager.

No es necesario cambiar tarjetas, colores, iconos o distribución visual para resolver estos errores.

## 8. Identidad y deduplicación

No usar VID/PID o nombre como identificador único de una unidad física. Esos datos pueden compartirse entre varios mandos del mismo modelo.

Priorizar, según disponibilidad y estabilidad:

1. Identificador de instancia o identificador persistente de Windows.
2. Dirección Bluetooth, cuando esté disponible y sea fiable.
3. Identificadores específicos del provider.
4. VID/PID y nombre solo como datos descriptivos o para coincidencias no concluyentes.

Si no se puede distinguir con certeza dos dispositivos, no fusionarlos automáticamente. Evitar que un mando desconectado se convierta en el mismo objeto conectado que otro mando idéntico.

## 9. Registro y diagnóstico

Añadir logs claros para cada ciclo de detección:

- Inicio y fin de actualización, indicando si es manual o automática.
- Cantidad de dispositivos enumerados por Windows y SDL2.
- Dispositivos emparejados, conectados y detectados por SDL2.
- Identidad utilizada para reconciliar cada dispositivo.
- Cambios de estado conectado/desconectado.
- Eventos de llegada y retirada recibidos por Windows.
- Excepciones y errores de provider.

No registrar datos personales innecesarios. Los logs deben ayudar a distinguir entre «Windows devolvió cero dispositivos» y «Windows no pudo enumerar».

---

## 10. Pruebas de aceptación obligatorias

Probar en una máquina Windows real con uno o más mandos Bluetooth y, de ser posible, un mando USB.

| Prueba | Acción | Resultado esperado |
|---|---|---|
| A | Emparejar un mando y dejarlo desconectado | Aparece como emparejado/disponible si la opción de mostrar dispositivos disponibles está activada |
| B | Conectar el mando | El estado cambia a conectado sin reiniciar |
| C | Desconectar el mando desde Windows o apagándolo | El estado cambia a desconectado automáticamente |
| D | Desconectar y pulsar Actualizar | No permanece falsamente conectado |
| E | Conectar y desconectar varias veces | La lista y los estados se mantienen correctos en cada ciclo |
| F | Mantener el mando emparejado pero desconectado y actualizar | Sigue apareciendo como disponible; no se marca conectado |
| G | Simular o provocar un fallo de enumeración | Se conserva la última instantánea válida y se registra el error |
| H | Tener dos mandos idénticos | No se fusionan solo por nombre o VID/PID |
| I | Conectar/desconectar un mando USB | La lista se actualiza sin reiniciar |
| J | Cerrar y abrir la aplicación | No se duplican eventos ni suscripciones |

La corrección se considera terminada solo cuando las pruebas A–F pasan de forma repetida sin reiniciar la aplicación.

---

## 11. Orden recomendado de implementación

1. Revisar y corregir la enumeración de Windows Bluetooth.
2. Corregir la reconciliación y la representación de estados en `GamepadManager`.
3. Corregir `RefreshAsync` para producir y publicar instantáneas completas.
4. Verificar el monitor de cambios HID/Windows.
5. Conectar correctamente el botón manual de actualización.
6. Revisar el enlace de eventos y el refresco de WinForms.
7. Añadir logs y ejecutar las pruebas de aceptación.
8. Solo después, considerar mejoras visuales menores para diferenciar «emparejado», «conectado» y «desconectado».

## 12. Restricciones de la corrección

- Mantener el diseño actual de la interfaz.
- No eliminar mandos guardados solo porque estén desconectados.
- No usar el nombre del dispositivo como identidad única.
- No convertir errores de enumeración en resultados vacíos exitosos.
- No marcar un dispositivo como conectado por conservar un objeto o estado antiguo.
- No introducir cambios visuales grandes como parte de esta reparación.

### Resultado esperado

La lista debe mostrar de forma coherente los mandos emparejados/disponibles y los conectados en ese momento. Al conectar, desconectar o pulsar **Actualizar**, el estado debe cambiar sin necesidad de reiniciar el programa.
