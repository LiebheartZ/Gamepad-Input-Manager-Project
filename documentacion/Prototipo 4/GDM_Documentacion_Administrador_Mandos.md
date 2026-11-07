# Especificación técnica para implementar la pestaña «Mandos» de GDM
**Documento de instrucciones para una IA de programación (Devin, GitHub Copilot u otra)**  
**Objetivo:** implementar o reconstruir la pestaña «Mandos» de forma fiable, modular y sin introducir SQLite ni dependencias innecesarias.

---

## 1. Instrucción principal para la IA

Trabaja sobre el proyecto GDM existente. Antes de modificar código:

1. Inspecciona toda la solución y determina el framework de interfaz (WPF, WinForms u otro), la versión de .NET, la versión y forma de integración de SDL, las bibliotecas ya instaladas y la arquitectura existente.
2. Identifica la ventana/vista de la pestaña «Mandos», sus controles, servicios de detección y modelos actuales.
3. Reutiliza lo que esté correcto y compatible. No reemplaces tecnologías ni actualices .NET sin una razón técnica imprescindible.
4. No inventes clases, nombres de controles, métodos, paquetes ni APIs que no existan en el proyecto. Adapta esta especificación a su estructura real.
5. Implementa por etapas, compila y corrige errores después de cada etapa. No dejes pseudocódigo, métodos vacíos ni botones sin funcionalidad.
6. No cambies otras pestañas ni funciones ajenas a «Mandos», salvo que sea imprescindible para integrar correctamente los servicios compartidos. En ese caso, limita y documenta los cambios.

El resultado debe integrarse con el proyecto actual y mantener su estilo visual. No rediseñes toda la aplicación sin necesidad.

## 2. Objetivo funcional de la pestaña

La pestaña «Mandos» debe permitir:

- Mostrar los mandos que Windows tiene emparejados o conoce, cuando sea posible obtener esa información.
- Mostrar los mandos que GDM tiene guardados.
- Detectar y mostrar los mandos conectados en ese momento.
- Permitir buscar dispositivos Bluetooth cercanos e iniciar el flujo de conexión/emparejamiento que Windows permita.
- Mostrar el estado real del mando y actualizarlo cuando se conecte o desconecte.
- Guardar un mando voluntariamente, junto con su identidad, perfil, remapeo y demás ajustes.
- Reconocer un mando guardado al reconectarlo y cargar su configuración.
- Conservar el mismo perfil si el mismo mando se utiliza por USB o Bluetooth, siempre que la identidad se pueda verificar con suficiente confianza.

La detección de un dispositivo **no debe guardarlo automáticamente**. El usuario decide qué mandos se conservan en GDM.

## 3. Regla fundamental: mando temporal frente a mando guardado

Hay dos conceptos distintos:

### 3.1 Mando temporal (no guardado)

- Aparece mientras GDM lo detecta conectado o disponible en la vista correspondiente.
- Puede mostrar información y permitir las funciones que ya admita el programa.
- No se crea un archivo persistente ni un registro de mando guardado por el simple hecho de detectarlo.
- Cuando se desconecta, desaparece de la lista de mandos conectados.
- No se conserva su perfil o remapeo como configuración propia de ese dispositivo.

### 3.2 Mando guardado

- El usuario pulsa «Guardar».
- GDM crea su registro en el catálogo JSON y un archivo individual para ese mando.
- El archivo individual contiene su identidad, perfil, remapeo y ajustes.
- Cuando se desconecta, permanece en la lista con estado «Desconectado».
- Al reconectarse, GDM intenta identificarlo y cargar su archivo.
- Desconectarlo nunca debe borrar sus datos.
- Solo se elimina de los guardados mediante una acción explícita, por ejemplo «Eliminar de guardados», con confirmación.

### 3.3 Presentación sin duplicados

Si un mando guardado está conectado, debe aparecer una sola vez, no como una fila de «guardado» y otra de «conectado». La tarjeta del mando guardado debe reflejar el estado actual «Conectado». Si está desconectado, debe aparecer como «Desconectado».

Un mando temporal conectado aparece en la lista mientras esté detectado. Al desconectarse, desaparece.

