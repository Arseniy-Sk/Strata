using System.Text.Json.Serialization;
using Browser.Core;

namespace Browser.Models;

public enum ThemeMode { Dark, Light, System }
public enum AccentKind { Teal, Blue, Violet, Orange }
public enum Density { Compact, Balanced, Airy }
public enum ShieldLevel { Off, Basic, Balanced, Strict }
public enum SearchEngineKind { DuckDuckGo, Google, Yandex, Bing, Brave }
public enum StartupMode { RestoreSession, NewTab }

/// <summary>Settings. Any property change raises Changed — saving and applying are deferred.</summary>
public sealed class BrowserSettings : ObservableObject
{
    private const string FileName = "settings.json";

    // Tabs and spaces
    private bool _tabTree = true;
    private int _freezeAfterMinutes = 2;
    private int _sleepAfterMinutes = 20;
    private int _maxLiveTabs = 12;
    private bool _keepAudioAwake = true;
    private bool _warnDuplicates = true;
    private bool _keepWindowOnLastTab = true;
    private StartupMode _startup = StartupMode.RestoreSession;

    // Privacy
    private ShieldLevel _shield = ShieldLevel.Strict;
    private bool _clearOnExit;
    private bool _savePasswords = true;
    private bool _autofill = true;

    // Appearance
    private ThemeMode _theme = ThemeMode.Dark;
    private AccentKind _accent = AccentKind.Teal;
    private Density _density = Density.Balanced;
    private bool _showStatusBar = true;
    private bool _sidebarOpen = true;
    private double _sidebarWidth = 272;

    // Performance
    private int _memoryBudgetMb = 4096;
    private bool _hardwareAcceleration = true;
    private bool _efficiencyMode = true;

    // Search
    private SearchEngineKind _searchEngine = SearchEngineKind.Google;
    private bool _searchSuggestions = true;

    // Extras
    private string _uiLanguage = "en";
    private bool _translateOffer = true;
    private bool _quickTranslate = true;
    private bool _liquidGlass = true;
    private bool _bookmarksBar = true;
    private bool _videoPopoutButton = true;

    public bool TabTree { get => _tabTree; set => Set(ref _tabTree, value); }
    public int FreezeAfterMinutes { get => _freezeAfterMinutes; set => Set(ref _freezeAfterMinutes, value); }
    public int SleepAfterMinutes { get => _sleepAfterMinutes; set => Set(ref _sleepAfterMinutes, value); }
    public int MaxLiveTabs { get => _maxLiveTabs; set => Set(ref _maxLiveTabs, value); }
    public bool KeepAudioAwake { get => _keepAudioAwake; set => Set(ref _keepAudioAwake, value); }
    public bool WarnDuplicates { get => _warnDuplicates; set => Set(ref _warnDuplicates, value); }
    public bool KeepWindowOnLastTab { get => _keepWindowOnLastTab; set => Set(ref _keepWindowOnLastTab, value); }
    public StartupMode Startup { get => _startup; set => Set(ref _startup, value); }

    public ShieldLevel Shield { get => _shield; set => Set(ref _shield, value); }
    public bool ClearOnExit { get => _clearOnExit; set => Set(ref _clearOnExit, value); }
    public bool SavePasswords { get => _savePasswords; set => Set(ref _savePasswords, value); }
    public bool Autofill { get => _autofill; set => Set(ref _autofill, value); }

    public ThemeMode Theme { get => _theme; set => Set(ref _theme, value); }
    public AccentKind Accent { get => _accent; set => Set(ref _accent, value); }
    public Density Density { get => _density; set => Set(ref _density, value); }
    public bool ShowStatusBar { get => _showStatusBar; set => Set(ref _showStatusBar, value); }
    public bool SidebarOpen { get => _sidebarOpen; set => Set(ref _sidebarOpen, value); }
    public double SidebarWidth { get => _sidebarWidth; set => Set(ref _sidebarWidth, value); }

    public int MemoryBudgetMb { get => _memoryBudgetMb; set => Set(ref _memoryBudgetMb, value); }
    public bool HardwareAcceleration { get => _hardwareAcceleration; set => Set(ref _hardwareAcceleration, value); }
    public bool EfficiencyMode { get => _efficiencyMode; set => Set(ref _efficiencyMode, value); }

    public SearchEngineKind SearchEngine { get => _searchEngine; set => Set(ref _searchEngine, value); }
    public bool SearchSuggestions { get => _searchSuggestions; set => Set(ref _searchSuggestions, value); }

    public string UiLanguage { get => _uiLanguage; set => Set(ref _uiLanguage, value); }
    public bool TranslateOffer { get => _translateOffer; set => Set(ref _translateOffer, value); }
    public bool QuickTranslate { get => _quickTranslate; set => Set(ref _quickTranslate, value); }
    public bool LiquidGlass { get => _liquidGlass; set => Set(ref _liquidGlass, value); }
    public bool BookmarksBar { get => _bookmarksBar; set => Set(ref _bookmarksBar, value); }
    public bool VideoPopoutButton { get => _videoPopoutButton; set => Set(ref _videoPopoutButton, value); }

    [JsonIgnore]
    public string SearchUrlPrefix => SearchEngine switch
    {
        // Google's udm=14 gives clean web results; AI answers (AI Overview) show up in regular search.
        SearchEngineKind.Google => "https://www.google.com/search?q=",
        SearchEngineKind.Yandex => "https://yandex.ru/search/?text=",
        SearchEngineKind.Bing => "https://www.bing.com/search?q=",
        SearchEngineKind.Brave => "https://search.brave.com/search?q=",
        _ => "https://duckduckgo.com/?q="
    };

    [JsonIgnore]
    public string SearchEngineName => SearchEngine switch
    {
        SearchEngineKind.Google => "Google",
        SearchEngineKind.Yandex => "Yandex",
        SearchEngineKind.Bing => "Bing",
        SearchEngineKind.Brave => "Brave Search",
        _ => "DuckDuckGo"
    };

    public int SchemaVersion { get; set; }

    // Window (no notifications — saved on close)
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1320;
    public double WindowHeight { get; set; } = 840;
    public bool WindowMaximized { get; set; }

    public static BrowserSettings Load()
    {
        var s = AppPaths.ReadJson<BrowserSettings>(FileName) ?? new BrowserSettings();
        // Migration: an early build defaulted to DuckDuckGo — switch it to Google (with AI answers).
        if (s.SchemaVersion < 1)
        {
            if (s.SearchEngine == SearchEngineKind.DuckDuckGo) s.SearchEngine = SearchEngineKind.Google;
            s.SchemaVersion = 1;
            s.Save();
        }
        return s;
    }

    public void Save() => AppPaths.WriteJson(FileName, this);
}
