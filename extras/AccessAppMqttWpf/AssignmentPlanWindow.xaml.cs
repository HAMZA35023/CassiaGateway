using AccessAppMqttWpf.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Data;

namespace AccessAppMqttWpf;

public partial class AssignmentPlanWindow : Window
{
    public AssignmentPlanWindow(AssignmentPlanWindowViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}

public enum AssignmentPlanDialogResult
{
    Cancel = 0,
    Apply = 1,
    KeepCurrent = 2,
}

public partial class AssignmentPlanWindowViewModel : ObservableObject
{
    public ObservableCollection<AssignmentChangeRow> Rows { get; }
    public ObservableCollection<CassiaLoadSummaryRow> LoadRows { get; }

    public ICollectionView RowsView { get; }

    private readonly bool _showKeep;
    private readonly Action<AssignmentPlanDialogResult> _close;

    [ObservableProperty] private string headerTitle = "";
    [ObservableProperty] private string headerSubtitle = "";
    [ObservableProperty] private string footerText = "";
    [ObservableProperty] private string notesText = "";

    [ObservableProperty] private bool showUnchanged = false;

    public string ChangesCountText
    {
        get
        {
            var total = Rows.Count;
            var changes = Rows.Count(r => r.IsChange);
            return $"{changes} change(s), {total} device(s)";
        }
    }

    public Visibility KeepButtonVisibility => _showKeep ? Visibility.Visible : Visibility.Collapsed;

    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand KeepCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand CopyCommand { get; }

    public AssignmentPlanWindowViewModel(
        string title,
        string subtitle,
        ObservableCollection<AssignmentChangeRow> rows,
        ObservableCollection<CassiaLoadSummaryRow> loadRows,
        string footer,
        string notes,
        bool showKeepButton,
        Action<AssignmentPlanDialogResult> close)
    {
        Rows = rows;
        LoadRows = loadRows;
        _showKeep = showKeepButton;
        _close = close;

        HeaderTitle = title;
        HeaderSubtitle = subtitle;
        FooterText = footer;
        NotesText = notes;

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = obj =>
        {
            if (obj is not AssignmentChangeRow r) return false;
            return ShowUnchanged || r.IsChange;
        };

        ApplyCommand = new RelayCommand(() => _close(AssignmentPlanDialogResult.Apply));
        KeepCommand = new RelayCommand(() => _close(AssignmentPlanDialogResult.KeepCurrent));
        CancelCommand = new RelayCommand(() => _close(AssignmentPlanDialogResult.Cancel));
        CopyCommand = new RelayCommand(CopyToClipboard);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShowUnchanged))
            {
                RowsView.Refresh();
                OnPropertyChanged(nameof(ChangesCountText));
            }
        };
        Rows.CollectionChanged += (_, __) => OnPropertyChanged(nameof(ChangesCountText));
    }

    partial void OnShowUnchangedChanged(bool value)
    {
        try { RowsView.Refresh(); } catch { }
        OnPropertyChanged(nameof(ChangesCountText));
    }

    private void CopyToClipboard()
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var r in Rows.OrderBy(r => r.Mac, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(r.Mac).Append("\t")
                  .Append(r.ClosestCassia).Append("\t").Append(r.ClosestRssi).Append("\t")
                  .Append(r.CurrentAssigned).Append("\t")
                  .Append(r.SuggestedAssigned).Append("\t").Append(r.SuggestedRssi).Append("\t")
                  .Append(r.Reason)
                  .AppendLine();
            }
            Clipboard.SetText(sb.ToString());
        }
        catch { }
    }
}
