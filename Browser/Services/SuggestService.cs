using System.Net.Http;
using System.Text.Json;

namespace Browser.Services;

/// <summary>Живые поисковые подсказки Google (запрос идёт из C#, без CORS-ограничений).</summary>
public static class SuggestService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public static async Task<List<string>> QueryAsync(string text, CancellationToken ct)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return result;
        try
        {
            var url = "https://suggestqueries.google.com/complete/search?client=firefox&hl=ru&q=" + Uri.EscapeDataString(text);
            var json = await Http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement[1].EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s!);
            }
        }
        catch
        {
            // Нет сети / таймаут — просто без подсказок.
        }
        return result;
    }
}
