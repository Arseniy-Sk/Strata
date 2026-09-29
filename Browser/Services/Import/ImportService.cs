using System.IO;
using System.Text.Json;
using Browser.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;

namespace Browser.Services.Import;

public enum BrowserFamily { Chromium, Firefox }

public sealed class BrowserProfile
{
    public required string Name { get; init; }
    public required BrowserFamily Family { get; init; }
    public required string Directory { get; init; }   // папка профиля (Default / *.default-release)
    public string UserDataDir { get; init; } = "";    // для Chromium — родитель с Local State
    public string Glyph { get; init; } = "";
    public int Hue { get; init; } = 220;
}

public sealed class ImportOptions
{
    public bool Bookmarks { get; set; } = true;
    public bool Cookies { get; set; } = true;
    public bool History { get; set; } = true;
    public bool Passwords { get; set; } = true;
}

public sealed class ImportResult
{
    public int Bookmarks, Cookies, History, Passwords;
    public List<string> Notes { get; } = new();
    public override string ToString()
    {
        var parts = new List<string>();
        if (Bookmarks > 0) parts.Add($"{Bookmarks} закладок");
        if (Cookies > 0) parts.Add($"{Cookies} cookie");
        if (Passwords > 0) parts.Add($"{Passwords} паролей");
        if (History > 0) parts.Add($"{History} записей истории");
        return parts.Count == 0 ? "нечего импортировать" : string.Join(", ", parts);
    }
}

public readonly record struct ImportedCookie(string Host, string Name, string Value, string Path, bool Secure, bool HttpOnly, long ExpiresUtc, int SameSite, bool IsSession);

