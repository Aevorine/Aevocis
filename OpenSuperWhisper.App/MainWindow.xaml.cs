using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        var modelName = Path.GetFileNameWithoutExtension(settings.ModelPath).Replace("ggml-", "");
        ModelLabel.Text = $"模型：{modelName}";
        RefreshHistory();

        // Left-click on the tray icon shows/hides this window; closing the window
        // (Alt+F4, the X button) should not end the background dictation service.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    /// <summary>Reloads from the store and re-applies whatever search text is currently in
    /// HistorySearchBox, so a new dictation landing while the user is mid-search doesn't clear
    /// their filter out from under them.</summary>
    public void RefreshHistory()
    {
        ApplyHistoryFilter();
    }

    /// <summary>F10: live substring filter (case-insensitive, matches anywhere in the transcript
    /// text) over the full history - HistoryStore itself has no query API, so this filters the
    /// already-loaded in-memory Items list client-side rather than adding search plumbing to the
    /// storage layer for what is, at MaxItems=200, a trivially small list to scan.</summary>
    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyHistoryFilter();

    private void ApplyHistoryFilter()
    {
        var query = HistorySearchBox.Text;
        HistoryList.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _history.Items
            : _history.Items.Where(r => r.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
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
