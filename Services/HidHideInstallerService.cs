using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace GDM.Services;

public sealed class HidHideInstallerService
{
    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/nefarius/HidHide/releases/latest";
    private const string OfficialPageUrl = "https://github.com/nefarius/HidHide";
    private const string ExpectedSignerName = "Nefarius";
    private const int CopyBufferSize = 81920;

    public string OfficialPage => OfficialPageUrl;

    public async Task<HidHideReleaseInfo> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using HttpClient http = CreateHttpClient();
        string json = await http.GetStringAsync(LatestReleaseApiUrl, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string tagName = root.TryGetProperty("tag_name", out JsonElement tag)
            ? tag.GetString() ?? string.Empty
            : string.Empty;
        string name = root.TryGetProperty("name", out JsonElement releaseName)
            ? releaseName.GetString() ?? tagName
            : tagName;

        if (!root.TryGetProperty("assets", out JsonElement assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "La última versión de HidHide no publica ningún instalador para Windows.");
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string assetName = asset.TryGetProperty("name", out JsonElement assetNameElement)
                ? assetNameElement.GetString() ?? string.Empty
                : string.Empty;
            bool isInstaller = assetName.EndsWith("_x64.exe", StringComparison.OrdinalIgnoreCase) ||
                (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                 assetName.Contains("x64", StringComparison.OrdinalIgnoreCase));
            if (!isInstaller)
            {
                continue;
            }

            string url = asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                ? urlElement.GetString() ?? string.Empty
                : string.Empty;
            long size = asset.TryGetProperty("size", out JsonElement sizeElement) &&
                        sizeElement.ValueKind == JsonValueKind.Number
                ? sizeElement.GetInt64()
                : 0;
            if (!string.IsNullOrEmpty(url))
            {
                return new HidHideReleaseInfo(tagName, name, assetName, url, size);
            }
        }

        throw new InvalidOperationException(
            "No se encontró el instalador de HidHide en la última versión publicada.");
    }

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Ruta de descarga no válida.");
        Directory.CreateDirectory(directory);
        string partialPath = destinationPath + ".part";
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        using HttpClient http = CreateHttpClient();
        using HttpResponseMessage response = await http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? 0;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            await using (var target = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                useAsync: true))
            {
                byte[] buffer = new byte[CopyBufferSize];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (totalBytes > 0)
                    {
                        progress?.Report((double)received / totalBytes);
                    }
                }

                await target.FlushAsync(cancellationToken);
            }
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }

        File.Move(partialPath, destinationPath, overwrite: true);
        progress?.Report(1.0);
    }

    public static string VerifyInstallerSignature(string path)
    {
        VerifyAuthenticode(path);

        X509Certificate signer;
        try
        {
            signer = X509Certificate.CreateFromSignedFile(path);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException(
                "El instalador descargado no tiene una firma digital válida; se canceló la instalación.",
                exception);
        }

        using (signer)
        using (var certificate = new X509Certificate2(signer))
        {
            string publisher = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (!publisher.Contains(ExpectedSignerName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"El instalador está firmado por «{publisher}», no por el desarrollador esperado de HidHide; se canceló la instalación.");
            }

            return publisher;
        }
    }

    private static void VerifyAuthenticode(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructureSize = Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path
        };
        IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
        var data = new WinTrustData
        {
            StructureSize = Marshal.SizeOf<WinTrustData>(),
            UiChoice = WtdUiNone,
            RevocationChecks = WtdRevokeNone,
            UnionChoice = WtdChoiceFile,
            File = fileInfoPtr,
            StateAction = WtdStateActionVerify
        };

        int dataSize = Marshal.SizeOf<WinTrustData>();
        IntPtr dataPtr = Marshal.AllocHGlobal(dataSize);
        int result;
        try
        {
            Marshal.StructureToPtr(data, dataPtr, false);
            result = NativeMethods.WinVerifyTrust(IntPtr.Zero, WinTrustGenericVerifyV2ActionId, dataPtr);

            WinTrustData updated = Marshal.PtrToStructure<WinTrustData>(dataPtr)!;
            updated.StateAction = WtdStateActionClose;
            Marshal.StructureToPtr(updated, dataPtr, false);
            _ = NativeMethods.WinVerifyTrust(IntPtr.Zero, WinTrustGenericVerifyV2ActionId, dataPtr);
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }

        if (result != 0)
        {
            throw new InvalidOperationException(
                $"La firma digital del instalador de HidHide no es válida (WinVerifyTrust 0x{result:X8}); se canceló la instalación.");
        }
    }

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    private static readonly Guid WinTrustGenericVerifyV2ActionId =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public int StructureSize;
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public int StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    private static class NativeMethods
    {
        [DllImport("wintrust.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern int WinVerifyTrust(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            IntPtr data);
    }

    public static Process StartInstaller(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        };
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows no pudo iniciar el instalador de HidHide.");
    }

    internal static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GDM-HidHide-Installer");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}

public sealed record HidHideReleaseInfo(
    string TagName,
    string ReleaseName,
    string AssetName,
    string DownloadUrl,
    long SizeBytes);
