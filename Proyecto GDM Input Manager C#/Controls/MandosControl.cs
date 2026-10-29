using System.Windows.Forms;
using GDMInputManager.Services;
using GDMInputManager.Data;
using GDMInputManager.Models;

namespace GDMInputManager.Controls;

/// <summary>
/// Control de usuario para la pestaña "Mandos"
/// Estructura: UserControl con ListView para mostrar mandos y botones para acciones
/// Propósito: Mostrar y gestionar la lista de mandos (conectados y guardados)
/// Componentes:
///   - ListView: Muestra la lista de mandos con columnas (Nombre, Tipo, Conexión, Estado)
///   - Botón Actualizar: Refresca la lista de mandos conectados
///   - Botón Emparejar: (pendiente) Empareja un mando Bluetooth
///   - Botón Desemparejar: (pendiente) Desempareja un mando Bluetooth
/// Flujo de datos:
///   - Al cargar: Inicializa servicios y muestra mandos guardados
///   - Al actualizar: Detecta mandos conectados y actualiza la lista
///   - Colores: Azul=conectado, Negro=emparejado, Gris=no emparejado
/// </summary>
public partial class MandosControl : UserControl
{
    // Detector de mandos (SDL2, RawInput, etc.)
    private IGamepadDetector? _gamepadDetector;

    // Servicio de base de datos para mandos
    private GamepadDatabaseService? _databaseService;

    /// <summary>
    /// Constructor del control
    /// Estructura: Inicializa componentes y servicios
    /// Propósito: Preparar el control para su uso inmediato
    /// </summary>
    public MandosControl()
    {
        InitializeComponent();
        InitializeServices();
    }

