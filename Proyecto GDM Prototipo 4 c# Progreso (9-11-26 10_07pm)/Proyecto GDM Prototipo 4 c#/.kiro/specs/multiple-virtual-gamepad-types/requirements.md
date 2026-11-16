# Requirements Document

## Introduction

El sistema GDM (Gamepad Device Manager) actualmente permite emular un mando virtual de Xbox 360 a través de HIDMaestro. Este sistema de requisitos define la funcionalidad para extender el soporte a múltiples tipos de mandos virtuales, específicamente añadiendo soporte para DualShock 4, mientras se mantiene la compatibilidad con perfiles existentes y la estructura actual del sistema.

## Glossary

- **GDM**: Gamepad Device Manager - Aplicación WPF/C# para gestión de mandos de juego
- **Controlador_Físico**: Dispositivo físico de entrada (mando) detectado por SDL2
- **Controlador_Virtual**: Dispositivo virtual emulado por HIDMaestro
- **HIDMaestro**: Biblioteca/API para creación de dispositivos HID virtuales en Windows
- **Perfil**: Configuración guardada que incluye mapeos de botones, ejes, zonas muertas e inversiones
- **Tipo_Controlador_Virtual**: Especificación del modelo de mando virtual a emular (Xbox 360, DualShock 4, etc.)
- **SDL2**: Simple DirectMedia Layer 2 - Biblioteca para detección y lectura de mandos
- **Interfaz_Usuario_GDM**: Interfaz gráfica de usuario de la aplicación GDM
- **Servicio_Mando_Virtual**: Componente que maneja la creación y gestión de mandos virtuales
- **Repositorio_Perfiles**: Sistema de almacenamiento persistente de perfiles en formato JSON

## Requirements

### Requirement 1: Extensión del Modelo de Perfil

**User Story:** Como usuario avanzado de GDM, quiero poder seleccionar el tipo de mando virtual para cada perfil, para que pueda emular diferentes modelos de controladores según mis necesidades de compatibilidad con juegos.

#### Acceptance Criteria

1. WHEN se crea un nuevo perfil, THE Interfaz_Usuario_GDM SHALL mostrar un selector de tipo de mando virtual con opciones "Xbox 360" y "DualShock 4"
2. WHERE el tipo de mando virtual es seleccionado, THE Repositorio_Perfiles SHALL persistir el tipo seleccionado en el archivo JSON del perfil
3. WHEN se carga un perfil existente sin tipo de mando virtual definido, THE Sistema SHALL asignar "Xbox 360" como valor predeterminado para mantener compatibilidad
4. WHEN se edita un perfil existente, THE Interfaz_Usuario_GDM SHALL mostrar y permitir modificar el tipo de mando virtual actual del perfil
5. THE Perfil SHALL incluir una propiedad "TipoControladorVirtual" con valores válidos: "Xbox360", "DualShock4"

### Requirement 2: Creación de Mando Virtual DualShock 4

**User Story:** Como usuario de GDM, quiero que al seleccionar DualShock 4 como tipo de mando virtual, el sistema cree correctamente un mando virtual de PlayStation 4, para poder emular este tipo de controlador en juegos compatibles.

#### Acceptance Criteria

1. WHEN un perfil con tipo "DualShock4" es asignado a un Controlador_Físico y la salida virtual es iniciada, THE Servicio_Mando_Virtual SHALL crear un dispositivo virtual DualShock 4 usando HIDMaestro
2. THE Servicio_Mando_Virtual SHALL configurar correctamente el descriptor HID del DualShock 4 con sus botones, ejes, y capacidades específicas
3. WHEN se inicia una salida virtual DualShock 4, THE Sistema SHALL mapear correctamente las entradas del Controlador_Físico según el perfil asignado
4. THE HIDMaestro SHALL exponer el dispositivo virtual DualShock 4 al sistema operativo Windows como un controlador de PlayStation 4 compatible
5. WHEN se detiene la salida virtual DualShock 4, THE Servicio_Mando_Virtual SHALL eliminar correctamente el dispositivo virtual

### Requirement 3: Compatibilidad con Perfiles Existentes

**User Story:** Como usuario existente de GDM, quiero que mis perfiles guardados sigan funcionando sin modificaciones, para no perder mi configuración actual al actualizar la aplicación.

#### Acceptance Criteria

1. WHEN se carga un perfil JSON existente (SchemaVersion = 1) sin la propiedad "TipoControladorVirtual", THE Sistema SHALL interpretarlo como tipo "Xbox360" automáticamente
2. THE SchemaVersion del modelo Perfil SHALL incrementarse a 2 para incluir la nueva propiedad
3. WHEN se guarda un perfil existente modificado, THE Repositorio_Perfiles SHALL actualizar el archivo JSON incluyendo el tipo de mando virtual actual
4. FOR ALL perfiles existentes, la migración a SchemaVersion 2 SHALL ser transparente y no requerir acción manual del usuario