## 4. Persistencia: solo archivos JSON/INI, sin SQLite

El usuario desea reducir al mínimo las instalaciones y dependencias. Por lo tanto:

- **No usar SQLite.**
- No instalar un servidor, motor de base de datos ni paquete NuGet solo para guardar los datos de los mandos.
- Preferir JSON con las bibliotecas incluidas en .NET, especialmente `System.Text.Json`, si la versión del proyecto lo permite.
- Si la versión de .NET no dispone de una API adecuada, elegir una alternativa compatible ya disponible en el proyecto. No actualizar .NET automáticamente.
- INI puede utilizarse para configuraciones sencillas, pero JSON es preferible para los perfiles y remapeos anidados.

La solución será una **base de datos lógica basada en archivos JSON**: un catálogo central para localizar los mandos guardados y un archivo independiente por cada mando. No se debe presentar esto como una base de datos SQLite.

### 4.1 Estructura de carpetas recomendada

Adapta la ruta a las convenciones del proyecto. Una organización posible:

```text
GDM/
├── GDM.exe
├── Config/
│   └── GDM_Settings.json
├── Data/
│   ├── Controllers/
│   │   ├── controllers-index.json
│   │   ├── controller_<id-1>.json
│   │   └── controller_<id-2>.json
│   └── Backups/
└── Logs/
```

- `GDM_Settings.json`: ajustes generales del programa, no los perfiles individuales de los mandos.
- `controllers-index.json`: catálogo de los mandos que el usuario guardó. Contiene IDs internos, nombres informativos, referencias a los archivos y datos mínimos para encontrarlos.
- `controller_<id>.json`: archivo único de cada mando guardado. Contiene toda la configuración individual, perfil, remapeo e información de identidad.
- `Backups/`: copias de seguridad opcionales antes de sobrescribir configuraciones importantes.
- `Logs/`: registros técnicos; nunca guardar secretos ni volcar direcciones MAC completas innecesariamente.

No utilizar el nombre visible del mando como nombre del archivo, porque varios mandos pueden llamarse igual y el nombre puede cambiar. Usar un ID interno estable y seguro para el nombre del archivo.

### 4.2 Qué se almacena en cada lugar

**Archivo general `GDM_Settings.json`:**
- Preferencias globales de interfaz.
- Opciones generales de detección y actualización.
- Preferencias de inicio y comportamiento de GDM.
- Ajustes globales que no pertenecen a un mando específico.

**Catálogo `controllers-index.json`:**
- ID interno de GDM para cada mando guardado.
- Nombre visible y fabricante/modelo si se conocen.
- Ruta relativa del archivo individual.
- Identificadores conocidos para ayudar a reconocerlo.
- Fecha de creación y última modificación del registro.
- Versión del formato del catálogo.

No duplicar aquí toda la configuración del mando. El catálogo sirve para localizar y relacionar archivos.

**Archivo individual del mando:**
- Identidad y evidencias de identificación.
- Nombre, fabricante, modelo y tipo, cuando se conozcan.
- MAC Bluetooth, si se obtiene de una fuente fiable.
- Identificadores de Windows y hardware disponibles.
- GUID de SDL como dato complementario, no como identidad física única.
- Perfil seleccionado y/o perfil propio.
- Remapeo de botones.
- Asignación de ejes.
- Zonas muertas, sensibilidad y ajustes de sticks/triggers.
- Vibración y otras opciones soportadas.
- Versión del esquema y fechas de modificación.

### 4.3 Ejemplo ilustrativo de archivo individual

El siguiente JSON es una orientación. Los nombres y campos finales deben adaptarse a las clases reales del proyecto y a las funciones que GDM soporte. No guardar valores ficticios como si hubieran sido detectados.

