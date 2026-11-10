using System.IO;
using System.Text.Json;

namespace GDM.Services.Persistence;

internal sealed class JsonFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<T> ReadAsync<T>(string filePath, CancellationToken cancellationToken)
    {
        byte[] content = await File.ReadAllBytesAsync(filePath, cancellationToken);

        try
        {
            return JsonSerializer.Deserialize<T>(content, SerializerOptions)
                ?? throw new InvalidDataException($"El archivo JSON '{filePath}' no contiene un objeto válido.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"El archivo JSON '{filePath}' está dañado.", exception);
        }
    }

    public async Task WriteAtomicallyAsync<T>(
        string filePath,
        T value,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (directory is null)
        {
            throw new InvalidOperationException($"La ruta '{filePath}' no contiene una carpeta.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
