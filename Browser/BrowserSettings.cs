using System.IO;
using System.Text.Json;
using System.Collections.Generic;

namespace Browser
{
    public class BrowserSettings
    {
        private static readonly string SettingsFile = "browser_settings.json";

        public string HomePage { get; set; } = "https://www.bing.com";
        public string SearchEngine { get; set; } = "https://www.bing.com/search?q=";
        public bool EnableJavaScript { get; set; } = true;
        public bool EnableCookies { get; set; } = true;
        public bool EnableImages { get; set; } = true;
        public bool BlockPopups { get; set; } = true;
        public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0";
        public bool HardwareAcceleration { get; set; } = true;
        public bool DoNotTrack { get; set; } = false;
        public int CacheSizeMB { get; set; } = 100;

        // Privacy settings
        public bool ClearHistoryOnExit { get; set; } = false;
        public bool ClearCacheOnExit { get; set; } = false;
        public bool BlockThirdPartyCookies { get; set; } = true;

        // New setting to control script error display
        public bool SuppressScriptErrors { get; set; } = true;

        // Search engine selection
        public string SearchEngineProvider { get; set; } = "bing"; // bing, google, duckduckgo

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch { /* Ignore save errors */ }
        }

        public static BrowserSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    return JsonSerializer.Deserialize<BrowserSettings>(json) ?? new BrowserSettings();
                }
            }
            catch { /* Ignore load errors */ }
            return new BrowserSettings();
        }
    }
}