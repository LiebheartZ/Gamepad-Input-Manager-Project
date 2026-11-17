# GDM

Herramienta de escritorio en Python para administrar y revisar gamepads en tiempo real. Muestra
el nombre, ID SDL, GUID, VID/PID, clasificación aproximada del modelo, batería
cuando el dispositivo la expone, ejes y botones. En Windows intenta obtener el
porcentaje mediante `RawGameController`; para DualSense también puede leer su
informe HID en la interfaz que coincide exactamente con la ruta SDL. Ese valor
HID es aproximado, en pasos de 10%, y se marca con `~`. Si ninguna fuente lo
expone, muestra la categoría aproximada de SDL o indica que no está disponible.
El panel derecho ilustra sticks,
gatillos y botones comunes; los controles activos se resaltan. Incluye una
sección Bluetooth para buscar dispositivos, emparejar y quitar emparejamientos.
La ilustración del tester adapta la forma y la distribución de controles según
el tipo de mando detectado (PlayStation, Xbox o genérico). En mandos
PlayStation, los símbolos de los botones se dibujan como iconos y el touchpad
se resalta al pulsar su botón integrado.
En Windows, Home de Xbox One se lee mediante `XInputGetStateEx`; si SDL no
identifica el slot XInput y hay varios mandos con el mismo VID/PID, GDM no
adivina cuál corresponde al seleccionado.

## Remapeo y mando virtual

La pestaña **Remapeo** muestra una pestaña por cada mando físico conectado,
con un icono y una etiqueta compacta; los mandos virtuales no aparecen en esta
lista. Cada mando puede usar el perfil predeterminado o tener un perfil
personalizado independiente. En **Configuración > Mandos** se edita el perfil
predeterminado. Para un perfil personalizado puedes seleccionar una salida
virtual **DualShock 4** o **Xbox 360** y remapear botones haciendo clic en sus
filas, configurar el inicio automático de la salida al conectar ese mando y
guardar su color LED compatible de forma independiente. Los ejes y gatillos se
transfieren directamente. Los perfiles personalizados permanecen disponibles
en la fila de pestañas aunque su mando esté desconectado; se pueden editar y
sus preferencias de inicio y LED se aplicarán al volver a conectarlo.
En cada perfil personalizado, **Configurar apagado** permite activar una acción
manteniendo Home/PS o una combinación configurable de botones durante 2
segundos. Puedes elegir entre desconectar el dispositivo de Windows conservando
el emparejamiento Bluetooth, o desemparejarlo y eliminar el vínculo guardado.
También puedes activar el apagado por inactividad y elegir de 1 a 60 minutos.
La cuenta empieza al conectar el mando y se reinicia mientras se pulsen botones,
se muevan sticks o gatillos, o se use la cruceta. Al cumplirse el tiempo se usa
la misma acción configurada para Home/PS o la combinación, incluyendo
desconectar o desemparejar. Estas acciones no apagan físicamente el mando.

La salida virtual es opcional y requiere `vgamepad` y el driver ViGEmBus
instalado por separado. ViGEmBus está retirado y ya no recibe actualizaciones;
GDM no descarga ni instala drivers. Para instalar el wrapper Python sin lanzar
el instalador de driver que incluye su paquete, usa PowerShell:

```powershell
$env:VGAMEPAD_SKIP_VIGEMBUS_INSTALL = "true"
py -m pip install vgamepad
Remove-Item Env:VGAMEPAD_SKIP_VIGEMBUS_INSTALL
```

Después instala ViGEmBus manualmente desde una fuente oficial de confianza y
reinicia GDM. Al activar un mando virtual, algunos juegos pueden detectar tanto
el mando físico como el virtual; configura el juego para usar solo el virtual si
es necesario. La emulación requiere Windows y no se activa hasta que se pulsa
**Iniciar salida virtual**. GDM mantiene una salida virtual independiente por
cada mando físico, por lo que puedes usar varios a la vez. Al desconectar un
mando, se detiene únicamente su salida virtual.

## HidHide

