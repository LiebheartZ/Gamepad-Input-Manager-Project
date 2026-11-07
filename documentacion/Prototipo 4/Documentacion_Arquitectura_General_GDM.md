# Documentación de arquitectura general de GDM
## Sistema principal, interfaz, módulos y almacenamiento de datos

**Documento de especificación para reconstruir el proyecto desde cero**  
**Destinatario:** IA de programación (Devin, GitHub Copilot u otra)  
**Producto:** GDM — Gamepad Device Manager  
**Alcance:** arquitectura general de la aplicación, sistema principal, interfaz, módulos, comunicación entre componentes y persistencia local.

---

# 1. Instrucción principal para la IA de programación

Debes construir una nueva versión de GDM desde cero o reorganizar el proyecto existente siguiendo esta especificación. El objetivo es crear una aplicación de escritorio para Windows, modular, mantenible y ampliable, centrada en administrar mandos de videojuegos.

Antes de escribir código:

1. Inspecciona los archivos del proyecto y determina si se va a crear una solución limpia o reutilizar componentes.
2. Confirma la versión de Windows y el framework de escritorio que mejor encaje con las tecnologías y requisitos reales del proyecto.
3. Compara WPF, WinUI 3 y Windows Forms solo si la tecnología aún no está decidida. Recomienda una opción y explica brevemente sus ventajas y limitaciones antes de fijarla.
4. Determina la versión de .NET y comprueba compatibilidad con SDL y las APIs de Windows que se necesiten.
5. Identifica las dependencias imprescindibles. Prioriza las bibliotecas incluidas en .NET y evita dependencias externas si no aportan una ventaja clara.
6. Presenta primero una propuesta de estructura de solución y módulos. Después implementa por fases.
7. No inventes APIs, paquetes, clases o capacidades de hardware. Verifica que cada API corresponde a la versión seleccionada.
8. No mezcles responsabilidades de interfaz, lógica, detección de hardware y almacenamiento.
9. Compila y prueba cada fase antes de continuar.
10. No declares una función terminada si solo existe su interfaz visual o un método vacío.

La aplicación debe poder crecer sin tener que reescribir el sistema central cada vez que se agregue una función.

---

# 2. Visión general de GDM

GDM será una aplicación de escritorio para Windows destinada a administrar y mejorar la experiencia de uso de mandos de videojuegos.

## 2.1 Funciones principales previstas

La arquitectura debe permitir incorporar, de forma progresiva:

- Detección y administración de mandos.
- Listado de mandos conectados, conocidos por Windows y guardados por el usuario.
- Información del dispositivo, conexión, batería e identificadores cuando estén disponibles.
- Prueba de botones, ejes, sticks, gatillos y vibración, según las capacidades del mando.
- Creación, edición, selección y administración de perfiles.
- Remapeo de botones y ejes.
- Ajustes de sensibilidad y zonas muertas.
- Configuraciones individuales por mando.
- Compatibilidad con mandos Xbox, PlayStation y otros dispositivos compatibles.
- Funciones relacionadas con mandos virtuales, como módulo futuro.
- Integración con HidHide, como módulo futuro.
- Registro de errores y diagnóstico.
- Configuración general del programa.

No es obligatorio implementar todas las funciones en la primera versión. Sí es obligatorio diseñar la arquitectura para que puedan añadirse como módulos sin alterar todo el sistema.

## 2.2 Principios de diseño

1. **Modularidad:** cada función importante debe tener una responsabilidad definida.
2. **Separación por capas:** la interfaz no debe acceder directamente a SDL, Windows, archivos o controladores.
3. **Persistencia sencilla:** usar archivos JSON para los datos de GDM; no utilizar SQLite.
4. **Dependencias mínimas:** no instalar paquetes si .NET o las bibliotecas ya existentes resuelven la necesidad.
5. **Estado coherente:** la interfaz debe reflejar el estado real de los servicios y dispositivos.
6. **Seguridad:** no aplicar configuraciones a un mando cuya identidad no se haya reconocido con suficiente confianza.
7. **Tolerancia a errores:** un módulo que falle no debe cerrar toda la aplicación.
8. **Ampliabilidad:** las funciones futuras deben poder integrarse sin acoplarse a la interfaz principal.
9. **Experiencia consistente:** navegación, tarjetas, mensajes, botones y ajustes deben seguir un mismo lenguaje visual.
10. **Transparencia:** indicar al usuario cuando una función no está disponible por limitaciones de Windows, SDL o del hardware.