    /// <summary>
    /// Inicializa los servicios necesarios (detector y base de datos)
    /// Estructura: Crea instancias de IGamepadDetector y GamepadDatabaseService
    /// Propósito: Preparar los servicios para detectar y persistir mandos
    /// Proceso:
    ///   1. Crea el detector SDL2
    ///   2. Inicializa el detector
    ///   3. Si tiene éxito, crea el servicio de base de datos
    ///   4. Refresca la lista de mandos inicial
    /// Logging: Todos los pasos se registran en LoggerService
    /// </summary>
    private void InitializeServices()
    {
        try
        {
            LoggerService.Instance.Info("Inicializando servicios de Mandos...");

            // Inicializar detector de mandos (SDL2 por defecto)
            _gamepadDetector = new SDL2GamepadDetector();
            LoggerService.Instance.Info($"Detector de mandos creado: {_gamepadDetector.Name}");

            if (_gamepadDetector.Initialize())
            {
                LoggerService.Instance.Success("Detector de mandos inicializado correctamente.");
                _databaseService = new GamepadDatabaseService(new JsonDataManager());
                LoggerService.Instance.Info("Servicio de base de datos de mandos inicializado.");
                RefreshGamepadList();
            }
            else
            {
                LoggerService.Instance.Error("No se pudo inicializar el detector de mandos.");
                MessageBox.Show("No se pudo inicializar el detector de mandos.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"Error al inicializar servicios: {ex.Message}");
            LoggerService.Instance.Error($"StackTrace: {ex.StackTrace}");
            MessageBox.Show($"Error al inicializar servicios: {ex.Message}\n\nStackTrace: {ex.StackTrace}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Refresca la lista de mandos conectados y guardados
    /// Estructura: Obtiene mandos conectados y guardados, los combina y muestra en ListView
    /// Propósito: Actualizar la UI para reflejar el estado actual de los mandos
    /// Proceso:
    ///   1. Detecta mandos conectados con SDL2
    ///   2. Carga mandos guardados de la base de datos
    ///   3. Marca mandos guardados como conectados si coinciden con los detectados
    ///   4. Agrega mandos nuevos a la base de datos
    ///   5. Muestra todos los mandos con colores según estado
    /// </summary>
    public void RefreshGamepadList()
    {
        if (_gamepadDetector == null || _databaseService == null)
        {
            LoggerService.Instance.Warning("Servicios no inicializados. No se puede refrescar la lista.");
            return;
        }

        try
        {
            LoggerService.Instance.Info("Refrescando lista de mandos...");
            listViewGamepads.Items.Clear();

            // Obtener mandos conectados
            var connectedGamepads = _gamepadDetector.GetConnectedGamepads();
            LoggerService.Instance.Info($"Mandos conectados detectados: {connectedGamepads.Count}");

            // Obtener todos los mandos guardados en la base de datos
            var savedGamepads = _databaseService.GetAllGamepads();
            LoggerService.Instance.Info($"Mandos guardados en BD: {savedGamepads.Count}");

            // Crear un set de mandos conectados para referencia rápida
            var connectedSet = new HashSet<string>();
            foreach (var connected in connectedGamepads)
            {
                connectedSet.Add($"{connected.Vid}-{connected.Pid}-{connected.MacAddress}");
            }

            // Mostrar todos los mandos guardados
            foreach (var savedGamepad in savedGamepads)
            {
                string key = $"{savedGamepad.Vid}-{savedGamepad.Pid}-{savedGamepad.MacAddress}";
                bool isConnected = connectedSet.Contains(key);

                savedGamepad.IsConnected = isConnected;

                if (isConnected)
                {
                    savedGamepad.LastUsed = DateTime.Now;
                    _databaseService.SaveGamepad(savedGamepad);
                }

                AddGamepadToList(savedGamepad, true); // isPaired = true (está en BD)
            }

            // Agregar mandos nuevos que no están en la BD
            foreach (var connectedGamepad in connectedGamepads)
            {
                string key = $"{connectedGamepad.Vid}-{connectedGamepad.Pid}-{connectedGamepad.MacAddress}";
                if (!savedGamepads.Any(g => $"{g.Vid}-{g.Pid}-{g.MacAddress}" == key))
                {
                    _databaseService.SaveGamepad(connectedGamepad);
                    LoggerService.Instance.Info($"Mando nuevo guardado: {connectedGamepad.Name}");
                    AddGamepadToList(connectedGamepad, false); // isPaired = false (acaba de agregarse)
                }
            }

            LoggerService.Instance.Success($"Lista de mandos actualizada: {listViewGamepads.Items.Count} dispositivos.");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"Error al refrescar la lista de mandos: {ex.Message}");
            LoggerService.Instance.Error($"StackTrace: {ex.StackTrace}");
            MessageBox.Show($"Error al refrescar la lista de mandos: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Agrega un mando al ListView con el color apropiado según su estado
    /// Estructura: Crea un ListViewItem con las columnas del mando y aplica color
    /// Propósito: Mostrar un mando en la lista con formato visual apropiado
    /// Proceso:
    ///   1. Crea el item con el nombre del mando
    ///   2. Agrega columnas: Tipo, Conexión, Estado
    ///   3. Aplica color según estado (azul=conectado, negro=emparejado, gris=no emparejado)
    /// Parámetros:
    ///   - gamepad: Objeto Gamepad a mostrar
    ///   - isPaired: true si está en BD, false si es nuevo
    /// </summary>
    private void AddGamepadToList(Gamepad gamepad, bool isPaired)
    {
        var item = new ListViewItem(gamepad.Name);
        item.SubItems.Add(gamepad.Type.ToString());
        item.SubItems.Add(gamepad.ConnectionType.ToString());

        // Estado
        string status = gamepad.IsConnected ? "Conectado" : "Desconectado";
        item.SubItems.Add(status);

        item.Tag = gamepad;

        // Colores según estado
        if (gamepad.IsConnected)
        {
            // Conectado: Azul
            item.ForeColor = Color.Blue;
        }
        else if (isPaired)
        {
            // Emparejado pero desconectado: Negro
            item.ForeColor = Color.Black;
        }
        else
        {
            // No emparejado y desconectado: Gris
            item.ForeColor = Color.Gray;
        }

        listViewGamepads.Items.Add(item);
    }

    /// <summary>
    /// Evento del botón Actualizar
    /// Propósito: Refrescar la lista de mandos cuando el usuario hace clic
    /// </summary>
    private void buttonRefresh_Click(object sender, EventArgs e)
    {
        RefreshGamepadList();
    }

    /// <summary>
    /// Evento del botón Emparejar (pendiente de implementación)
    /// Propósito: Emparejar un mando Bluetooth seleccionado
    /// </summary>
    private void buttonPair_Click(object sender, EventArgs e)
    {
        LoggerService.Instance.Warning("Función de emparejamiento pendiente de implementar.");
        MessageBox.Show("Función de emparejamiento pendiente de implementar.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Evento del botón Desemparejar (pendiente de implementación)
    /// Propósito: Desemparejar un mando Bluetooth seleccionado
    /// </summary>
    private void buttonUnpair_Click(object sender, EventArgs e)
    {
        LoggerService.Instance.Warning("Función de desemparejamiento pendiente de implementar.");
        MessageBox.Show("Función de desemparejamiento pendiente de implementar.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
