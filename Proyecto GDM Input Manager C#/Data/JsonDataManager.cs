using System;
using System.IO;
using System.Text.Json;

namespace GDMInputManager.Data;

/// <summary>
/// Gestor de datos usando archivos JSON
/// Estructura: Clase que proporciona operaciones de lectura/escritura de archivos JSON
/// Propósito: Centralizar todas las operaciones de persistencia de datos en formato JSON
/// Estrategia de almacenamiento:
///   - Directorio "Data" en la carpeta del ejecutable
///   - Cada archivo es un JSON con formato legible (indentado)
/// Ventajas:
///   - Fácil de leer/editar manualmente (formato JSON)
///   - Sin dependencias de base de datos externas
///   - Portátil (los datos viajan con el ejecutable)
/// </summary>
public class JsonDataManager
{
    // Ruta del directorio donde se guardan todos los archivos JSON
    private readonly string _dataDirectory;

    /// <summary>
    /// Constructor del gestor de datos
    /// Estructura: Crea el directorio "Data" junto al ejecutable si no existe
    /// Propósito: Preparar el directorio de almacenamiento para uso inmediato
    /// Proceso:
    ///   1. Obtiene la ruta base donde está el ejecutable
    ///   2. Combina con "Data" para crear la ruta del directorio de datos
    ///   3. Si el directorio no existe, lo crea
    /// Nota: BaseDirectory apunta a la carpeta donde está el .exe (bin/Debug/net10.0-windows/)
    /// </summary>
    public JsonDataManager()
    {
        // Crear directorio de datos en la carpeta del ejecutable
        _dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
        if (!Directory.Exists(_dataDirectory))
        {
            Directory.CreateDirectory(_dataDirectory);
        }
    }

    /// <summary>
    /// Guarda un objeto en un archivo JSON
    /// Estructura: Serializa el objeto a JSON y lo escribe en un archivo
    /// Propósito: Persistir cualquier objeto C# en formato JSON
    /// Proceso:
    ///   1. Combina el nombre del archivo con el directorio de datos
    ///   2. Serializa el objeto a JSON con formato indentado (legible)
    ///   3. Escribe el JSON en el archivo (sobrescribe si existe)
    /// Parámetros:
    ///   - fileName: Nombre del archivo (ej: "config.json", "mando_123.json")
    ///   - data: Objeto C# a serializar (puede ser cualquier tipo)
    /// Nota: WriteIndented = true hace que el JSON sea legible para humanos
    /// </summary>
    public void SaveToFile<T>(string fileName, T data)
    {
        string filePath = Path.Combine(_dataDirectory, fileName);
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Lee un objeto desde un archivo JSON
    /// Estructura: Lee el archivo JSON y lo deserializa a un objeto C#
    /// Propósito: Recuperar objetos persistidos desde archivos JSON
    /// Proceso:
    ///   1. Combina el nombre del archivo con el directorio de datos
    ///   2. Si el archivo no existe, retorna el valor por defecto
    ///   3. Lee el contenido del archivo JSON
    ///   4. Deserializa el JSON al tipo especificado
    ///   5. Si la deserialización falla, retorna el valor por defecto
    /// Parámetros:
    ///   - fileName: Nombre del archivo a leer
    ///   - defaultValue: Valor a retornar si el archivo no existe o hay error
    /// Retorna: Objeto deserializado o el valor por defecto
    /// </summary>
    public T LoadFromFile<T>(string fileName, T defaultValue = default!)
    {
        string filePath = Path.Combine(_dataDirectory, fileName);

        if (!File.Exists(filePath))
        {
            return defaultValue;
        }

        string json = File.ReadAllText(filePath);
        var result = JsonSerializer.Deserialize<T>(json);
        return result ?? defaultValue;
    }

    /// <summary>
    /// Verifica si existe un archivo de datos
    /// Estructura: Comprueba si el archivo existe en el directorio de datos
    /// Propósito: Permitir verificar si un archivo existe antes de intentar leerlo
    /// Proceso:
    ///   1. Combina el nombre del archivo con el directorio de datos
    ///   2. Usa File.Exists para verificar la existencia
    /// Parámetros:
    ///   - fileName: Nombre del archivo a verificar
    /// Retorna: true si el archivo existe, false en caso contrario
    /// </summary>
    public bool FileExists(string fileName)
    {
        string filePath = Path.Combine(_dataDirectory, fileName);
        return File.Exists(filePath);
    }

    /// <summary>
    /// Elimina un archivo de datos
    /// Estructura: Elimina el archivo especificado del directorio de datos
    /// Propósito: Permitir borrar archivos de datos (ej: al eliminar un mando)
    /// Proceso:
    ///   1. Combina el nombre del archivo con el directorio de datos
    ///   2. Verifica si el archivo existe antes de intentar eliminarlo
    ///   3. Si existe, lo elimina con File.Delete
    /// Parámetros:
    ///   - fileName: Nombre del archivo a eliminar
    /// Nota: Es seguro llamar este método incluso si el archivo no existe
    /// </summary>
    public void DeleteFile(string fileName)
    {
        string filePath = Path.Combine(_dataDirectory, fileName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    /// <summary>
    /// Obtiene la ruta completa del directorio de datos
    /// Estructura: Propiedad de solo lectura que retorna la ruta del directorio
    /// Propósito: Permitir conocer dónde se están guardando los datos
    /// Uso típico: Para mostrar al usuario, para logs, para operaciones de archivo
    /// Retorna: Ruta completa del directorio "Data"
    /// </summary>
    public string GetDataDirectory()
    {
        return _dataDirectory;
    }
}