---

# 3. Arquitectura general del sistema

Organizar GDM en capas. Los nombres de proyectos y carpetas pueden adaptarse, pero las responsabilidades deben mantenerse.

## 3.1 Capas

### A. Presentación (UI)

Responsable de mostrar la aplicación y recibir las acciones del usuario.

Incluye:
- Ventana principal.
- Barra lateral o sistema de navegación.
- Encabezado y área de contenido.
- Vistas de cada módulo.
- Controles reutilizables.
- Diálogos y notificaciones.
- Estados de carga, error y ausencia de datos.

La UI no debe:
- Consultar SDL directamente.
- Enumerar dispositivos Windows directamente.
- Leer y escribir JSON por su cuenta.
- Implementar la lógica de asociación de dispositivos.
- Ejecutar operaciones de sistema de bajo nivel dentro de los eventos visuales.

La UI solicita acciones a ViewModels o servicios de aplicación y presenta sus resultados.

### B. Aplicación (Application)

Coordina los casos de uso de GDM.

Ejemplos:
- Iniciar o cerrar un módulo.
- Actualizar la lista de mandos.
- Guardar un mando.
- Cargar un perfil.
- Aplicar un remapeo.
- Iniciar una prueba de botones.
- Consultar el estado de HidHide.
- Crear o detener un mando virtual.

Esta capa coordina servicios, valida operaciones y devuelve resultados claros a la UI. No debe contener detalles de controles visuales ni APIs de hardware.

### C. Dominio (Domain)

Contiene las reglas y modelos centrales del programa.

Ejemplos:
- Mando.
- Identidad de dispositivo.
- Perfil.
- Mapeo de botones y ejes.
- Estado de conexión.
- Tipo de transporte.
- Configuración general.
- Resultado de una operación.

Las reglas del dominio no deben depender de WPF, WinUI, Windows Forms, SDL ni del sistema de archivos.

### D. Infraestructura (Infrastructure)

Contiene las implementaciones técnicas concretas:
- Lectura y escritura JSON.
- Detección mediante SDL.
- Consulta de dispositivos Windows.
- Descubrimiento Bluetooth.
- Lectura de batería cuando sea compatible.
- Integración con APIs de HidHide.
- Integración con tecnología de mandos virtuales.
- Acceso a archivos, procesos y servicios del sistema.
- Registro de logs.

La infraestructura implementa interfaces definidas por las capas superiores cuando corresponda.

### E. Servicios compartidos (Shared/Core)

Contiene utilidades realmente compartidas:
- Gestión de eventos internos.
- Resultado y errores comunes.
- Reloj/fechas.
- Validación.
- Registro de logs.
- Utilidades de rutas.
- Gestión de cancelación de tareas.
- Versionado de archivos.

No convertir esta capa en un depósito de código sin organización. Cada utilidad debe tener una responsabilidad concreta.

---

# 4. Estructura recomendada de la solución

La IA debe adaptar los nombres al framework seleccionado. Como orientación, una solución por proyectos podría ser:

```text
GDM/
├── GDM.sln
├── src/
│   ├── GDM.App/
│   │   ├── App/
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Controls/
│   │   ├── Resources/
│   │   ├── Themes/
│   │   └── Navigation/
│   │
│   ├── GDM.Application/
│   │   ├── Abstractions/
│   │   ├── Services/
│   │   ├── UseCases/
│   │   ├── DTOs/
│   │   └── Events/
│   │
│   ├── GDM.Domain/
│   │   ├── Models/
│   │   ├── Enums/
│   │   ├── Rules/
│   │   └── ValueObjects/
│   │
│   ├── GDM.Infrastructure/
│   │   ├── Persistence/
│   │   ├── Devices/
│   │   │   ├── SDL/
│   │   │   ├── Windows/
│   │   │   └── Bluetooth/
│   │   ├── HidHide/
│   │   ├── VirtualControllers/
│   │   └── Logging/
│   │
│   └── GDM.Shared/
│       ├── Results/
│       ├── Events/
│       ├── Diagnostics/
│       └── Utilities/
│
├── Data/
│   ├── Settings/
│   ├── Controllers/
│   ├── Profiles/
│   ├── Modules/
│   ├── Cache/
│   ├── Backups/
│   └── Logs/
│
└── Documentation/
```