La pestaña **HidHide** permite consultar los dispositivos de juego detectados por
HidHide, seleccionar un mando, agregarlo o quitarlo de la lista de
ocultamiento y activar o desactivar el filtro. GDM usa `HidHideCLI.exe`; no
instala ni habilita el driver por su cuenta. Instala HidHide únicamente desde su
[repositorio oficial](https://github.com/nefarius/HidHide/releases).
GDM muestra los mandos detectados por HidHide sin consultar el árbol PnP de
Windows. Si no identifica automáticamente un mando virtual, selecciónalo y
pulsa **Marcar virtual** o **Marcar físico**; GDM guarda el ID del dispositivo
y conserva esa clasificación entre reinicios. Los mandos marcados como
virtuales no se pueden ocultar desde esta pestaña.

Primero pulsa **Autorizar GDM**. Esto agrega la aplicación actual a la lista de
permitidas de HidHide para que GDM pueda seguir leyendo el mando y remapearlo.
Después selecciona un mando y pulsa **Ocultar mando**; finalmente activa el
filtro. Al ejecutar GDM desde el código fuente, HidHide autoriza el intérprete
de Python actual; esto también permite el acceso a otros programas que se
ejecuten con ese mismo intérprete. Una versión empaquetada autoriza su propio
ejecutable. Los cambios de configuración pueden requerir ejecutar GDM como
administrador. HidHide oculta los mandos de otras aplicaciones solo cuando el
filtro está activado y los mandos correspondientes están seleccionados para
ocultarse.

## Ejecución en Windows

1. Instala Python 3.10 o posterior.
2. En esta carpeta, instala las dependencias:

   ```powershell
   py -m pip install -r requirements.txt
   ```

3. Inicia la aplicación:

   ```powershell
   pyw .\gdm.pyw
   ```

Al iniciar, GDM muestra una bienvenida y revisa los paquetes Python, vgamepad,
ViGEmBus y HidHide. En **Configuración > Dependencias**, GDM puede ejecutar
`python -m pip install -r requirements.txt` o instalar vgamepad sin iniciar el
instalador de ViGEmBus incluido por terceros. Para ViGEmBus e HidHide, GDM abre
sus páginas oficiales; sus instaladores requieren confirmación propia y pueden
solicitar permisos de administrador. La detección y la instalación de drivers
solo están disponibles en Windows.

`gdm.pyw` inicia GDM sin abrir una terminal de Windows. También puedes abrirlo
con doble clic desde el Explorador de archivos. Para mostrar la consola de
diagnóstico de Python, usa `py .\gdm.py`. El icono de la ventana y de la
aplicación es `gdm_icon.png`. En Windows solo se permite una instancia de GDM a
la vez para evitar que varias salidas virtuales queden activas en paralelo. Si
abres GDM cuando ya se está ejecutando, la instancia activa restaura su ventana
desde la bandeja si hace falta y la pone en primer plano.

Conecta el mando antes o después de iniciar. La pestaña inicial **Mandos**
muestra los dispositivos conectados, su tipo (PlayStation, Xbox o genérico),
transporte (USB o Bluetooth), batería cuando SDL puede leerla y una etiqueta
**MANDO VIRTUAL** o **MANDO FÍSICO** cuando HidHide puede identificarlo. Las
etiquetas usan la clasificación automática o manual de HidHide; si no hay
coincidencia disponible, se indica **TIPO NO IDENTIFICADO**. Si hay más
dispositivos de los que caben, usa la rueda del ratón para recorrer la lista.
**Probar con Windows** abre el panel de dispositivos de juego de Windows. En
**Gamepad Tester**, usa los botones `<` y `>` del panel de lecturas para cambiar
entre mandos, `R` para volver a buscar y `Esc` para cerrar o minimizar GDM. La
barra superior también permite alternar entre **Bluetooth**, **Remapeo**,
**HidHide**, **Configuración** y **Acerca de**. La pestaña Acerca de muestra el
creador, el año de creación y la versión actual (0.1.0).

También puedes recorrer las pestañas y opciones de la interfaz con las flechas
del teclado y activar el elemento enfocado con **Enter**. Esta navegación no
cambia los controles del mando.

En **Remapeo** puedes activar **Iniciar salida virtual automáticamente al
conectar un mando**. Si está habilitado, GDM inicia la salida para el nuevo
mando físico detectado. La lista de **Mandos** consolida los dos alias SDL que
Windows puede publicar para una misma salida Xbox virtual; esto solo cambia la
presentación de esa lista, no la enumeración del tester.

Los perfiles de **Remapeo** se identifican por mando físico. Activa **Establecer
un perfil personalizado para este mando** para guardar su asignación de botones
y su tipo de salida virtual por separado; si lo dejas desactivado, ese mando usa
el perfil predeterminado compartido, editable en **Configuración > Mandos**,
donde también puedes elegir la salida virtual predeterminada y el mapeo base.
GDM asocia los perfiles a la ruta del dispositivo SDL para distinguir dos
mandos iguales y conservar sus perfiles al reconectarlos. Si SDL no proporciona
una ruta, GDM avisa que el identificador disponible puede no persistir.

En esa pestaña también puedes cambiar el color de la barra LED de mandos
DualSense y DualShock 4 físicos compatibles. Para DualSense, GDM valida la ruta
exacta del dispositivo para no confundirlo con la salida virtual y envía el
informe HID nativo por USB o Bluetooth; DualShock 4 usa el control LED de SDL.
El color seleccionado se guarda y se vuelve a enviar varias veces durante los
primeros segundos al conectar un mando compatible, para que prevalezca sobre el
color predeterminado que el mando establece al terminar de inicializarse.
El ajuste nunca se envía a mandos virtuales ni Xbox; si el modelo no está
soportado o no puede identificarse su interfaz física, GDM lo indica y no envía
el cambio. Pulsa **Desactivar** junto a los colores para evitar que GDM cambie
el LED. Para
distinguir USB de Bluetooth, GDM prioriza la ruta del dispositivo HID sobre el
número de interfaz informado por hidapi.

Al pulsar **Escape**, GDM pregunta si quieres cerrar el programa o minimizarlo
a la bandeja. Puedes marcar **Recordar mi elección para las próximas veces**
para aplicar esa acción directamente en futuras pulsaciones. La pregunta se
puede activar o desactivar en **Configuración > General**. En esa misma sección
puedes activar o borrar la elección recordada y elegir si la **X** de la ventana
cierra GDM o lo minimiza a la bandeja.
El botón de minimizar (—) minimiza la ventana normalmente a la barra de tareas.
Haz clic derecho en el icono de GDM para **Abrir GDM** o **Cerrar programa**.

La consola de diagnóstico se muestra abajo a la derecha. Registra las llamadas
de búsqueda y emparejamiento, resultados y excepciones; los errores aparecen en
rojo y las operaciones exitosas en verde. Puedes minimizarla, expandirla y
copiar el texto del registro al portapapeles para compartirlo al reportar un
problema.

**Configuración** está organizada en secciones internas. En **General** puedes
iniciar GDM al iniciar Windows y elegir si ese inicio debe abrirlo oculto en la
bandeja. La opción de inicio automático crea o elimina
un acceso directo en la carpeta Inicio del usuario actual y usa `pythonw.exe`
con `gdm.pyw` para no abrir una terminal. La opción de inicio oculto solo tiene
efecto cuando se activa el inicio automático. En **General** también puedes
elegir el color del icono de bandeja: automático según el tema claro u oscuro de
Windows, blanco o negro. La detección automática se actualiza mientras GDM está
abierto. El logo nuevo se usa como icono de la aplicación y su silueta
monocromática como icono de bandeja. También puedes ocultar el botón **Abrir
consola**; la preferencia se conserva entre inicios. En **Mandos** puedes activar el
emparejamiento automático, filtrar para mostrar solo mandos y elegir qué acción
ejecutar al mantener Home/PS durante 2 segundos. GDM guarda estas preferencias
En **Interfaz**, junto a **General**, puedes elegir el tema oscuro, claro o
automático. El modo automático sigue la preferencia de tema de aplicaciones de
Windows y actualiza la interfaz mientras GDM está abierto. El tema predeterminado
es oscuro. En esa misma sección puedes elegir la pestaña que se abrirá al iniciar
GDM y volver a activar la bienvenida para que aparezca en el próximo inicio.
GDM guarda estas preferencias
automáticamente, junto con el estado de la consola, en Windows en
`%APPDATA%\GDM\settings.json`. Si encuentra preferencias guardadas por una
versión anterior, GDM las importa automáticamente a la nueva ubicación. Si
todavía no existe ninguna configuración, el
emparejamiento automático y el filtro de mandos empiezan activados, y la consola
minimizada. La pestaña inicial por defecto es Mandos y la solicitud de
desconexión al mantener Home/PS está activada. También se guarda el tamaño de la
ventana al redimensionarla y se restaura en el siguiente inicio; el tamaño mínimo
permitido es 800 × 600. La interfaz adapta el tamaño de los elementos y mantiene
la proporción del dibujo del mando en ventanas pequeñas. Si el contenido de
**Configuración** no cabe en la ventana, usa la rueda del ratón sobre el panel
para desplazarte.

La búsqueda de Bluetooth clásico y Bluetooth LE se mantiene mientras el programa
está abierto, también cuando se usa la pestaña Gamepad Tester. En Bluetooth,
selecciona un dispositivo y usa **Añadir / emparejar** o **Eliminar
emparejamiento**. Un indicador en la barra superior muestra si Bluetooth está
activo, desactivado o no disponible. GDM comprueba periódicamente el estado del
adaptador y avisa si Bluetooth está desactivado o si no hay adaptador disponible;
al volver a activarlo, reanuda la búsqueda automáticamente. La opción para emparejar automáticamente intenta añadir los
dispositivos reconocidos como mandos al detectarlos. El filtro **Mostrar solo
mandos** oculta el resto sin detener la búsqueda y puede desactivarse para
consultar todos los dispositivos. El filtro reconoce nombres genéricos como
`gamepad` y `Game Pad`, además de nombres habituales e identificadores VID/PID
conocidos. El modo automático
intenta emparejar cada mando no emparejado reconocido, incluso si Windows todavía
indica que no está listo; los resultados y fallos aparecen en la consola.
Windows puede mostrar una confirmación del sistema durante el emparejamiento. El mando debe estar en
modo de emparejamiento y con Bluetooth habilitado. El estado de Windows muestra
el resultado (por ejemplo, si el mando no está listo o si la operación fue
denegada). Los resultados de emparejar/eliminar quedan visibles en el estado
para que la búsqueda en segundo plano no los reemplace inmediatamente. Si un
emparejamiento automático falla porque el mando todavía no está listo, GDM
lo reintenta periódicamente mientras el dispositivo siga detectado; no hace falta
reiniciar la búsqueda.

Al activar la acción de Home/PS en **Configuración**, puedes elegir:

- **Desconexión Bluetooth**: solicita la desconexión directamente al adaptador
  Bluetooth de Windows mediante `IOCTL_BTH_DISCONNECT_DEVICE`, el método
  utilizado por DS4Windows para el DualShock 4. El vínculo permanece guardado
  y no hay que emparejarlo de nuevo. Windows puede rechazar la operación si no
  encuentra una conexión activa o si el adaptador no la admite; en ese caso se
  muestra el error en el estado y la consola.
- **Desemparejar el mando**: elimina su vínculo de Windows y puede desconectarlo.
  Para volver a utilizarlo por Bluetooth tendrás que emparejarlo otra vez. Si el
  emparejamiento automático está activado, GDM vuelve a intentarlo cada cinco
  segundos hasta que el mando entre en modo de emparejamiento y Windows lo acepte.
Si Windows todavía no ha actualizado la asociación Bluetooth cuando mantienes
Home/PS, GDM inicia una búsqueda y reintenta la acción sin volver a disparar el
emparejamiento automático.
La lectura de Home/PS se habilita también cuando GDM está minimizado o en
segundo plano, siempre que SDL y el controlador permitan entregar esas entradas.

## Limitaciones

- La detección USB/Bluetooth se intenta en Windows mediante la enumeración PnP
  de PowerShell y los identificadores VID/PID. Si hay varios dispositivos
  coincidentes, puede indicarse como ambiguo; en otras plataformas se muestra
  como no disponible.
- SDL/pygame no ofrece de forma estándar la versión de firmware del gamepad.
  El campo lo indica en vez de presentar un dato inventado.
- La batería se consulta mediante SDL cuando el mando y el sistema operativo la
  exponen; de lo contrario se muestra como no disponible.
- Cuando SDL reconoce el mando como GameController, se usa su mapeo estándar
  para sincronizar sticks, gatillos, Start/Select y botones entre modelos
  (incluido DualSense de PS5). Si no hay un mapeo disponible, la aplicación
  muestra los índices directos y avisa que dependen del modelo.
- La administración Bluetooth se implementa con las API de Windows; en otros
  sistemas la pestaña no puede buscar ni emparejar dispositivos. El modo
  automático filtra por nombres e identificadores reconocibles de mandos para
  evitar emparejar automáticamente otros tipos de dispositivos.
- El emparejamiento manual usa la API de emparejamiento personalizado de
  Windows y acepta confirmaciones simples y PIN que deban mostrarse. No requiere
  iniciar GDM como administrador. Si un mando exige introducir un PIN,
  confirmar una comparación numérica u otro método no compatible, GDM
  informa del motivo; se puede completar ese tipo de vínculo desde Windows.
- En la pestaña Bluetooth, el verde indica que Windows reporta el mando conectado;
  el azul indica que está emparejado pero desconectado. Los dispositivos solo
  detectados aparecen sin esos estados. Para actualizar cambios de conexión o
  emparejamiento, pulsa **Reiniciar búsqueda**; la lista no reinicia los
  buscadores automáticamente. Si GDM está leyendo el mando por SDL, también lo
  indica como conectado aunque Windows tarde en actualizar la propiedad de
  conexión.
