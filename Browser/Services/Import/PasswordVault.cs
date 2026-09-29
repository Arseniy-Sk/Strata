using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Browser.Core;

namespace Browser.Services.Import;

public sealed class LoginEntry
{
    public string Host { get; set; } = "";
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Хранилище логинов Strata. Файл шифруется DPAPI (только этот пользователь Windows).
/// Используется для автозаполнения: WebView2 не даёт писать в свой встроенный менеджер паролей,
/// поэтому импортированные пароли живут здесь и подставляются скриптом на подходящих сайтах.
/// </summary>
public sealed class PasswordVault
{
    private const string FileName = "logins.dat";
    private readonly List<LoginEntry> _entries;

    public PasswordVault()
    {
        _entries = Load();
    }

    public int Count => _entries.Count;

    public IReadOnlyList<LoginEntry> Entries => _entries;

    public LoginEntry? ForHost(string host)
    {
        host = host.StartsWith("www.") ? host[4..] : host;
        return _entries.FirstOrDefault(e => Norm(e.Host) == host)
               ?? _entries.FirstOrDefault(e => host.EndsWith("." + Norm(e.Host)) || Norm(e.Host).EndsWith("." + host));
    }

    private static string Norm(string host) => host.StartsWith("www.") ? host[4..] : host;

    public int Merge(IEnumerable<LoginEntry> incoming)
    {
        int added = 0;
        foreach (var entry in incoming)
        {
            if (string.IsNullOrEmpty(entry.Password) || string.IsNullOrEmpty(entry.Username)) continue;
            if (_entries.Any(e => Norm(e.Host) == Norm(entry.Host) && e.Username == entry.Username)) continue;
            _entries.Add(entry);
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public void Remove(LoginEntry entry)
    {
        _entries.Remove(entry);
        Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    private static List<LoginEntry> Load()
    {
        try
        {
            var path = AppPaths.File(FileName);
            if (!File.Exists(path)) return new List<LoginEntry>();
            var protectedBytes = File.ReadAllBytes(path);
            var json = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<LoginEntry>>(json) ?? new List<LoginEntry>();
        }
        catch
        {
            return new List<LoginEntry>();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(_entries);
            var protectedBytes = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AppPaths.File(FileName), protectedBytes);
        }
        catch
        {
            // Не критично.
        }
    }
}