Si una solución con varios proyectos introduce complejidad innecesaria para el tamaño inicial, se puede empezar con una sola solución/proyecto organizado por carpetas y límites claros. No obstante, las dependencias entre capas deben respetarse para que luego sea posible separar proyectos.

## 4.1 Dirección de dependencias

Regla principal:

- La UI puede depender de Application y de modelos/contratos públicos.
- Application puede depender de Domain y de abstracciones.
- Infrastructure implementa las abstracciones definidas por Application o Domain.
- Domain no depende de UI ni Infrastructure.
- Los módulos no deben acceder directamente a las clases internas de otros módulos.

Evitar referencias circulares.

---

# 5. Sistema principal y ciclo de vida

La ventana principal es el contenedor de la aplicación. No debe implementar las funciones de todos los módulos.

## 5.1 Responsabilidades del sistema principal

- Inicializar configuración y servicios.
- Construir el contenedor de dependencias o mecanismo equivalente.
- Cargar el tema visual y recursos globales.
- Preparar la navegación.
- Mostrar la vista inicial.
- Gestionar el ciclo de vida de la aplicación.
- Coordinar el inicio y cierre de módulos.
- Presentar notificaciones globales.
- Controlar el cierre ordenado y la liberación de recursos.

## 5.2 Inicio de GDM

Orden recomendado:

1. Crear las carpetas de datos necesarias.
2. Inicializar el sistema de logs.
3. Cargar y validar la configuración general.
4. Aplicar valores predeterminados si el archivo no existe.
5. Inicializar los servicios comunes.
6. Registrar los módulos disponibles.
7. Inicializar solo los servicios que deban estar activos al inicio.
8. Construir la ventana principal.
9. Cargar la navegación y la vista inicial.
10. Mostrar errores recuperables sin cerrar la aplicación.

No iniciar escaneos Bluetooth, abrir dispositivos o crear mandos virtuales sin que el usuario o una preferencia explícita lo solicite, salvo que exista una necesidad justificada.

## 5.3 Cierre de GDM

Al cerrar:

1. Evitar aceptar nuevas operaciones.
2. Cancelar escaneos y tareas de fondo.
3. Detener pruebas activas.
4. Detener o liberar mandos virtuales si corresponde y según la configuración.
5. Desuscribirse de eventos.
6. Liberar recursos SDL y Windows.
7. Guardar ajustes generales pendientes.
8. Completar escrituras pendientes.
9. Cerrar logs.
10. Finalizar la aplicación.

El cierre debe ser ordenado, evitando archivos incompletos y recursos bloqueados.

---

# 6. Interfaz principal (Shell)

La interfaz debe ser moderna, clara, consistente y adecuada para una aplicación de escritorio. Mantener una estética propia de GDM; no copiar interfaces de terceros.

## 6.1 Distribución general

Se recomienda una estructura con:

- **Barra lateral:** navegación entre módulos.
- **Encabezado superior:** nombre de la sección actual, acciones globales pertinentes y acceso a ajustes/notificaciones.
- **Área central:** contenido de la sección seleccionada.
- **Zona de estado opcional:** actividad de servicios, mensajes breves o estado general.

La barra lateral debe permitir reconocer qué sección está seleccionada. La navegación no debe reiniciar innecesariamente los servicios ni perder el estado de una vista.

## 6.2 Navegación inicial sugerida

La navegación debe reflejar las funciones reales disponibles. Propuesta:

1. **Inicio:** resumen de estado y accesos rápidos.
2. **Mandos:** detección, lista, guardado e información de mandos.
3. **Pruebas:** herramientas para comprobar botones, ejes, sticks, gatillos y vibración.
4. **Perfiles:** creación y administración de perfiles.
5. **Remapeo:** configuración de botones/ejes y asignación de perfiles a mandos.
6. **Mandos virtuales:** módulo futuro.
7. **HidHide:** módulo futuro.
8. **Ajustes:** configuración general de GDM.
9. **Acerca de / Diagnóstico:** versión, dependencias, logs y diagnóstico.

