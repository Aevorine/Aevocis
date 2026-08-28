using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Storage;

namespace OpenSuperWhisper.App;

/// <summary>
/// F02 UI: lets a non-technical user edit the professional-vocabulary correction list that used
/// to be editable only by hand-editing terms.json. A plain "错误词 -> 正确词" grid backed directly
/// by <see cref="TermDictionaryStore"/>.Load()/Save() - edits happen on a mutable copy (TermRow),
/// so clicking "取消" (or the window's X) walks away without touching the file on disk.
/// </summary>
public partial class TermDictionaryWindow : Window
{
    private sealed class TermRow
    {
        public string Wrong { get; set; } = "";
        public string Correct { get; set; } = "";
    }

    private readonly TermDictionaryStore _store;
    private readonly ObservableCollection<TermRow> _rows;

    public TermDictionaryWindow(TermDictionaryStore store)
    {
        InitializeComponent();
        _store = store;
        _rows = new ObservableCollection<TermRow>(
            _store.Load().Select(t => new TermRow { Wrong = t.Wrong, Correct = t.Correct }));
        TermsGrid.ItemsSource = _rows;
    }

    private void AddRowButton_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new TermRow());
    }

    private void DeleteRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (TermsGrid.SelectedItem is TermRow row)
            _rows.Remove(row);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Flush any in-progress cell edit (the user clicking straight from a text box to Save,
        // without tabbing/clicking off the cell first) into the bound TermRow before reading it.
        TermsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var corrections = _rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Wrong) && !string.IsNullOrWhiteSpace(r.Correct))
            .Select(r => new TermCorrection(r.Wrong.Trim(), r.Correct.Trim()))
            .ToList();
        _store.Save(corrections);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