```json
{
  "schemaVersion": 1,
  "controllerId": "id-interno-estable",
  "displayName": "Nombre del mando",
  "identity": {
    "manufacturer": null,
    "model": null,
    "bluetoothMac": null,
    "windowsDeviceIds": [],
    "hardwareIds": [],
    "containerIds": [],
    "sdlGuid": null,
    "knownTransports": []
  },
  "profile": {
    "profileId": null,
    "profileName": null
  },
  "mapping": {
    "buttons": {},
    "axes": {},
    "deadZones": {},
    "sensitivity": {}
  },
  "settings": {
    "vibrationEnabled": true,
    "customOptions": {}
  },
  "createdAtUtc": null,
  "updatedAtUtc": null
}
```

`null` y las listas vacías significan que el dato no está disponible. No sustituir un dato desconocido por una MAC inventada, una dirección por defecto o un porcentaje de batería falso.

### 4.4 Operaciones seguras con archivos

Implementar servicios responsables de leer y escribir JSON. Deben:

- Validar JSON antes de cargarlo.
- Comprobar `schemaVersion` y manejar versiones incompatibles de forma controlada.
- Escribir primero a un archivo temporal y después reemplazar el archivo anterior de forma segura.
- Evitar dejar el catálogo apuntando a un archivo que no se haya creado correctamente.
- Manejar archivos ausentes, JSON corrupto, permisos insuficientes y disco lleno sin cerrar inesperadamente GDM.
- Mantener una copia de respaldo antes de cambios importantes, si es viable.
- No borrar automáticamente archivos individuales por una simple desconexión.
- Si el catálogo está corrupto, ofrecer recuperación mediante exploración de archivos individuales, sin sobrescribir datos válidos a ciegas.
- Usar rutas relativas dentro de `Data/` siempre que sea posible y validar que las rutas no permitan salir de la carpeta de datos.

Al guardar un mando por primera vez, crear el archivo individual y actualizar el catálogo de manera consistente. Si una de las operaciones falla, informar del error y evitar dejar un registro incompleto.

## 5. Identidad del mando: reconocerlo por USB y Bluetooth

Esta es una de las partes más importantes y delicadas.

El perfil pertenece al **mando físico guardado**, no a la conexión USB o Bluetooth ni a una instancia temporal de SDL.

Ejemplo esperado:
1. El usuario guarda un mando y le asigna el perfil «Shooter».
2. Lo conecta por Bluetooth: GDM lo reconoce y carga ese perfil.
3. Lo desconecta y conecta por USB: GDM intenta reconocer el mismo mando y vuelve a cargar «Shooter».
4. Vuelve a Bluetooth: sigue usando el mismo registro y perfil.
5. No se debe crear un segundo mando guardado solo porque cambió el transporte.

### 5.1 Identificadores y sus limitaciones

Utilizar varias evidencias, según estén disponibles:

- Dirección Bluetooth MAC, solo si Windows o una API fiable la proporciona.
- Identificadores de dispositivo de Windows.
- Hardware IDs, Container ID u otros identificadores de Windows que sean pertinentes.
- Información de fabricante/modelo.
- Datos de SDL como GUID, nombre e instancia.

Limitaciones obligatorias:
- El GUID de SDL normalmente identifica una familia/modelo, no una unidad física concreta.
- El Instance ID de SDL identifica una instancia activa y puede cambiar al reconectar.
- El nombre del mando no es único.
- `DeviceInformation.Id` no debe asumirse automáticamente como MAC.
- Algunas direcciones Bluetooth, especialmente direcciones privadas/aleatorias de ciertos dispositivos BLE, pueden cambiar o no ser accesibles.
- No asumir que la MAC Bluetooth y el identificador USB son iguales.
- No fusionar dos mandos solo porque tengan el mismo nombre, GUID o modelo.

### 5.2 Asociación USB/Bluetooth

Implementar una estrategia de identidad con niveles de confianza:

- **Coincidencia fuerte:** identificador estable y verificado que permita vincular la conexión actual con un mando guardado.
- **Coincidencia probable:** varias evidencias compatibles, pero no concluyentes. No cargar automáticamente una configuración que pueda pertenecer a otro mando; solicitar confirmación o dejar el dispositivo sin asociar.
- **Sin coincidencia:** tratar como mando temporal/no guardado.

Diseñar la lógica para que puedan añadirse nuevos métodos de identificación sin rehacer la interfaz. Guardar el origen de cada identificador (Windows, Bluetooth, HID, SDL, etc.) y no confundir datos temporales con datos persistentes.