No mostrar secciones futuras como si estuvieran implementadas. Si se muestran desde el principio, deben identificarse claramente como «Próximamente» y no incluir controles que aparenten funcionar.

## 6.3 Reglas visuales

- Mantener tipografía, espaciado, iconografía, radios y colores consistentes.
- Utilizar controles reutilizables.
- Mostrar jerarquía clara entre título, descripción, contenido y acciones.
- Evitar llenar la pantalla de tarjetas o bordes innecesarios.
- Los estados importantes deben expresarse con texto e iconos, no solo con color.
- Los botones deben tener estados normal, hover, presionado y deshabilitado.
- Mostrar indicadores de carga cuando una operación tarde.
- Los errores deben explicar qué ocurrió y qué puede hacer el usuario.
- Confirmar operaciones destructivas.
- Mantener accesibilidad básica: contraste, foco por teclado, nombres accesibles y tamaños razonables.

## 6.4 Componentes reutilizables

Crear componentes compartidos cuando aporten consistencia:
- Botón principal, secundario y de peligro.
- Tarjeta de contenido.
- Tarjeta de dispositivo.
- Indicador de estado.
- Diálogo de confirmación.
- Notificación/toast.
- Selector de perfil.
- Control de búsqueda/filtro.
- Estado vacío.
- Estado de carga.
- Panel de error.
- Encabezado de sección.

No duplicar el mismo diseño en cada módulo.

---

# 7. Sistema de módulos

Cada módulo debe estar separado y comunicarse con el resto mediante contratos o servicios públicos.

## 7.1 Contrato conceptual de módulo

Cada módulo debe declarar:
- Identificador único.
- Nombre visible.
- Descripción.
- Estado: disponible, activo, deshabilitado o con error.
- Requisitos o dependencias.
- Vista o punto de entrada visual.
- Servicio de inicialización, si lo necesita.
- Método de activación y desactivación.
- Recursos que debe liberar.

No es necesario crear un sistema complejo de plugins dinámicos desde la primera versión. Se puede comenzar con módulos internos registrados en el arranque. La arquitectura debe permitir separar o ampliar módulos más adelante.

## 7.2 Aislamiento

- Un fallo de HidHide no debe impedir abrir Mandos.
- Un error del módulo de mandos virtuales no debe cerrar GDM.
- Un escaneo Bluetooth fallido no debe bloquear la navegación.
- Los módulos no deben escribir directamente en los archivos de otros módulos.
- Los datos compartidos deben pasar por servicios o contratos definidos.

## 7.3 Comunicación entre módulos

Ejemplos:
- Mandos informa que un dispositivo está conectado.
- Perfiles consulta los mandos guardados para mostrar asignaciones.
- Remapeo carga el perfil seleccionado.
- Pruebas utiliza el flujo de entrada del dispositivo activo.
- Mandos virtuales puede recibir una configuración de salida si se implementa esa integración.
- Diagnóstico recibe errores de los servicios.

Usar eventos internos o interfaces de aplicación para estas comunicaciones. Evitar referencias directas entre vistas.

---

# 8. Sistema de datos y persistencia: JSON, sin SQLite

El usuario quiere minimizar instalaciones. Por tanto, el sistema principal debe utilizar archivos JSON y APIs incluidas en .NET siempre que sea posible.

No usar SQLite. No introducir un motor de base de datos ni un servidor local.

La persistencia se organiza como un **almacenamiento local basado en archivos**, con catálogos e índices JSON y archivos separados para cada entidad cuando corresponda.

## 8.1 Ubicación de datos

Elegir una ubicación persistente adecuada para Windows, preferentemente dentro de la carpeta de datos de usuario de la aplicación (por ejemplo, una subcarpeta de `LocalAppData` o `ApplicationData`), en vez de escribir junto al ejecutable si este puede estar instalado en una carpeta protegida.

La IA debe definir una única ruta raíz de datos y un servicio central para resolver las rutas. No repartir rutas absolutas por todo el código.

Estructura conceptual:

```text
GDM-Data/
├── Settings/
│   └── app-settings.json
├── Controllers/
│   ├── controllers-index.json
│   └── <controller-id>.json
├── Profiles/
│   ├── profiles-index.json
│   └── <profile-id>.json
├── Modules/
│   ├── hidhide/
│   └── virtual-controllers/
├── Cache/
├── Backups/
└── Logs/
```

