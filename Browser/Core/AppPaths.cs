using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Browser.Core;

public static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Strata");

    public static readonly string Engine = Path.Combine(Root, "Engine");
    public static readonly string Favicons = Path.Combine(Root, "Favicons");

    public static string File(string name) => Path.Combine(Root, name);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    static AppPaths()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Favicons);
    }

    public static T? ReadJson<T>(string name) where T : class
    {
        try
        {
            var path = File(name);
            if (!System.IO.File.Exists(path)) return null;
            using var stream = System.IO.File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Atomic write: to a temp file first, then swapped in — a failure never corrupts the session.</summary>
    public static void WriteJson<T>(string name, T value)
    {
        try
        {
            var path = File(name);
            var tmp = path + ".tmp";
            using (var stream = System.IO.File.Create(tmp))
                JsonSerializer.Serialize(stream, value, Json);
            System.IO.File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Not critical: the next save attempt will overwrite the file.
        }
    }
}