Si no se puede demostrar que USB y Bluetooth corresponden al mismo mando físico, explicar esta limitación con claridad. No prometer identificación universal cuando el hardware o Windows no expongan información suficiente.

## 6. Detección y fuentes de información

No confundir estas categorías:

1. **Conectados:** mandos que están activos y disponibles para entrada ahora.
2. **Emparejados/conocidos por Windows:** dispositivos que Windows registra como emparejados o conocidos, aunque no estén conectados.
3. **Guardados en GDM:** mandos para los que el usuario creó un registro persistente.
4. **Bluetooth cercanos:** dispositivos encontrados durante un escaneo Bluetooth activo.

Un mismo dispositivo puede pertenecer a varias categorías.

### 6.1 SDL

- Revisar la versión de SDL que el proyecto ya utiliza y las APIs disponibles en esa versión.
- Utilizar SDL como fuente principal para enumerar mandos de juego conectados y obtener entrada de botones/ejes, si esa es la arquitectura existente.
- No mezclar APIs de SDL2 con SDL3.
- No asumir que SDL enumera todos los dispositivos emparejados por Windows o que realiza por sí solo el escaneo/emparejamiento Bluetooth.
- Mantener separados los identificadores de instancia de SDL y la identidad persistente del dispositivo.

### 6.2 Windows

Usar las APIs de Windows adecuadas para información que SDL no proporciona, como dispositivos emparejados/conocidos o descubrimiento Bluetooth, si el proyecto y su modelo de aplicación lo permiten.

- Comprobar compatibilidad con el framework y versión de Windows objetivo.
- No agregar paquetes o dependencias externas sin necesidad.
- El escaneo Bluetooth debe tener estados claros: iniciando, buscando, detenido, error y permisos requeridos.
- Respetar el flujo de emparejamiento de Windows cuando el sistema operativo requiera intervención del usuario.
- No afirmar que un dispositivo está conectado solo porque aparezca en la lista de emparejados.
- No afirmar que un dispositivo está emparejado solo porque aparezca en un escaneo cercano.

### 6.3 Actualización de conexión/desconexión

Resolver expresamente los fallos conocidos del proyecto:
- Mandos ya emparejados en Windows que dejaron de aparecer.
- Mandos desconectados que siguen figurando como conectados.
- El botón de actualizar que mantiene un estado antiguo hasta reiniciar el programa.

La solución debe:
- Suscribirse a eventos de conexión/desconexión cuando estén disponibles.
- Tener un mecanismo de reconciliación/actualización periódica como respaldo.
- Al pulsar «Actualizar», volver a consultar las fuentes reales y reconstruir el estado actual, no limitarse a refrescar la interfaz con datos antiguos.
- Eliminar instancias desconectadas de la colección de dispositivos activos.
- Conservar en la lista solo los registros persistentes de mandos guardados.
- Evitar eventos duplicados, carreras entre tareas y actualizaciones simultáneas de la interfaz.
- Cancelar correctamente tareas de escaneo al cerrar la vista o la aplicación.

## 7. Batería y dirección MAC

### 7.1 MAC

- Mostrar y guardar la MAC únicamente cuando se haya obtenido de una fuente fiable.
- Normalizar el formato para comparar y mostrar.
- Guardar el origen y fecha de obtención cuando sea útil.
- Si no está disponible, mostrar «No disponible».
- No usar GUID SDL, nombre o Device ID como si fueran una MAC.
- Evitar imprimir la MAC completa en logs.

### 7.2 Batería

- Mostrar porcentaje solo cuando la API o el protocolo realmente proporcione un porcentaje fiable.
- Si SDL3 está disponible en una versión compatible, comprobar las capacidades de la API de batería correspondiente. Si el dato es desconocido, conservarlo como desconocido.
- En SDL2, el nivel puede ser cualitativo y no necesariamente un porcentaje exacto.
- Se puede evaluar una fuente de Windows/HID como alternativa solo si se valida para el modelo y transporte concretos.
- No abrir en exclusiva un dispositivo HID si eso puede impedir que SDL u otra aplicación lo utilice.
- No asumir posiciones de bytes ni protocolos de batería sin documentación/verificación.
- Si no hay lectura fiable, mostrar «No disponible», nunca `0 %` como sustituto.
- Actualizar con una frecuencia moderada o mediante eventos, sin consultar continuamente de forma innecesaria.