La estructura concreta debe ser coherente con la documentación específica de la pestaña Mandos. No mantener dos catálogos distintos que compitan por ser la fuente de verdad.

## 8.2 Qué datos van en cada archivo

### Configuración general

`app-settings.json` almacena preferencias de la aplicación:
- Tema y apariencia.
- Idioma, si se implementa.
- Preferencias de inicio.
- Preferencias globales de detección.
- Ajustes de notificaciones.
- Preferencias generales de módulos.
- Opciones globales que no pertenecen a un mando o perfil individual.

No almacenar aquí remapeos particulares de cada mando.

### Catálogo de mandos

`controllers-index.json` contiene el listado de mandos guardados, sus IDs internos y las rutas a sus archivos individuales. Los detalles de su estructura se definen en la documentación específica de la pestaña Mandos.

### Archivos individuales de mandos

Cada mando guardado tiene su archivo propio. Allí están identidad, perfil asignado, remapeo y ajustes individuales. La detección temporal no debe crear archivos.

### Catálogo de perfiles

`profiles-index.json` contiene IDs, nombres y rutas de los perfiles disponibles.

### Archivos individuales de perfiles

Cada perfil reutilizable puede tener su propio JSON con:
- Nombre y descripción.
- Versión del esquema.
- Asignaciones de botones.
- Asignaciones de ejes.
- Zonas muertas y sensibilidad.
- Opciones de vibración u otras opciones soportadas.
- Metadatos de creación/modificación.

**Diferencia entre perfil y configuración de mando:** un perfil es una configuración reutilizable que se puede asignar a uno o más mandos. El archivo del mando conserva qué perfil tiene asignado y también los ajustes específicos de esa unidad. Si el programa permite personalizaciones exclusivas del mando, deben almacenarse en su archivo individual y combinarse con el perfil mediante reglas explícitas.

No duplicar silenciosamente los mismos datos en varios archivos. Definir cuál es la fuente de verdad y cómo se resuelven los ajustes del perfil frente a las excepciones del mando.

### Datos de módulos

Cada módulo puede tener su propia carpeta para datos específicos, pero debe respetar los servicios de persistencia y las reglas generales de versionado y respaldo. No debe guardar preferencias generales de GDM dentro de su carpeta.

## 8.3 Tecnología JSON

- Preferir `System.Text.Json` si es compatible con el .NET seleccionado.
- No instalar Newtonsoft.Json si no existe una necesidad concreta que `System.Text.Json` no resuelva.
- Definir opciones de serialización consistentes.
- Versionar cada esquema con `schemaVersion`.
- Validar los datos después de deserializar.
- Definir valores predeterminados cuando falten campos opcionales.
- Manejar campos desconocidos de forma compatible cuando sea posible.
- Evitar serializar objetos de UI o clases internas de framework.

## 8.4 Escritura y recuperación

Toda escritura importante debe:
1. Validar los datos.
2. Serializar a un archivo temporal.
3. Comprobar que la escritura finalizó.
4. Reemplazar el archivo anterior de forma segura.
5. Mantener respaldo cuando sea necesario.

Manejar:
- Archivo inexistente.
- JSON corrupto.
- Permisos insuficientes.
- Archivo bloqueado.
- Espacio insuficiente.
- Versión de esquema futura o incompatible.

No borrar automáticamente datos por un error de lectura. Informar y ofrecer recuperación.

## 8.5 Configuración y respaldo

- Crear automáticamente valores predeterminados si un archivo general no existe.
- Permitir restaurar ajustes predeterminados sin borrar perfiles ni mandos guardados, salvo que el usuario elija explícitamente borrar todo.
- Permitir exportar/importar configuraciones y perfiles en una fase posterior.
- Diseñar un mecanismo de copia de seguridad antes de migraciones importantes.

---

# 9. Sistema de configuración y perfiles

Debe existir una distinción clara entre:

1. Configuración general de GDM.
2. Perfil reutilizable.
3. Configuración particular del mando.
4. Estado temporal de conexión.

No mezclar estos cuatro tipos de información.

## 9.1 Configuración general

Se modifica desde Ajustes y afecta a la aplicación o a todos los módulos según corresponda.

## 9.2 Perfiles

