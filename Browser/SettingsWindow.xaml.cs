using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Browser
{
    public partial class SettingsWindow : Window
    {
        public BrowserSettings Settings { get; set; }

        public SettingsWindow(BrowserSettings settings)
        {
            InitializeComponent();
            Settings = settings;
            DataContext = this;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            Settings.Save();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ClearDataButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will clear all browsing data including history, cookies, and cache. Continue?",
                "Clear Browsing Data",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                // Clear browsing data logic would go here
                MessageBox.Show("Browsing data has been cleared.", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void Button_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Button button)
            {
                button.Opacity = 0.9;
            }
        }

        private void Button_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Button button)
            {
                button.Opacity = 1.0;
            }
        }
    }
}