## 8. Diseño y comportamiento visual

Mantener el lenguaje visual de GDM y adaptar la distribución existente. La vista debe permitir distinguir rápidamente estado, identidad y acciones.

Elementos previstos:
- Área superior con controles generales de la pestaña.
- Acción «Actualizar».
- Acción para iniciar/detener búsqueda Bluetooth.
- Filtros o secciones para conectados, guardados y dispositivos encontrados/emparejados, según el diseño actual.
- Lista de tarjetas de mandos debajo.
- Cada tarjeta debe mostrar, cuando se conozcan: nombre, fabricante/modelo, tipo de conexión (USB/Bluetooth), estado, batería y acciones disponibles.

### 8.1 Estados visuales

Usar estados inequívocos:
- Conectado.
- Desconectado (solo para mandos guardados).
- Buscando.
- Disponible/encontrado (Bluetooth, sin afirmar que está conectado).
- Emparejado por Windows (si la fuente lo confirma).
- Error o información no disponible.

No representar «desconocido» como «desconectado» si no se ha podido consultar la fuente.

### 8.2 Acciones por tarjeta

- **Guardar:** solo para mandos todavía no guardados.
- **Guardado:** indicador cuando ya existe un registro persistente.
- **Abrir configuración/perfil:** para editar el mando, según las funciones existentes.
- **Eliminar de guardados:** acción separada, con confirmación. No equivale a desconectar.
- Acciones de conexión/emparejamiento solo cuando Windows y el tipo de dispositivo las permitan.

Los botones deben realizar acciones reales y mostrar errores comprensibles. No dejar controles decorativos o sin implementar.

## 9. Arquitectura por capas recomendada

Adaptar nombres al proyecto, pero mantener responsabilidades separadas.

### 9.1 Presentación (UI)
- Vista/control de la pestaña «Mandos».
- Tarjetas o elementos de lista.
- Estados visuales y comandos de usuario.
- No debe contener llamadas directas a APIs de Windows, SDL ni lectura/escritura JSON.

### 9.2 Modelos
Definir modelos claros, por ejemplo:
- `ControllerDevice`: representación de un dispositivo detectado en el momento.
- `SavedController`: registro persistente de GDM.
- `ControllerIdentity`: identificadores y evidencias de identidad.
- `ControllerProfile`: perfil asociado.
- `ControllerMapping`: remapeo de botones y ejes.
- `ControllerSettings`: opciones individuales.
- `ControllerConnectionState`: estado actual.
- `ControllerTransport`: USB, Bluetooth, otro o desconocido.

No es obligatorio usar estos nombres si el proyecto tiene convenciones distintas.

### 9.3 Servicios de detección
- Servicio SDL para mandos conectados.
- Servicio Windows para dispositivos emparejados/conocidos.
- Servicio Bluetooth para escaneo, cuando corresponda.
- Servicio de batería/MAC u otras capacidades, solo con fuentes verificadas.

### 9.4 Identidad y asociación
Servicio independiente que:
- Recibe la información detectada.
- Compara evidencias con los mandos guardados.
- Devuelve una coincidencia y un nivel de confianza.
- Nunca decide basándose exclusivamente en nombre o GUID de modelo.
- Permite que la asociación USB/Bluetooth se resuelva de forma segura.

### 9.5 Persistencia JSON
Responsable de:
- Leer/escribir configuración general.
- Leer/escribir catálogo de mandos guardados.
- Leer/escribir el archivo individual de cada mando.
- Validar y versionar esquemas.
- Realizar escrituras seguras y recuperación de errores.

### 9.6 Coordinación/orquestación
Un servicio o ViewModel coordina:
- Detección.
- Asociación de identidad.
- Carga del archivo del mando.
- Actualización de la lista.
- Guardado, edición y eliminación.
- Cambios de estado en la UI.