Los perfiles son conjuntos de asignaciones y parámetros que el usuario puede crear, duplicar, renombrar, editar y eliminar.

Cada perfil debe tener un ID interno estable. No identificar perfiles únicamente por su nombre.

## 9.3 Asignación de perfil

Un mando guardado puede tener un perfil asignado. Al reconocer el mando, GDM recupera esa asignación y aplica el perfil según las reglas de remapeo.

Si un perfil se elimina:
- No dejar referencias inválidas silenciosas.
- Informar qué mandos lo utilizaban.
- Permitir reasignar un perfil o usar configuración predeterminada.
- No borrar los archivos de mandos.

## 9.4 Precedencia de configuración

Definir y documentar cómo se combinan:
- Valores predeterminados.
- Perfil seleccionado.
- Excepciones particulares del mando.

Una regla inicial recomendada:
1. Valores predeterminados del sistema.
2. Valores del perfil asignado.
3. Excepciones explícitas guardadas para ese mando.

La excepción del mando prevalece solo para las propiedades que realmente tenga definidas. Una propiedad no especificada hereda el valor del perfil o del sistema. No sobrescribir configuraciones con valores vacíos accidentalmente.

---

# 10. Servicios centrales

La aplicación debe exponer servicios con responsabilidades claras. Los nombres son orientativos.

- `IAppSettingsService`: carga y guarda ajustes generales.
- `IStoragePathService`: resuelve rutas de datos.
- `IJsonFileService`: operaciones JSON comunes y seguras.
- `IControllerCatalogService`: catálogo de mandos guardados.
- `IControllerDetectionService`: consulta de dispositivos activos.
- `IDeviceIdentityService`: asociación segura de identidades.
- `IProfileService`: catálogo, lectura y escritura de perfiles.
- `IInputService`: estado de botones, ejes y entrada.
- `IModuleManager`: registro y ciclo de vida de módulos.
- `INotificationService`: mensajes y notificaciones.
- `ILogService`: logs.
- `IDiagnosticsService`: diagnóstico y estado de componentes.

No es obligatorio crear una interfaz para cada clase pequeña. Crear contratos cuando permitan separar responsabilidades, sustituir implementaciones o probar el sistema.

---

# 11. Procesamiento en segundo plano y eventos

Las operaciones lentas no deben ejecutarse en el hilo de interfaz.

- Usar tareas asíncronas cuando corresponda.
- Admitir cancelación con `CancellationToken`.
- Evitar dos escaneos simultáneos del mismo recurso.
- Proteger colecciones compartidas frente a modificaciones concurrentes.
- Actualizar la UI mediante el mecanismo correcto del framework seleccionado.
- Desuscribirse de eventos al cerrar vistas, servicios o aplicación.
- Evitar ciclos de eventos que provoquen actualizaciones infinitas.
- No consultar hardware a intervalos excesivamente cortos.

Crear un modelo de eventos internos para sucesos importantes, por ejemplo:
- `ControllerConnected`.
- `ControllerDisconnected`.
- `ControllerIdentityResolved`.
- `ProfileChanged`.
- `SettingsChanged`.
- `ModuleStateChanged`.
- `OperationFailed`.

Los eventos deben contener datos necesarios y no referencias a controles visuales.

---

# 12. Logs, diagnóstico y errores

GDM debe poder explicar por qué una función no funciona sin mostrar errores técnicos incomprensibles al usuario.

## 12.1 Niveles de log

- Debug: detalles para desarrollo.
- Information: operaciones normales importantes.
- Warning: situación recuperable o dato no disponible.
- Error: operación fallida.
- Critical: fallo que compromete el funcionamiento general.

## 12.2 Reglas

- No registrar contraseñas, tokens ni datos privados.
- Evitar registrar direcciones MAC completas.
- No escribir continuamente el estado de cada botón en el log normal.
- Incluir módulo, operación, fecha y excepción técnica cuando corresponda.
- La UI muestra mensajes sencillos y el log conserva el detalle técnico.

## 12.3 Diagnóstico

Una vista de diagnóstico puede mostrar:
- Versión de GDM.
- Versión de .NET.
- Versión de SDL.
- Módulos disponibles y estado.
- Estado de servicios.
- Ruta de datos.
- Errores recientes.
- Dependencias opcionales detectadas.

