using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Browser.Core;
using Browser.Models;
using Browser.Services;
using Browser.Services.Import;

namespace Browser.Views.Pages;

public partial class SettingsPage : UserControl, IPage
{
    private readonly TabManager _tabs;
    private readonly BrowserSettings _s;
    private readonly List<(string Title, Border Button)> _navButtons = new();
    private string _section = "tabs";

    private static readonly (string Id, string Title)[] Sections =
    {
        ("tabs", "Tabs and spaces"),
        ("privacy", "Privacy and shield"),
        ("appearance", "Appearance"),
        ("keys", "Keys and gestures"),
        ("perf", "Performance"),
        ("search", "Search and translation"),
        ("import", "Import from browsers"),
        ("system", "System")
    };

    public SettingsPage(TabManager tabs)
    {
        _tabs = tabs;
        _s = tabs.Settings;
        InitializeComponent();

        foreach (var (id, title) in Sections)
        {
            var text = new TextBlock { Text = title, FontSize = 12.5 };
            var button = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, Child = text, Background = Brushes.Transparent };
            var sectionId = id;
            button.MouseLeftButtonUp += (_, _) => Show(sectionId);
            button.MouseEnter += (_, _) => { if (_section != sectionId) button.SetResourceReference(Border.BackgroundProperty, "Hover"); };
            button.MouseLeave += (_, _) => { if (_section != sectionId) button.Background = Brushes.Transparent; };
            Nav.Items.Add(button);
            _navButtons.Add((id, button));
        }
        Show("tabs");
    }

    public void OnShown(BrowserTab tab)
    {
        if (_section is "perf" or "system") Show(_section);
    }

    private void Show(string id)
    {
        _section = id;
        foreach (var (navId, button) in _navButtons)
        {
            var text = (TextBlock)button.Child;
            if (navId == id)
            {
                button.SetResourceReference(Border.BackgroundProperty, "Hover2");
                text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            }
            else
            {
                button.Background = Brushes.Transparent;
                text.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            }
        }

        Body.Children.Clear();
        Scroller.ScrollToTop();
        switch (id)
        {
            case "tabs": BuildTabs(); break;
            case "privacy": BuildPrivacy(); break;
            case "appearance": BuildAppearance(); break;
            case "keys": BuildKeys(); break;
            case "perf": BuildPerformance(); break;
            case "search": BuildSearch(); break;
            case "import": BuildImport(); break;
            case "system": BuildSystem(); break;
        }
    }

    // ------------------------------------------------------------------ sections

    private void BuildTabs()
    {
        Header("Tabs and spaces", "The tree, sleep, and the rules tabs live and sleep by. A sleeping tab doesn't use memory, but stays in the tree with its icon and scroll position.");
        Toggle("Tab tree", "Pages opened from a tab become its children", () => _s.TabTree, v => _s.TabTree = v);
        Choice("Freeze background tabs after", "The tab's process is paused: zero CPU, memory shrinks",
            new (string, object)[] { ("1 min", 1), ("2 min", 2), ("5 min", 5), ("10 min", 10), ("never", 0) },
            () => _s.FreezeAfterMinutes, v => _s.FreezeAfterMinutes = (int)v);
        Choice("Sleep after", "An inactive tab is fully unloaded from memory",
            new (string, object)[] { ("5 min", 5), ("10 min", 10), ("20 min", 20), ("30 min", 30), ("1 hour", 60), ("3 hours", 180), ("never", 0) },
            () => _s.SleepAfterMinutes, v => _s.SleepAfterMinutes = (int)v);
        Choice("Max tabs in memory", "The oldest ones sleep once the limit is exceeded — handy with hundreds of tabs",
            new (string, object)[] { ("4", 4), ("8", 8), ("12", 12), ("20", 20), ("40", 40), ("no limit", 0) },
            () => _s.MaxLiveTabs, v => _s.MaxLiveTabs = (int)v);
        Toggle("Don't sleep tabs with sound", "Music and calls keep playing in the background", () => _s.KeepAudioAwake, v => _s.KeepAudioAwake = v);
        Toggle("Warn about duplicates", "Show the already-open tab instead of a new one", () => _s.WarnDuplicates, v => _s.WarnDuplicates = v);
        Toggle("Closing the last tab", "Open the start page instead of closing the window", () => _s.KeepWindowOnLastTab, v => _s.KeepWindowOnLastTab = v);
        Choice("On startup", "Restored tabs sleep and only wake up when opened",
            new (string, object)[] { ("restore session", StartupMode.RestoreSession), ("new tab", StartupMode.NewTab) },
            () => _s.Startup, v => _s.Startup = (StartupMode)v);
    }

    private void BuildPrivacy()
    {
        Header("Privacy and shield", "The shield works at the network level: rules apply before the document loads. Chromium's built-in tracking protection complements Strata's own list. Empty ad slots on sites are additionally hidden, and ads in the YouTube player are skipped automatically.");
        Choice("Tracker and ad blocking", "Strict — maximum protection; basic — if a site breaks",
            new (string, object)[] { ("strict", ShieldLevel.Strict), ("balanced", ShieldLevel.Balanced), ("basic", ShieldLevel.Basic), ("off", ShieldLevel.Off) },
            () => _s.Shield, v => _s.Shield = (ShieldLevel)v);
        Toggle("Save passwords", "Offer to save a password after signing in", () => _s.SavePasswords, v => _s.SavePasswords = v);
        Toggle("Autofill forms", "Addresses and contacts in input fields", () => _s.Autofill, v => _s.Autofill = v);
        Toggle("Clear on exit", "Cookies, cache, and site storage are removed on close", () => _s.ClearOnExit, v => _s.ClearOnExit = v);
        ActionRow("Clear site data now", "Cookies, cache, localStorage, IndexedDB, service workers", "Clear", async () =>
        {
            if (_tabs.AnyProfile == null) { Note("Open any site — data is cleared through the engine"); return; }
            await _tabs.ClearBrowsingDataAsync();
            Note("Site data cleared");
        });
        ActionRow("Browsing history", $"{_tabs.History.Entries.Count} entries are stored only on this computer", "Clear", () =>
        {
            _tabs.History.Clear();
            Note("History cleared");
            Show("privacy");
        });
        Stat($"Blocked this session: {_tabs.TotalBlocked}");
    }

    private void BuildAppearance()
    {
        Header("Appearance", "Theme, accent, and tab list density. Changes apply immediately.");
        Choice("Theme", "Dark, light, or follow the system", new (string, object)[] { ("dark", ThemeMode.Dark), ("light", ThemeMode.Light), ("system", ThemeMode.System) },
            () => _s.Theme, v => _s.Theme = (ThemeMode)v);

        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (kind, name) in new[] { (AccentKind.Teal, "Strata Teal"), (AccentKind.Blue, "Indigo"), (AccentKind.Violet, "Lavender"), (AccentKind.Orange, "Coral") })
        {
            var color = ThemeManager.AccentColor(kind);
            var chip = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Margin = new Thickness(6, 0, 0, 0),
                Background = new SolidColorBrush(color), Cursor = Cursors.Hand, ToolTip = name,
                BorderThickness = new Thickness(2)
            };
            if (_s.Accent == kind) chip.SetResourceReference(Border.BorderBrushProperty, "Text");
            else chip.BorderBrush = Brushes.Transparent;
            var k = kind;
            chip.MouseLeftButtonUp += (_, _) => { _s.Accent = k; _s.Save(); Show("appearance"); };
            swatches.Children.Add(chip);
        }
        Row("Accent color", "Active tab, shield, and palette", swatches);

        Choice("Tab list density", "Row height in the tree", new (string, object)[] { ("compact", Density.Compact), ("balanced", Density.Balanced), ("airy", Density.Airy) },
            () => _s.Density, v => _s.Density = (Density)v);
        Toggle("Liquid glass", "Frosted Mica material behind panels (Windows 11)", () => _s.LiquidGlass, v => _s.LiquidGlass = v);
        Toggle("Bookmarks bar", "A bookmarks row under the address bar", () => _s.BookmarksBar, v => _s.BookmarksBar = v);
        Toggle("Pop-out button on video", "Appears on hover over a video — like in Firefox and Yandex Browser", () => _s.VideoPopoutButton, v => _s.VideoPopoutButton = v);
        Toggle("Status bar", "Tab counts, memory, and the link address under the cursor", () => _s.ShowStatusBar, v => _s.ShowStatusBar = v);
        Toggle("Tab sidebar", "Ctrl B — hide it for full-width content", () => _s.SidebarOpen, v => _s.SidebarOpen = v);
        Stat("Design fonts: Space Grotesk and IBM Plex Mono. If they aren't installed, Segoe UI Variable and Cascadia Mono are used instead.");
    }

    private void BuildKeys()
    {
        Header("Keys and gestures", "The palette covers most actions; everything else follows familiar chords.");
        foreach (var (label, hint, keys) in new[]
                 {
                     ("Command palette", "Search tabs, history, and commands. Prefixes: # tabs, @ history, > commands", "Ctrl K"),
                     ("Address bar", "Type an address or search query", "Ctrl L"),
                     ("New / close tab", "Closing switches to the neighboring one in the tree", "Ctrl T · Ctrl W"),
                     ("Reopen closed tab", "Stack of recently closed tabs", "Ctrl Shift T"),
                     ("Next / previous", "Through the current space's visible tree", "Ctrl Tab"),
                     ("Tab by number", "1–8, 9 — last one", "Ctrl 1…9"),
                     ("Switch space", "Cycles through the list of spaces", "Ctrl Alt ← →"),
                     ("Split screen", "Active tab + the next one", "Ctrl \\"),
                     ("Collapse sidebar", "Full-width content", "Ctrl B"),
                     ("Reader mode", "Strip the page down to its content", "Ctrl Alt R"),
                     ("Bookmark", "Star in the address bar", "Ctrl D"),
                     ("Pin tab", "Pinned tabs are visible in every space", "Ctrl Shift P"),
                     ("Sleep background tabs", "Free up memory instantly", "Ctrl Shift S"),
                     ("Find on page", "Built-in search panel", "Ctrl F"),
                     ("Translate page", "Translate all text on the page", "Ctrl Alt T"),
                     ("Translator (panel)", "Floating panel above the tab", "Ctrl Alt Y"),
                     ("Picture-in-picture", "Pop the video out into its own window", "Ctrl Alt P"),
                     ("History / downloads / settings", "Internal strata:// pages", "Ctrl H · J · ,"),
                     ("Full screen / DevTools", "", "F11 · F12"),
                     ("Middle-click on a tab", "Close; dragging moves it in the tree or to a space", "Mouse 3"),
                 })
        {
            var chip = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(9, 4, 9, 4) };
            chip.SetResourceReference(Border.BorderBrushProperty, "Hair2");
            chip.SetResourceReference(Border.BackgroundProperty, "Well");
            chip.Child = new TextBlock { Text = keys, Style = (Style)FindResource("Mono"), FontSize = 11 };
            ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, "Text");
            Row(label, hint, chip);
        }
    }

    private void BuildPerformance()
    {
        Header("Performance", "The memory budget for the window and what happens when it's exceeded. The biggest saving is tab sleep: 100 sleeping tabs use less memory than a single live one.");
        Choice("Memory budget", "The oldest background tabs sleep once it's exceeded",
            new (string, object)[] { ("1 GB", 1024), ("2 GB", 2048), ("4 GB", 4096), ("8 GB", 8192), ("no limit", 0) },
            () => _s.MemoryBudgetMb, v => _s.MemoryBudgetMb = (int)v);
        Toggle("Efficiency mode", "Hidden tabs immediately get low memory priority", () => _s.EfficiencyMode, v => _s.EfficiencyMode = v);
        Toggle("Hardware acceleration", "GPU compositing. Applies after a restart", () => _s.HardwareAcceleration, v => _s.HardwareAcceleration = v);
        ActionRow("Sleep all background tabs", "Keep only the visible ones in memory", "Sleep", () => { _tabs.SleepAllBackground(); });
        Stat($"Right now: {Format.Bytes(_tabs.MemoryBytes)} · engine processes: {_tabs.ProcessCount} · {_tabs.LiveTabs} in memory out of {Plural.Tabs(_tabs.TotalTabs)}");
    }

    private void BuildSearch()
    {
        Header("Search and translation", "The default search engine, live suggestions, and page translation.");
        Choice("Default", "Google shows AI answers right in the results",
            new (string, object)[] { ("Google", SearchEngineKind.Google), ("Yandex", SearchEngineKind.Yandex), ("Bing", SearchEngineKind.Bing), ("DuckDuckGo", SearchEngineKind.DuckDuckGo), ("Brave Search", SearchEngineKind.Brave) },
            () => _s.SearchEngine, v => _s.SearchEngine = (SearchEngineKind)v);
        Toggle("Search suggestions", "Live Google suggestions in the address bar and palette", () => _s.SearchSuggestions, v => _s.SearchSuggestions = v);
        foreach (var (label, hint, prefix) in new[] { ("Tab prefix", "Jump to an open tab", "#"), ("History prefix", "Search history only", "@"), ("Command prefix", "Browser actions only", ">") })
        {
            var chip = new TextBlock { Text = prefix, Style = (Style)FindResource("Mono"), FontSize = 13 };
            chip.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
            Row(label, hint, chip);
        }
        Choice("Translation language", "Pages and selected text are translated into this language",
            new (string, object)[] { ("Русский", "ru"), ("English", "en"), ("Español", "es"), ("Deutsch", "de"), ("Français", "fr"), ("中文", "zh-CN") },
            () => _s.UiLanguage, v => _s.UiLanguage = (string)v);
        Toggle("Quick selection translate", "Select text on a page — a translation appears next to it", () => _s.QuickTranslate, v => _s.QuickTranslate = v);
        Stat("Translate page — the globe button in the address bar, or Ctrl Alt T. Picture-in-picture for video — Ctrl Alt P.");
    }

    private void BuildImport()
    {
        Header("Import from browsers", "Bring over bookmarks, cookies (keeps you signed in on sites), history, and passwords. Data is read locally; cookies need the engine running — open any site first.");
        var profiles = ImportService.Detect();
        if (profiles.Count == 0) { Stat("No installed browsers found."); return; }

        foreach (var profile in profiles)
        {
            var p = profile;
            var opt = new ImportOptions();
            var body = new StackPanel();

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Core.ColorUtil.HueBrush(p.Hue), Margin = new Thickness(0, 0, 11, 0),
                Child = new TextBlock { Text = p.Glyph, Foreground = Core.ColorUtil.HueForeground(p.Hue), FontWeight = FontWeights.SemiBold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            head.Children.Add(Kit.Text(p.Name, 14, weight: FontWeights.Medium));
            ((TextBlock)head.Children[^1]).VerticalAlignment = VerticalAlignment.Center;
            body.Children.Add(head);

            var checks = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
            CheckBox Chk(string label, bool init, Action<bool> set)
            {
                var cb = new CheckBox { Content = label, IsChecked = init, Margin = new Thickness(0, 0, 18, 0), Foreground = Kit.Res("Read"), VerticalContentAlignment = VerticalAlignment.Center };
                cb.Checked += (_, _) => set(true);
                cb.Unchecked += (_, _) => set(false);
                checks.Children.Add(cb);
                return cb;
            }
            Chk("Bookmarks", true, v => opt.Bookmarks = v);
            Chk("Cookies / sign-ins", true, v => opt.Cookies = v);
            Chk("History", true, v => opt.History = v);
            Chk("Passwords", p.Family == BrowserFamily.Chromium, v => opt.Passwords = v);
            body.Children.Add(checks);

            var status = new TextBlock { Style = (Style)FindResource("Mono"), FontSize = 11, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
            status.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

            var button = new Button { Content = "Import", Style = (Style)FindResource("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                status.Text = "Importing…";
                var result = await ImportService.ImportAsync(p, opt, _tabs.Bookmarks, _tabs.History, _tabs.Vault, _tabs.AnyCore);
                status.Text = "Done: " + result + (result.Notes.Count > 0 ? " · " + string.Join(" ", result.Notes) : "");
                button.IsEnabled = true;
                Note($"Imported from {p.Name}: {result}");
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(button);
            body.Children.Add(row);
            body.Children.Add(status);

            var card = Kit.Card(body, new Thickness(18));
            card.Margin = new Thickness(0, 0, 0, 12);
            Body.Children.Add(card);
        }

        Stat($"In Strata's vault: {_tabs.Vault.Count} passwords, {_tabs.Bookmarks.Items.Count} bookmarks. Passwords are encrypted with DPAPI and filled in automatically.");
    }

    private void BuildSystem()
    {
        Header("System", "The engine, profile data, and build information.");
        Stat($"Engine: Microsoft Edge WebView2 {WebEngine.RuntimeVersion}");
        ActionRow("Profile folder", AppPaths.Root, "Open", () => Native.OpenExternal(AppPaths.Root));
        ActionRow("Set as default browser", "Opens Windows' \"Default apps\" settings", "Settings",
            () => Native.OpenExternal("ms-settings:defaultapps"));
        ActionRow("Restart Strata", "Needed after changing hardware acceleration", "Restart", () =>
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            _s.Save();
            System.Diagnostics.Process.Start(exe);
            Application.Current.MainWindow?.Close();
        });
    }

    // ------------------------------------------------------------------ builders

    private void Header(string title, string lead)
    {
        Body.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("PageTitle") });
        Body.Children.Add(new TextBlock { Text = lead, Style = (Style)FindResource("PageLead"), Margin = new Thickness(0, 8, 0, 20) });
    }

    private void Row(string label, string hint, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        texts.Children.Add(Kit.Text(label, 13));
        if (hint.Length > 0)
        {
            var h = Kit.Text(hint, 12, "Faint");
            h.Margin = new Thickness(0, 3, 0, 0);
            texts.Children.Add(h);
        }
        grid.Children.Add(texts);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);

        var border = new Border { Padding = new Thickness(0, 14, 0, 14), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        border.SetResourceReference(Border.BorderBrushProperty, "Hair");
        Body.Children.Add(border);
    }

    private void Toggle(string label, string hint, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = get() };
        box.Click += (_, _) => { set(box.IsChecked == true); _s.Save(); };
        Row(label, hint, box);
    }

    private void Choice(string label, string hint, (string Text, object Value)[] options, Func<object> get, Action<object> set)
    {
        var combo = new ComboBox { Style = (Style)FindResource("ValueCombo") };
        var current = get();
        foreach (var (text, value) in options)
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            combo.Items.Add(item);
            if (Equals(value, current)) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: { } value })
            {
                set(value);
                _s.Save();
            }
        };
        Row(label, hint, combo);
    }

    private void ActionRow(string label, string hint, string button, Action action)
    {
        var b = new Button { Content = button, Style = (Style)FindResource("OutlineButton") };
        b.Click += (_, _) => action();
        Row(label, hint, b);
    }

    private void Stat(string text)
    {
        var block = new TextBlock { Text = text, Style = (Style)FindResource("Mono"), FontSize = 11, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(2, 18, 0, 0) };
        Body.Children.Add(block);
    }

    private void Note(string text) => (Application.Current.MainWindow as MainWindow)?.ShowToast(text);
}