La UI no debe implementar estas reglas por su cuenta.

## 10. Flujo detallado de las operaciones

### 10.1 Inicio de GDM / carga de la pestaña
1. Cargar `GDM_Settings.json`.
2. Cargar y validar `controllers-index.json`.
3. Leer los registros de mandos guardados.
4. Mostrar esos mandos inicialmente como desconectados, hasta comprobar su estado real.
5. Consultar dispositivos conectados y dispositivos conocidos por Windows mediante sus servicios.
6. Asociar dispositivos detectados con registros guardados usando el servicio de identidad.
7. Actualizar tarjetas sin duplicar mandos.

No marcar como conectado un mando solo porque exista en el catálogo o figure emparejado.

### 10.2 Actualizar
1. Cancelar o coordinar una actualización anterior para evitar solapamientos.
2. Consultar de nuevo las fuentes de dispositivos.
3. Reconciliar el resultado con la colección activa.
4. Actualizar estados y datos disponibles.
5. Quitar dispositivos temporales que ya no estén conectados.
6. Mantener los guardados desconectados.
7. Informar si alguna fuente no pudo consultarse; no borrar registros persistentes por un error temporal.

### 10.3 Guardar un mando
1. Verificar que el mando detectado todavía existe y no está ya guardado.
2. Recopilar identificadores disponibles y su fuente.
3. Crear un ID interno estable de GDM.
4. Preparar el archivo individual JSON con perfil, remapeo y ajustes actuales.
5. Escribir y validar el archivo individual.
6. Actualizar el catálogo JSON de forma segura.
7. Cambiar el estado visual a «Guardado».
8. Si falla el proceso, informar y no dejar un registro parcial.

### 10.4 Reconectar un mando guardado
1. Detectar el dispositivo.
2. Obtener identificadores disponibles.
3. Buscar coincidencia en el catálogo de guardados.
4. Evaluar el nivel de confianza.
5. Si la coincidencia es fuerte, leer y validar el archivo individual.
6. Cargar el perfil y remapeo asociados.
7. Aplicarlos mediante los mecanismos existentes del programa.
8. Mostrar el mando como conectado.
9. Si la coincidencia no es segura, no aplicar un perfil ajeno; dejarlo sin asociar y permitir resolverlo.

### 10.5 Desconectar
1. Detectar la desconexión mediante evento o reconciliación.
2. Marcar como desconectada la instancia activa.
3. Si corresponde a un mando guardado, mantener su tarjeta y archivo.
4. Si es temporal/no guardado, quitarlo de la lista de conectados.
5. No borrar configuración general, catálogo ni archivos de otros mandos.

### 10.6 Eliminar de guardados
1. Pedir confirmación.
2. Explicar que se eliminará la configuración guardada de GDM, no el emparejamiento de Windows.
3. Eliminar el registro del catálogo y el archivo individual de forma segura.
4. Si el dispositivo sigue conectado, puede continuar apareciendo como temporal/no guardado.
5. No intentar desemparejarlo de Windows salvo que exista una acción separada y explícita para ello.

## 11. Requisitos de calidad y manejo de errores

- La aplicación no debe cerrarse por un fallo de detección, un dispositivo que desaparece durante una consulta o un JSON inválido.
- No bloquear el hilo de interfaz durante escaneos o lecturas lentas.
- Evitar tareas de detección duplicadas y fugas de eventos.
- Liberar correctamente los recursos de SDL y Windows.
- Evitar guardar la misma unidad física dos veces cuando haya evidencia suficiente de que es el mismo mando.
- No fusionar dispositivos ambiguos.
- No crear archivos persistentes para mandos temporales.
- No borrar mandos guardados cuando una fuente de detección falla.
- Registrar errores técnicos con información útil, sin exponer datos sensibles.
- Mantener compatibilidad con mandos Xbox, PlayStation y otros dispositivos compatibles con las fuentes utilizadas, sin prometer capacidades que el hardware no exponga.

## 12. Pruebas de aceptación obligatorias

La implementación no se considera terminada hasta probar, como mínimo:

