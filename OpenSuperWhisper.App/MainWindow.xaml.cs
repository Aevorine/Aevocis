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

    /// <summary>
    /// Re-renders the history list from the store. The ItemsSource is ALWAYS a detached snapshot
    /// (a fresh List copy) - never the store's own live _items List. HistoryStore mutates that
    /// list in place (Add on a new dictation) with no INotifyCollectionChanged, so binding the
    /// ListBox directly to it made WPF's ListCollectionView desync: the generator cached a count
    /// that no longer matched the real list and threw "ItemsControl is inconsistent with its
    /// items source" on the next layout pass (observed as an app crash right after the history
    /// window was shown). A snapshot is immutable once handed to the view, so a mid-layout add
    /// can never desync it - RefreshHistory() re-snapshots after every dictation lands.
    /// </summary>
    private void ApplyHistoryFilter()
    {
        var query = HistorySearchBox.Text;
        // ToList() here is what makes each render independent of the store's live list.
        HistoryList.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _history.Items.ToList()
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
