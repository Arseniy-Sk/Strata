using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;

namespace Browser
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly NavigationService _navigationService;
        private readonly BrowserSettings _settings;
        private string _currentUrl;
        private string _statusMessage = "Ready";
        private bool _isLoading;
        private string _pageTitle = "Simple Browser";

        // Основной конструктор
        public MainViewModel(NavigationService navigationService, BrowserSettings settings)
        {
            _navigationService = navigationService;
            _settings = settings;
            _currentUrl = _settings.HomePage;

            InitializeCommands();
            SubscribeToEvents();
        }

        // Конструктор по умолчанию для резервного создания
        public MainViewModel() : this(new NavigationService(), new BrowserSettings())
        {
        }

        public string CurrentUrl
        {
            get => _currentUrl;
            set => SetProperty(ref _currentUrl, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string PageTitle
        {
            get => _pageTitle;
            set => SetProperty(ref _pageTitle, value);
        }

        public bool CanGoBack => _navigationService.CanGoBack;
        public bool CanGoForward => _navigationService.CanGoForward;

        public RelayCommand NavigateCommand { get; private set; }
        public RelayCommand NavigateBackCommand { get; private set; }
        public RelayCommand NavigateForwardCommand { get; private set; }
        public RelayCommand RefreshCommand { get; private set; }
        public RelayCommand GoHomeCommand { get; private set; }
        public RelayCommand ShowSettingsCommand { get; private set; }
        public RelayCommand ExitCommand { get; private set; }

        public event Action<string> NavigationRequested;

        private void InitializeCommands()
        {
            NavigateCommand = new RelayCommand(async () => await NavigateAsync());
            NavigateBackCommand = new RelayCommand(() => GoBack(), () => CanGoBack);
            NavigateForwardCommand = new RelayCommand(() => GoForward(), () => CanGoForward);
            RefreshCommand = new RelayCommand(async () => await RefreshAsync());
            GoHomeCommand = new RelayCommand(() => GoHome());
            ShowSettingsCommand = new RelayCommand(() => ShowSettings());
            ExitCommand = new RelayCommand(() => Application.Current.Shutdown());
        }

        private void SubscribeToEvents()
        {
            _navigationService.NavigationStarted += OnNavigationStarted;
            _navigationService.NavigationCompleted += OnNavigationCompleted;
            _navigationService.NavigationFailed += OnNavigationFailed;
        }

        private async Task NavigateAsync()
        {
            if (string.IsNullOrWhiteSpace(CurrentUrl)) return;

            if (IsSearchQuery(CurrentUrl) && !CurrentUrl.StartsWith("http"))
            {
                string searchUrl = ConvertToSearchUrl(CurrentUrl);
                CurrentUrl = searchUrl;
            }
            else if (!CurrentUrl.StartsWith("http://") && !CurrentUrl.StartsWith("https://"))
            {
                CurrentUrl = "https://" + CurrentUrl;
            }

            await _navigationService.NavigateAsync(CurrentUrl);
            NavigationRequested?.Invoke(CurrentUrl);
        }

        private bool IsSearchQuery(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            if (input.StartsWith("http://") || input.StartsWith("https://")) return false;
            return input.Contains(" ") || (!input.Contains(".") && input.Length > 3);
        }

        private string ConvertToSearchUrl(string query)
        {
            string encodedQuery = Uri.EscapeDataString(query);
            return $"https://www.google.com/search?q={encodedQuery}";
        }

        private void GoBack()
        {
            _navigationService.GoBack();
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }

        private void GoForward()
        {
            _navigationService.GoForward();
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }

        private async Task RefreshAsync()
        {
            if (!string.IsNullOrEmpty(CurrentUrl))
            {
                await NavigateAsync();
            }
        }

        private void GoHome()
        {
            CurrentUrl = _settings.HomePage;
            NavigateCommand.Execute(null);
        }

        private void ShowSettings()
        {
            // This will be handled by the View
        }

        private void OnNavigationStarted(string url)
        {
            IsLoading = true;
            StatusMessage = $"Loading {url}...";

            try
            {
                var uri = new Uri(url);
                PageTitle = $"{uri.Host} - Simple Browser";
            }
            catch
            {
                PageTitle = "Simple Browser";
            }
        }

        private void OnNavigationCompleted(string url, string title)
        {
            IsLoading = false;
            StatusMessage = "Done";

            if (!string.IsNullOrEmpty(title) && title != "Back navigation" && title != "Forward navigation")
            {
                PageTitle = $"{title} - Simple Browser";
            }

            CurrentUrl = url;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }

        private void OnNavigationFailed(string url, string error)
        {
            IsLoading = false;
            StatusMessage = $"Error: {error}";
            PageTitle = "Navigation Failed - Simple Browser";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}