1. Un mando USB no guardado aparece al conectarlo y desaparece al desconectarlo.
2. Un mando Bluetooth no guardado aparece al conectarlo y desaparece al desconectarlo.
3. Un mando guardado permanece en la lista después de desconectarlo.
4. Un mando guardado cambia correctamente entre estados conectado/desconectado.
5. Cerrar y abrir GDM conserva los mandos guardados.
6. La configuración general sobrevive al reinicio.
7. Cada mando guardado tiene su propio archivo JSON.
8. Editar el remapeo de un mando no modifica el archivo de otro.
9. Al reconectar un mando guardado con identidad verificada, carga su perfil.
10. Si el mismo mando puede identificarse de forma verificada por USB y Bluetooth, conserva el mismo registro y perfil.
11. Cambiar de conexión no crea duplicados cuando la identidad se puede verificar.
12. Si la identidad es ambigua, GDM no carga automáticamente un perfil potencialmente incorrecto.
13. Pulsar «Actualizar» refleja una desconexión real sin reiniciar GDM.
14. Los dispositivos emparejados por Windows no se confunden con dispositivos conectados.
15. Un dispositivo Bluetooth cercano no se presenta como conectado ni emparejado sin confirmación.
16. Un JSON corrupto produce un error controlado y no destruye los demás archivos.
17. Si no se conoce la MAC o batería, se muestra «No disponible» y no un valor inventado.
18. Eliminar de guardados no desempareja el dispositivo de Windows.
19. Un error temporal de Windows/SDL no borra registros guardados.
20. La pestaña sigue respondiendo mientras se actualizan los dispositivos.

Documentar qué pruebas se ejecutaron, en qué entorno y cuáles no pudieron verificarse por falta de hardware.

## 13. Orden de implementación recomendado

Implementar y validar en este orden:

**Fase 1 — Inspección**
- Documentar framework, versión .NET, SDL, estructura de carpetas y servicios existentes.
- Identificar el origen de los fallos actuales de conexión/desconexión y actualización.

**Fase 2 — Modelos y persistencia**
- Crear modelos.
- Implementar configuración general JSON.
- Implementar catálogo JSON y archivos individuales.
- Probar lectura, escritura, validación, respaldo y recuperación.

**Fase 3 — Detección**
- Corregir enumeración de mandos conectados con SDL.
- Integrar la consulta de dispositivos conocidos/emparejados de Windows.
- Implementar actualización y eventos de conexión/desconexión.

**Fase 4 — Identidad**
- Implementar recopilación de identificadores.
- Implementar comparación y niveles de confianza.
- Probar el reconocimiento USB/Bluetooth sin asumir que todos los modelos lo permiten.

**Fase 5 — Interfaz**
- Implementar lista unificada y tarjetas sin duplicados.
- Implementar estados y acciones reales.
- Mantener el diseño general existente.

**Fase 6 — Perfiles y remapeo**
- Conectar la carga/guardado del perfil y mapeo con el sistema de entrada ya existente.
- Verificar que los cambios de un mando no afecten a otros.

**Fase 7 — Pruebas**
- Ejecutar todas las pruebas de aceptación.
- Corregir errores.
- Entregar un resumen de archivos modificados, decisiones técnicas, dependencias añadidas (idealmente ninguna nueva para JSON) y pruebas realizadas.

## 14. Entrega que debe producir la IA de programación

Al terminar, la IA debe proporcionar:

1. Resumen de cómo funciona la pestaña.
2. Lista de archivos creados y modificados.
3. Estructura final de carpetas y explicación de cada JSON.
4. Explicación de cómo identifica los mandos y qué limitaciones tiene USB/Bluetooth.
5. Dependencias añadidas o confirmación de que no se añadió ninguna para persistencia JSON.
6. Resultado de compilación.
7. Pruebas realizadas y sus resultados.
8. Problemas pendientes que requieran hardware real o APIs no disponibles.

**Criterio final:** priorizar fiabilidad, separación por capas y ausencia de dependencias innecesarias. La detección temporal no guarda datos; solo la acción explícita «Guardar» crea la configuración persistente del mando.
