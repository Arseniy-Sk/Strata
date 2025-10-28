using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

namespace Browser
{
    public partial class DevToolsWindow : Window, INotifyPropertyChanged
    {
        private string _currentUrl = "";
        private string _consoleOutputText = "Console ready...\n";
        private string _pageSourceText = "";

        public string CurrentUrl
        {
            get => _currentUrl;
            set
            {
                _currentUrl = value;
                OnPropertyChanged(nameof(CurrentUrl));
                Title = $"Developer Tools - {value}";
            }
        }

        public string ConsoleOutputText
        {
            get => _consoleOutputText;
            set
            {
                _consoleOutputText = value;
                OnPropertyChanged(nameof(ConsoleOutputText));
                ScrollConsoleToEnd();
            }
        }

        public string PageSourceText
        {
            get => _pageSourceText;
            set
            {
                _pageSourceText = value;
                OnPropertyChanged(nameof(PageSourceText));
            }
        }

        public ObservableCollection<NetworkRequest> NetworkRequests { get; } = new ObservableCollection<NetworkRequest>();

        public DevToolsWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        public void SetPageSource(string html)
        {
            PageSourceText = html;
        }

        public void AddConsoleMessage(string message)
        {
            ConsoleOutputText += $"{DateTime.Now:HH:mm:ss} - {message}\n";
        }

        public void AddNetworkRequest(string url, string status, string type, string size)
        {
            NetworkRequests.Add(new NetworkRequest { Url = url, Status = status, Type = type, Size = size });
        }

        private void ScrollConsoleToEnd()
        {
            try
            {
                // Use Dispatcher to ensure UI thread operation
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ConsoleOutputTextBox.CaretIndex = ConsoleOutputTextBox.Text.Length;
                    ConsoleOutputTextBox.ScrollToEnd();
                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Scroll console error: {ex.Message}");
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            AddConsoleMessage("Page refresh requested");
        }

        private void ClearConsoleButton_Click(object sender, RoutedEventArgs e)
        {
            ConsoleOutputText = "Console cleared\n";
        }

        private void ViewSourceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (HtmlViewerTextBox != null)
                {
                    var tabControl = HtmlViewerTextBox.Parent as TabControl;
                    if (tabControl != null)
                    {
                        tabControl.SelectedIndex = 1; // Elements tab
                    }
                }
            }
            catch (Exception ex)
            {
                AddConsoleMessage($"View source error: {ex.Message}");
            }
        }

        private void InspectElementButton_Click(object sender, RoutedEventArgs e)
        {
            AddConsoleMessage("Element inspection mode activated - click on any element in the main window");
        }

        private void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            ExecuteConsoleCommand();
        }

        private void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ExecuteConsoleCommand();
            }
        }

        private void ExecuteConsoleCommand()
        {
            try
            {
                string command = ConsoleInputTextBox.Text.Trim();
                if (!string.IsNullOrEmpty(command))
                {
                    AddConsoleMessage($"> {command}");

                    // Simulate command execution
                    if (command.ToLower() == "clear")
                    {
                        ConsoleOutputText = "Console cleared\n";
                    }
                    else if (command.ToLower().StartsWith("log "))
                    {
                        AddConsoleMessage(command.Substring(4));
                    }
                    else
                    {
                        AddConsoleMessage($"Result: Command '{command}' executed");
                    }

                    ConsoleInputTextBox.Clear();
                }
            }
            catch (Exception ex)
            {
                AddConsoleMessage($"Command error: {ex.Message}");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class NetworkRequest
    {
        public string Url { get; set; } = "";
        public string Status { get; set; } = "";
        public string Type { get; set; } = "";
        public string Size { get; set; } = "";
    }
}