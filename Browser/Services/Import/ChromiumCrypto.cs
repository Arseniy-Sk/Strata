using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Browser.Services.Import;

/// <summary>
/// Расшифровка секретов Chromium-браузеров (Chrome, Edge, Yandex, Brave).
/// Ключ лежит в «Local State» (base64, защищён DPAPI), значения — AES-256-GCM (префикс v10/v11)
/// или напрямую DPAPI у старых версий. App-bound шифрование v20 (Chrome 127+) расшифровать нельзя.
/// </summary>
public sealed class ChromiumCrypto
{
    private readonly byte[]? _key;

    public bool HasKey => _key != null;

    public ChromiumCrypto(string userDataDir)
    {
        _key = LoadKey(Path.Combine(userDataDir, "Local State"));
    }

    private static byte[]? LoadKey(string localStatePath)
    {
        try
        {
            if (!File.Exists(localStatePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)) return null;
            if (!osCrypt.TryGetProperty("encrypted_key", out var encKey)) return null;
            var blob = Convert.FromBase64String(encKey.GetString()!);
            // Префикс "DPAPI" (5 байт) снимается перед расшифровкой.
            if (blob.Length > 5 && Encoding.ASCII.GetString(blob, 0, 5) == "DPAPI")
                return ProtectedData.Unprotect(blob[5..], null, DataProtectionScope.CurrentUser);
            return ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }

    public string Decrypt(byte[] data)
    {
        if (data.Length == 0) return "";
        try
        {
            // v10/v11 — AES-256-GCM.
            if (data.Length > 3 && (data[0] == 'v') && (data[1] == '1') && (data[2] == '0' || data[2] == '1'))
            {
                if (_key == null) return "";
                var nonce = data[3..15];
                var tag = data[^16..];
                var cipher = data[15..^16];
                var plain = new byte[cipher.Length];
                using var gcm = new AesGcm(_key, 16);
                gcm.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
            // v20 — app-bound, недоступно из стороннего процесса.
            if (data.Length > 3 && data[0] == 'v' && data[1] == '2' && data[2] == '0') return "";
            // Старый формат — прямой DPAPI.
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }
}