No mostrar datos sensibles sin necesidad.

---

# 13. Seguridad y permisos

- Solicitar permisos elevados solo cuando una función realmente los necesite.
- No ejecutar toda la aplicación como administrador por comodidad.
- Aislar operaciones privilegiadas en el componente que las requiera.
- Validar rutas de archivos recibidas desde JSON.
- No ejecutar comandos arbitrarios obtenidos de archivos de configuración.
- Validar importaciones de perfiles y datos externos.
- Confirmar acciones destructivas.
- No eliminar dispositivos emparejados en Windows al borrar un registro de GDM, salvo acción independiente y explícita.
- Explicar claramente cuándo Windows necesita que el usuario complete una operación.

---

# 14. Compatibilidad y dependencias

## 14.1 Principio

La aplicación debe instalar la menor cantidad posible de componentes externos.

Antes de agregar una dependencia:
1. Verificar si .NET ya incluye la funcionalidad.
2. Verificar si una biblioteca existente en el proyecto la resuelve.
3. Evaluar mantenimiento, licencia, tamaño y compatibilidad.
4. Explicar por qué es necesaria.
5. No actualizar el framework por comodidad.

## 14.2 SDL

SDL se utiliza para las funciones de entrada y detección de mandos que correspondan a la versión integrada.

- Confirmar SDL2 o SDL3 y no mezclar APIs.
- Encapsular SDL en Infrastructure.
- No permitir que las vistas dependan de tipos SDL.
- Gestionar inicialización, eventos y liberación correctamente.
- Documentar limitaciones de la versión utilizada.

## 14.3 Funciones opcionales

HidHide, mandos virtuales y otras integraciones pueden requerir componentes o controladores externos. Deben tratarse como requisitos opcionales y no bloquear la instalación ni el inicio de GDM.

La interfaz debe detectar si están disponibles y mostrar:
- Disponible.
- No instalado.
- Versión incompatible.
- Permisos insuficientes.
- Error de comunicación.

No afirmar que una función está activa si solo se ha detectado el nombre de un archivo o paquete.

---

# 15. Pruebas y calidad

La arquitectura debe permitir probar servicios sin depender siempre de un mando físico.

## 15.1 Pruebas unitarias

- Serialización y deserialización JSON.
- Validación de configuración.
- Migración de esquemas.
- Reglas de precedencia de perfiles.
- Catálogo de mandos.
- Asociación de identidad con datos simulados.
- Gestión de errores.
- Estados de módulos.

## 15.2 Pruebas de integración

- Carga de configuración al iniciar.
- Guardado y recuperación de perfiles.
- Registro y carga de mandos guardados.
- Comunicación entre módulos.
- Inicio y cierre ordenado.
- Fallos de archivos y permisos.

## 15.3 Pruebas manuales en Windows

- Inicio en una instalación limpia.
- Navegación entre todas las secciones.
- Conexión y desconexión de mandos.
- Uso de mandos Xbox y PlayStation cuando estén disponibles.
- Pruebas de botones y ejes.
- Remapeo y carga de perfiles.
- Reinicio de GDM y persistencia.
- Funcionamiento sin HidHide o sin módulo de mandos virtuales instalado.
- Cierre durante una operación de detección.

No declarar probada una función de hardware si no se ha probado con el hardware correspondiente.

---

# 16. Fases recomendadas para reconstruir el proyecto

No desarrollar todo a la vez. Crear una base estable primero.

## Fase 1 — Diseño y decisión tecnológica

- Inspeccionar el entorno.
- Elegir framework de UI y versión de .NET.
- Verificar SDL y dependencias.
- Definir estructura de solución.
- Crear un documento de decisiones técnicas.

**Resultado:** proyecto base y arquitectura acordada.

## Fase 2 — Aplicación principal

- Crear ventana principal.
- Crear navegación.
- Crear tema y componentes visuales comunes.
- Crear sistema de vistas.
- Crear estados vacíos y notificaciones.

**Resultado:** GDM abre y navega entre secciones, aunque algunos módulos todavía sean provisionales.

## Fase 3 — Núcleo y servicios compartidos

- Crear modelos centrales.
- Crear gestión de rutas.
- Crear lectura/escritura JSON.
- Crear configuración general.
- Crear logs y manejo de errores.
- Crear ciclo de vida de módulos.