### Requirement 4: Interfaz de Usuario para Selección de Tipo

**User Story:** Como usuario de GDM, quiero una interfaz clara e intuitiva para seleccionar y cambiar el tipo de mando virtual en cada perfil, para poder configurar fácilmente mis preferencias.

#### Acceptance Criteria

1. THE Interfaz_Usuario_GDM SHALL mostrar el tipo de mando virtual actual en la vista de detalles del perfil
2. WHEN se edita un perfil, THE Interfaz_Usuario_GDM SHALL incluir un control de selección (combobox) con las opciones "Xbox 360" y "DualShock 4"
3. THE Interfaz_Usuario_GDM SHALL mostrar un ícono o indicador visual que represente el tipo de mando virtual seleccionado
4. WHEN se crea un nuevo perfil, THE Interfaz_Usuario_GDM SHALL pre-seleccionar "Xbox 360" como tipo predeterminado
5. THE Interfaz_Usuario_GDM SHALL proporcionar tooltips o ayuda contextual explicando las diferencias entre los tipos de mandos virtuales

### Requirement 5: Mapeo de Controles Específico por Tipo

**User Story:** Como usuario avanzado, quiero que el sistema maneje correctamente las diferencias en botones y ejes entre Xbox 360 y DualShock 4, para que los mapeos de perfil sean apropiados para cada tipo de mando.

#### Acceptance Criteria

1. WHERE el tipo de mando virtual es "Xbox360", THE Sistema SHALL usar el conjunto de botones estándar de Xbox 360 (A, B, X, Y, LB, RB, etc.)
2. WHERE el tipo de mando virtual es "DualShock4", THE Sistema SHALL usar el conjunto de botones estándar de PlayStation 4 (×, ○, □, △, L1, R1, etc.)
3. THE Sistema SHALL proporcionar plantillas de mapeo predeterminadas apropiadas para cada tipo de mando virtual
4. WHEN se cambia el tipo de mando virtual en un perfil existente, THE Interfaz_Usuario_GDM SHALL advertir al usuario sobre posibles conflictos en mapeos de botones
5. THE Sistema SHALL validar que los mapeos de botones en un perfil sean compatibles con el tipo de mando virtual seleccionado

### Requirement 6: Gestión de Múltiples Tipos Simultáneos

**User Story:** Como usuario que maneja varios controladores, quiero poder tener diferentes tipos de mandos virtuales activos simultáneamente, para emular diferentes plataformas en la misma sesión.

#### Acceptance Criteria

1. WHEN múltiples Controladores_Físicos tienen perfiles con diferentes tipos de mando virtual asignados, THE Sistema SHALL poder crear y mantener todos los tipos de mandos virtuales simultáneamente
2. THE HIDMaestro SHALL soportar la coexistencia de dispositivos virtuales Xbox 360 y DualShock 4 en el mismo sistema
3. WHEN se inician salidas virtuales de diferentes tipos, THE Sistema SHALL identificar cada dispositivo virtual correctamente por su tipo y perfil asociado
4. THE Interfaz_Usuario_GDM SHALL mostrar claramente el tipo de cada mando virtual activo en la lista de mandos
5. WHEN se detienen salidas virtuales de diferentes tipos, THE Sistema SHALL manejar correctamente la liberación de recursos específicos de cada tipo

### Requirement 7: Extensibilidad para Futuros Tipos

**User Story:** Como desarrollador de GDM, quiero una arquitectura extensible para añadir nuevos tipos de mandos virtuales en el futuro, para poder soportar más plataformas sin reescribir código significativo.

#### Acceptance Criteria

1. THE Sistema SHALL usar un patrón de diseño que permita añadir nuevos tipos de mandos virtuales con cambios mínimos en el código base
2. WHERE un nuevo tipo de mando virtual es añadido, THE Interfaz_Usuario_GDM SHALL automáticamente incluirlo en los selectores sin requerir cambios en la lógica de presentación
3. THE Sistema SHALL definir una interfaz o clase base para todos los tipos de mandos virtuales
4. THE Repositorio_Perfiles SHALL validar que el tipo de mando virtual almacenado sea uno de los tipos soportados actualmente
5. WHEN se intenta cargar un perfil con un tipo de mando virtual no soportado, THE Sistema SHALL mostrar un error claro y usar un tipo predeterminado seguro