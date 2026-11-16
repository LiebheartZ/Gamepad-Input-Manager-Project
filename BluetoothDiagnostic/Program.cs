using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace BluetoothDiagnostic
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.Title = "Diagnóstico Bluetooth GDM";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================");
            Console.WriteLine("   DIAGNÓSTICO BLUETOOTH PARA GDM");
            Console.WriteLine("==========================================");
            Console.ResetColor();
            Console.WriteLine();
            
            try
            {
                await CheckBluetoothRadios();
                await CheckAllBluetoothDevices();
                await CheckBluetoothLEProtocol();
                await CheckBluetoothClassicProtocol();
                await CheckWindowsBluetoothSettings();
                await CheckSpecificGamepadDevices();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR GLOBAL: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
            }
            
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Presiona cualquier tecla para salir...");
            Console.ResetColor();
            Console.ReadKey();
        }
        
        private static async Task CheckBluetoothRadios()
        {
            Console.WriteLine("1. VERIFICANDO RADIOS BLUETOOTH:");
            
            try
            {
                IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
                Console.WriteLine($"   Se encontraron {radios.Count} radios");
                
                foreach (Radio radio in radios)
                {
                    Console.WriteLine($"   - Radio: {radio.Name}");
                    Console.WriteLine($"     Tipo: {radio.Kind}");
                    Console.WriteLine($"     Estado: {radio.State}");
                    Console.WriteLine($"     ID: {radio.DeviceId}");
                    
                    if (radio.Kind == RadioKind.Bluetooth)
                    {
                        Console.WriteLine($"     ✓ Este es un radio Bluetooth");
                        
                        if (radio.State == RadioState.On)
                        {
                            Console.WriteLine($"     ✓ Bluetooth está ACTIVADO");
                        }
                        else
                        {
                            Console.WriteLine($"     ✗ Bluetooth está DESACTIVADO o NO DISPONIBLE");
                        }
                    }
                }
                
                if (radios.Count == 0)
                {
                    Console.WriteLine("   ✗ NO se encontraron radios Bluetooth en el sistema");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ ERROR al verificar radios: {ex.Message}");
                Console.WriteLine($"     Tipo: {ex.GetType().Name}");
            }
            
            Console.WriteLine();
        }
        
        private static async Task CheckAllBluetoothDevices()
        {
            Console.WriteLine("2. BUSCANDO TODOS LOS DISPOSITIVOS BLUETOOTH:");
            
            try
            {
                // Filtro para todos los dispositivos asociados
                string aqsFilter = "System.Devices.Aep.IsAssociated:=System.StructuredQueryType.Boolean#True";
                
                string[] requestedProperties = {
                    "System.Devices.Aep.DeviceAddress",
                    "System.Devices.Aep.IsConnected",
                    "System.Devices.Aep.IsPaired",
                    "System.Devices.Aep.ProtocolId",
                    "System.Devices.Aep.CanPair",
                    "System.Devices.Aep.IsPresent",
                    "System.Devices.Aep.Category",
                    "System.ItemNameDisplay"
                };
                
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(aqsFilter, requestedProperties);
                
                Console.WriteLine($"   Se encontraron {devices.Count} dispositivos asociados al sistema");
                
                int bluetoothDevices = 0;
                int gamepadDevices = 0;
                
                foreach (DeviceInformation device in devices)
                {
                    string deviceName = device.Name ?? "Sin nombre";
                    string deviceId = device.Id ?? "Sin ID";
                    
                    // Verificar si es dispositivo Bluetooth
                    bool isBluetooth = deviceId.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                                      deviceName.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase);
                    
                    if (isBluetooth)
                    {
                        bluetoothDevices++;
                        
                        Console.WriteLine($"\n   - Dispositivo Bluetooth [{bluetoothDevices}]: {deviceName}");
                        Console.WriteLine($"     ID: {deviceId}");
                        
                        // Obtener propiedades
                        if (device.Properties.TryGetValue("System.Devices.Aep.IsPaired", out object? pairedObj))
                        {
                            Console.WriteLine($"     Emparejado: {pairedObj}");
                        }
                        
                        if (device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out object? connectedObj))
                        {
                            Console.WriteLine($"     Conectado: {connectedObj}");
                        }
                        
                        if (device.Properties.TryGetValue("System.Devices.Aep.ProtocolId", out object? protocolObj))
                        {
                            Console.WriteLine($"     Protocol ID: {protocolObj}");
                        }
                        
                        if (device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out object? addressObj))
                        {
                            Console.WriteLine($"     Dirección: {addressObj}");
                        }
                        
                        // Verificar si parece un mando
                        bool looksLikeGamepad = deviceName.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
                                               deviceName.Contains("Controller", StringComparison.OrdinalIgnoreCase) ||
                                               deviceName.Contains("Gamepad", StringComparison.OrdinalIgnoreCase) ||
                                               deviceName.Contains("DualShock", StringComparison.OrdinalIgnoreCase) ||
                                               deviceName.Contains("DualSense", StringComparison.OrdinalIgnoreCase);
                        
                        if (looksLikeGamepad)
                        {
                            gamepadDevices++;
                            Console.WriteLine($"     ✓ PARECE SER UN MANDO DE JUEGO");
                        }
                    }
                }
                
                Console.WriteLine($"\n   RESUMEN: {bluetoothDevices} dispositivos Bluetooth, {gamepadDevices} posibles mandos");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ ERROR al buscar dispositivos: {ex.Message}");
                Console.WriteLine($"     Tipo: {ex.GetType().Name}");
            }
            
            Console.WriteLine();
        }
        
        private static async Task CheckBluetoothLEProtocol()
        {
            Console.WriteLine("3. VERIFICANDO DISPOSITIVOS BLUETOOTH LE (BLE):");
            
            try
            {
                string bleProtocolId = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
                string aqsFilter = $"System.Devices.Aep.ProtocolId:=\"{bleProtocolId}\"";
                
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(aqsFilter);
                Console.WriteLine($"   Se encontraron {devices.Count} dispositivos BLE");
                
                foreach (DeviceInformation device in devices)
                {
                    Console.WriteLine($"   - BLE: {device.Name}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ ERROR en BLE: {ex.Message}");
            }
            
            Console.WriteLine();
        }
        
        private static async Task CheckBluetoothClassicProtocol()
        {
            Console.WriteLine("4. VERIFICANDO DISPOSITIVOS BLUETOOTH CLÁSICO:");
            
            try
            {
                string classicProtocolId = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
                string aqsFilter = $"System.Devices.Aep.ProtocolId:=\"{classicProtocolId}\"";
                
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(aqsFilter);
                Console.WriteLine($"   Se encontraron {devices.Count} dispositivos Bluetooth clásico");
                
                foreach (DeviceInformation device in devices)
                {
                    Console.WriteLine($"   - Clásico: {device.Name}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ ERROR en Bluetooth clásico: {ex.Message}");
            }
            
            Console.WriteLine();
        }
        
        private static async Task CheckWindowsBluetoothSettings()
        {
            Console.WriteLine("5. VERIFICANDO CONFIGURACIÓN DE WINDOWS:");
            
            try
            {
                // Intentar acceder a la configuración de Bluetooth
                var deviceSelector = BluetoothDevice.GetDeviceSelector();
                Console.WriteLine($"   Selector de dispositivos Bluetooth: {deviceSelector}");
                
                DeviceInformationCollection pairedDevices = await DeviceInformation.FindAllAsync(
                    BluetoothDevice.GetDeviceSelector(),
                    new[] { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" });
                
                Console.WriteLine($"   Dispositivos Bluetooth emparejados en Windows: {pairedDevices.Count}");
                
                foreach (DeviceInformation device in pairedDevices)
                {
                    Console.WriteLine($"   - {device.Name} (ID: {device.Id})");
                    
                    if (device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out object? connectedObj))
                    {
                        Console.WriteLine($"     Conectado: {connectedObj}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ ERROR al verificar configuración Windows: {ex.Message}");
                Console.WriteLine($"     Tipo: {ex.GetType().Name}");
            }
            
            Console.WriteLine();
        }
        
        private static async Task CheckSpecificGamepadDevices()
        {
            Console.WriteLine("6. BUSCANDO ESPECÍFICAMENTE MANDOS DE XBOX:");
            
            try
            {
                // Buscar dispositivos que contengan "Xbox" en el nombre
                DeviceInformationCollection allDevices = await DeviceInformation.FindAllAsync(DeviceClass.All);
                
                int xboxDevices = 0;
                
                foreach (DeviceInformation device in allDevices)
                {
                    string deviceName = device.Name ?? "";
                    
                    if (deviceName.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
                        deviceName.Contains("XBOX", StringComparison.OrdinalIgnoreCase))
                    {
                        xboxDevices++;
                        
                        Console.WriteLine($"\n   - Dispositivo Xbox encontrado: {deviceName}");
                        Console.WriteLine($"     ID: {device.Id}");
                        Console.WriteLine($"     Emparejado: {device.Pairing.IsPaired}");
                        
                        if (device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out object? connectedObj))
                        {
                            Console.WriteLine($"     Conectado: {connectedObj}");
                        }
                        
                        if (device.Properties.TryGetValue("System.Devices.Aep.ProtocolId", out object? protocolObj))
                        {
                            Console.WriteLine($"     Protocolo: {protocolObj}");
                        }
                    }
                }
                
                Console.WriteLine($"\n   Total de dispositivos Xbox encontrados: {xboxDevices}");
                
                if (xboxDevices == 0)
                {
                    Console.WriteLine("   ✗ NO se encontraron dispositivos Xbox en el sistema");
                    Console.WriteLine("   ℹ️  Asegúrate de:");
                    Console.WriteLine("      1. Tener Bluetooth activado en Windows");
                    Console.WriteLine("      2. Poner el mando Xbox en modo emparejamiento (botón pequeño)");
                    Console.WriteLine("      3. Buscar dispositivos en Configuración de Windows > Bluetooth");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ ERROR al buscar mandos Xbox: {ex.Message}");
            }
            
            Console.WriteLine();
        }
    }
}