**Resultado:** aplicación con núcleo estable y configuración persistente.

## Fase 4 — Módulo Mandos

Implementar según el documento específico **«Documentación de la pestaña Mandos de GDM»**. Ese documento define en detalle detección, guardado individual, identidad, USB/Bluetooth, batería, MAC, estados y pruebas.

**Resultado:** módulo Mandos funcional e integrado.

## Fase 5 — Perfiles y remapeo

- Crear catálogo de perfiles.
- Crear editor de perfiles.
- Implementar precedencia de valores.
- Integrar remapeo con el sistema de entrada.
- Asociar perfiles a mandos guardados.

**Resultado:** perfiles reutilizables y ajustes individuales coherentes.

## Fase 6 — Pruebas

- Implementar panel de prueba de entrada.
- Mostrar botones, ejes, sticks y gatillos.
- Probar vibración cuando el dispositivo lo soporte.
- Evitar interferir con otros módulos que utilicen el mando.

**Resultado:** módulo de diagnóstico funcional.

## Fase 7 — Integraciones opcionales

- HidHide.
- Mandos virtuales.
- Otras integraciones.

Cada integración debe ser independiente y detectar correctamente si sus requisitos están disponibles.

## Fase 8 — Estabilización

- Ejecutar pruebas.
- Corregir fallos.
- Revisar rendimiento.
- Revisar uso de memoria y liberación de recursos.
- Verificar persistencia.
- Preparar publicación y documentación de usuario.

---

# 17. Criterios de aceptación del programa principal

La base general de GDM se considera aceptable cuando:

1. La aplicación inicia sin requerir SQLite ni un servidor de base de datos.
2. La configuración general se guarda en JSON y sobrevive al reinicio.
3. Los datos persistentes se guardan en una ubicación apropiada del usuario.
4. La ventana principal y la navegación funcionan.
5. La UI no accede directamente a SDL ni a archivos JSON.
6. Los módulos tienen responsabilidades separadas.
7. Un error de un módulo no cierra toda la aplicación.
8. Las operaciones lentas no congelan la interfaz.
9. El cierre cancela tareas y libera recursos.
10. Los perfiles se almacenan y administran de forma independiente.
11. La configuración de un mando no se confunde con la configuración general.
12. Los módulos opcionales pueden estar ausentes sin impedir el inicio.
13. Los errores se registran y se presentan de forma comprensible.
14. La solución compila y las pruebas automatizadas pasan.
15. La estructura permite agregar nuevas funciones sin reescribir el núcleo.

---

# 18. Entregables obligatorios de la IA

Al finalizar cada fase, entregar:

- Resumen de lo implementado.
- Archivos y proyectos creados/modificados.
- Explicación de la arquitectura real.
- Dependencias agregadas y motivo.
- Resultado de compilación.
- Pruebas realizadas y resultados.
- Limitaciones conocidas.
- Tareas pendientes.

Al terminar la reconstrucción, entregar también:
- Diagrama de arquitectura.
- Árbol de carpetas definitivo.
- Descripción de cada archivo JSON.
- Guía para agregar un módulo nuevo.
- Guía para agregar una nueva pantalla.
- Guía para compilar y ejecutar el proyecto.

---

# 19. Relación con la documentación específica de la pestaña Mandos

Esta especificación define el sistema madre y la arquitectura general. La documentación **«Documentación de la pestaña Mandos de GDM»** define el comportamiento detallado del módulo Mandos.

La IA debe leer ambos documentos y respetar esta separación:

- Este documento: aplicación principal, capas, navegación, módulos, servicios comunes, configuración global y arquitectura de datos.
- Documento Mandos: detección, estados, identidad física, guardado voluntario, archivos individuales, perfiles asociados, USB/Bluetooth, MAC, batería y pruebas específicas.

Si hay una diferencia, no duplicar sistemas ni crear dos fuentes de verdad. Mantener un único servicio de persistencia y un único catálogo de mandos, y hacer que el módulo Mandos utilice los contratos y rutas definidos por el núcleo.

**Regla final:** construir primero un núcleo pequeño, estable y modular. Después integrar cada módulo de forma independiente. No convertir la ventana principal en una clase gigantesca que contenga toda la lógica de GDM.
