using System.Windows;
using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Storage;

namespace OpenSuperWhisper.App;

public partial class MainWindow : Window
{
    private readonly HistoryStore _history;
    private readonly Action _openSettings;

    public MainWindow(HistoryStore history, AppSettings settings, Action openSettings)
    {
        InitializeComponent();
        _history = history;
        _openSettings = openSettings;
        RefreshHistory();

        // Left-click on the tray icon shows/hides this window; closing the window
        // (Alt+F4, the X button) should not end the background dictation service.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public void RefreshHistory()
    {
        HistoryList.ItemsSource = _history.Items;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _openSettings();
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Items.Count == 0) return;

        var result = MessageBox.Show(
            "确定要清空全部历史记录吗？此操作不可恢复。",
            "清空历史",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _history.Clear();
        RefreshHistory();
    }
}