/// <summary>Импорт закладок, cookie, истории и паролей из Chrome, Edge, Yandex, Brave и Firefox.</summary>
public static class ImportService
{
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static List<BrowserProfile> Detect()
    {
        var list = new List<BrowserProfile>();
        void Chromium(string name, string userData, string glyph, int hue)
        {
            var def = Path.Combine(userData, "Default");
            if (Directory.Exists(def))
                list.Add(new BrowserProfile { Name = name, Family = BrowserFamily.Chromium, Directory = def, UserDataDir = userData, Glyph = glyph, Hue = hue });
        }

        Chromium("Google Chrome", Path.Combine(Local, "Google", "Chrome", "User Data"), "C", 210);
        Chromium("Microsoft Edge", Path.Combine(Local, "Microsoft", "Edge", "User Data"), "E", 196);
        Chromium("Yandex Browser", Path.Combine(Local, "Yandex", "YandexBrowser", "User Data"), "Я", 12);
        Chromium("Brave", Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"), "B", 32);
        Chromium("Opera", Path.Combine(Roaming, "Opera Software", "Opera Stable"), "O", 350);
        Chromium("Vivaldi", Path.Combine(Local, "Vivaldi", "User Data"), "V", 350);

        // Firefox: профиль по умолчанию из profiles.ini.
        var ffRoot = Path.Combine(Roaming, "Mozilla", "Firefox");
        var profilesIni = Path.Combine(ffRoot, "profiles.ini");
        if (File.Exists(profilesIni))
        {
            var dir = FirefoxDefaultProfile(profilesIni, ffRoot);
            if (dir != null)
                list.Add(new BrowserProfile { Name = "Mozilla Firefox", Family = BrowserFamily.Firefox, Directory = dir, Glyph = "F", Hue = 30 });
        }
        return list;
    }

    private static string? FirefoxDefaultProfile(string iniPath, string root)
    {
        string? fallback = null;
        foreach (var raw in File.ReadAllLines(iniPath))
        {
            var line = raw.Trim();
            if (line.StartsWith("Default=") && line.Contains('/') || line.StartsWith("Default=") && line.Contains('\\'))
            {
                var rel = line["Default=".Length..].Trim();
                var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(full)) return full;
            }
            if (line.StartsWith("Path="))
            {
                var rel = line["Path=".Length..].Trim();
                var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(full)) fallback ??= full;
            }
        }
        return fallback;
    }

    public static async Task<ImportResult> ImportAsync(BrowserProfile profile, ImportOptions options,
        BookmarkService bookmarks, HistoryService history, PasswordVault vault, CoreWebView2? cookieHost)
    {
        var result = new ImportResult();
        await Task.Run(() =>
        {
            try
            {
                if (options.Bookmarks)
                {
                    var items = profile.Family == BrowserFamily.Chromium ? ChromiumBookmarks(profile) : FirefoxBookmarks(profile);
                    result.Bookmarks = App.Dispatch(() => bookmarks.Merge(items));
                }
                if (options.History)
                {
                    var items = profile.Family == BrowserFamily.Chromium ? ChromiumHistory(profile) : FirefoxHistory(profile);
                    App.Dispatch(() => { foreach (var (url, title, when) in items) history.AddImported(url, title, when); history.FinishImport(); });
                    result.History = items.Count;
                }
                if (options.Passwords)
                {
                    if (profile.Family == BrowserFamily.Chromium)
                    {
                        var logins = ChromiumPasswords(profile);
                        result.Passwords = App.Dispatch(() => vault.Merge(logins));
                    }
                    else result.Notes.Add("Пароли Firefox защищены NSS и не импортируются.");
                }
            }
            catch (Exception ex)
            {
                result.Notes.Add(ex.Message);
            }
        });

        // Cookie добавляются на UI-потоке через движок.
        if (options.Cookies && cookieHost != null)
        {
            try
            {
                var cookies = await Task.Run(() => profile.Family == BrowserFamily.Chromium ? ChromiumCookies(profile) : FirefoxCookies(profile));
                result.Cookies = InjectCookies(cookieHost, cookies);
            }
            catch (Exception ex)
            {
                result.Notes.Add("Cookie: " + ex.Message);
            }
        }
        else if (options.Cookies)
        {
            result.Notes.Add("Cookie импортируются после открытия сайта (движок не запущен).");
        }

        return result;
    }

    // ---------------------------------------------------------------- Chromium

    private static IEnumerable<(string Url, string Title)> ChromiumBookmarks(BrowserProfile p)
    {
        var path = Path.Combine(p.Directory, "Bookmarks");
        var result = new List<(string, string)>();
        if (!File.Exists(path)) return result;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("roots", out var roots)) return result;
        void Walk(JsonElement node)
        {
            if (node.TryGetProperty("type", out var type))
            {
                if (type.GetString() == "url" && node.TryGetProperty("url", out var url))
                    result.Add((url.GetString() ?? "", node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
                else if (node.TryGetProperty("children", out var children))
                    foreach (var child in children.EnumerateArray()) Walk(child);
            }
        }
        foreach (var root in roots.EnumerateObject()) if (root.Value.ValueKind == JsonValueKind.Object) Walk(root.Value);
        return result.Where(x => x.Item1.StartsWith("http")).ToList();
    }

    private static List<(string Url, string Title, DateTime When)> ChromiumHistory(BrowserProfile p)
    {
        var list = new List<(string, string, DateTime)>();
        using var conn = OpenCopy(Path.Combine(p.Directory, "History"));
        if (conn == null) return list;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT url, title, last_visit_time FROM urls WHERE url LIKE 'http%' ORDER BY last_visit_time DESC LIMIT 3000";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), ChromeTime(r.GetInt64(2))));
        return list;
    }

    private static List<ImportedCookie> ChromiumCookies(BrowserProfile p)
    {
        var list = new List<ImportedCookie>();
        var dbPath = Path.Combine(p.Directory, "Network", "Cookies");
        if (!File.Exists(dbPath)) dbPath = Path.Combine(p.Directory, "Cookies");
        using var conn = OpenCopy(dbPath);
        if (conn == null) return list;
        var crypto = new ChromiumCrypto(p.UserDataDir);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT host_key, name, encrypted_value, path, is_secure, is_httponly, expires_utc, samesite FROM cookies";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var enc = (byte[])r["encrypted_value"];
            var value = crypto.Decrypt(enc);
            if (value.Length == 0) continue;
            long expires = r.GetInt64(6);
            list.Add(new ImportedCookie(r.GetString(0), r.GetString(1), value, r.GetString(3),
                r.GetInt64(4) != 0, r.GetInt64(5) != 0, expires, (int)r.GetInt64(7), expires == 0));
        }
        return list;
    }

    private static List<LoginEntry> ChromiumPasswords(BrowserProfile p)
    {
        var list = new List<LoginEntry>();
        using var conn = OpenCopy(Path.Combine(p.Directory, "Login Data"));
        if (conn == null) return list;
        var crypto = new ChromiumCrypto(p.UserDataDir);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT origin_url, username_value, password_value FROM logins";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var password = crypto.Decrypt((byte[])r["password_value"]);
            if (password.Length == 0) continue;
            var url = r.GetString(0);
            list.Add(new LoginEntry { Url = url, Host = Core.UrlHelper.DisplayHost(url), Username = r.IsDBNull(1) ? "" : r.GetString(1), Password = password });
        }
        return list;
    }

    // ---------------------------------------------------------------- Firefox

    private static IEnumerable<(string Url, string Title)> FirefoxBookmarks(BrowserProfile p)
    {
        var list = new List<(string, string)>();
        using var conn = OpenCopy(Path.Combine(p.Directory, "places.sqlite"));
        if (conn == null) return list;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT p.url, b.title FROM moz_bookmarks b JOIN moz_places p ON b.fk = p.id WHERE b.type = 1 AND p.url LIKE 'http%'";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        return list;
    }

    private static List<(string Url, string Title, DateTime When)> FirefoxHistory(BrowserProfile p)
    {
        var list = new List<(string, string, DateTime)>();
        using var conn = OpenCopy(Path.Combine(p.Directory, "places.sqlite"));
        if (conn == null) return list;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT url, title, last_visit_date FROM moz_places WHERE url LIKE 'http%' AND last_visit_date IS NOT NULL ORDER BY last_visit_date DESC LIMIT 3000";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), UnixMicros(r.GetInt64(2))));
        return list;
    }

    private static List<ImportedCookie> FirefoxCookies(BrowserProfile p)
    {
        var list = new List<ImportedCookie>();
        using var conn = OpenCopy(Path.Combine(p.Directory, "cookies.sqlite"));
        if (conn == null) return list;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT host, name, value, path, isSecure, isHttpOnly, expiry, sameSite FROM moz_cookies";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long expirySec = r.IsDBNull(6) ? 0 : r.GetInt64(6);
            long chromeUtc = expirySec == 0 ? 0 : (expirySec + 11644473600L) * 1_000_000L;
            list.Add(new ImportedCookie(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt64(4) != 0, r.GetInt64(5) != 0, chromeUtc, (int)(r.IsDBNull(7) ? 0 : r.GetInt64(7)), expirySec == 0));
        }
        return list;
    }

    // ---------------------------------------------------------------- cookie -> движок

    private static int InjectCookies(CoreWebView2 core, List<ImportedCookie> cookies)
    {
        int added = 0;
        var manager = core.CookieManager;
        foreach (var c in cookies)
        {
            try
            {
                var cookie = manager.CreateCookie(c.Name, c.Value, c.Host, string.IsNullOrEmpty(c.Path) ? "/" : c.Path);
                cookie.IsSecure = c.Secure;
                cookie.IsHttpOnly = c.HttpOnly;
                cookie.SameSite = c.SameSite switch
                {
                    0 => CoreWebView2CookieSameSiteKind.None,
                    1 => CoreWebView2CookieSameSiteKind.Lax,
                    _ => CoreWebView2CookieSameSiteKind.Strict
                };
                if (!c.IsSession && c.ExpiresUtc > 0)
                {
                    long unix = c.ExpiresUtc / 1_000_000L - 11644473600L; // микросекунды-с-1601 → секунды UNIX
                    if (unix > 0 && unix < 253402300800L)
                        cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                }
                manager.AddOrUpdateCookie(cookie);
                added++;
            }
            catch
            {
                // Некорректный cookie пропускаем.
            }
        }
        return added;
    }

    // ---------------------------------------------------------------- утилиты

    private static SqliteConnection? OpenCopy(string dbPath)
    {
        if (!File.Exists(dbPath)) return null;
        // Файл заблокирован работающим браузером — копируем в temp вместе с WAL.
        var tmp = Path.Combine(Path.GetTempPath(), "strata-imp-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            File.Copy(dbPath, tmp, true);
            foreach (var ext in new[] { "-wal", "-shm" })
                if (File.Exists(dbPath + ext)) File.Copy(dbPath + ext, tmp + ext, true);
            var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Cache=Private");
            conn.Open();
            return conn;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            return null;
        }
    }

    private static DateTime ChromeTime(long micros)
    {
        if (micros <= 0) return DateTime.UtcNow;
        try { return new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(micros * 10); }
        catch { return DateTime.UtcNow; }
    }

    private static DateTime UnixMicros(long micros)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000).UtcDateTime; }
        catch { return DateTime.UtcNow; }
    }
}
