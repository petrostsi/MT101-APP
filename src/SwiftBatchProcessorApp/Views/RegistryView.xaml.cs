using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class RegistryView : UserControl, IRefreshable
{
    private const string AllUsers = "All users";
    private const string All = "All";

    private bool _suspend;
    private string? _focusFile;

    public RegistryView()
    {
        InitializeComponent();
        _suspend = true;
        StatusFilter.ItemsSource = new[] { All, FileStatus.Pending, FileStatus.Completed, FileStatus.Escalated, FileStatus.NoMatch, FileStatus.ParseError };
        StatusFilter.SelectedIndex = 0;
        FromDate.SelectedDate = DateTime.Today.AddDays(-6);
        ToDate.SelectedDate = DateTime.Today;
        bool manager = Session.IsManager;
        UserFilterPanel.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        ReassignButton.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;
        _suspend = false;
        ShowDetail(null);
    }

    public void Refresh()
    {
        FillUserFilter();
        ApplyFilter();
    }

    private IEnumerable<RegistryEntry> Visible() =>
        Session.IsManager ? WorkbookStore.Registry : WorkbookStore.Registry.Where(e => SameUser(e.AssignedUser, Session.MyEmail));

    private void FillUserFilter()
    {
        if (!Session.IsManager) return;
        string? selected = UserFilter.SelectedItem as string;
        var users = WorkbookStore.Registry.Select(e => e.AssignedUser)
            .Concat(Session.PickableUsers().Select(u => u.Email))
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .Prepend(AllUsers)
            .ToList();
        _suspend = true;
        UserFilter.ItemsSource = users;
        UserFilter.SelectedItem = users.FirstOrDefault(u => SameUser(u, selected)) ?? AllUsers;
        _suspend = false;
    }

    private void ApplyFilter()
    {
        DateTime from = (FromDate.SelectedDate ?? DateTime.Today).Date;
        DateTime to = (ToDate.SelectedDate ?? DateTime.Today).Date;
        string user = UserFilter.SelectedItem as string ?? AllUsers;
        string status = StatusFilter.SelectedItem as string ?? All;
        string search = SearchBox.Text.Trim();
        string? keep = _focusFile ?? (FilesGrid.SelectedItem as RegistryEntry)?.FileName;

        List<RegistryEntry> rows = Visible().Where(e =>
                e.Day is { } d && d >= from && d <= to &&
                (user == AllUsers || SameUser(e.AssignedUser, user)) &&
                (status == All || e.DisplayStatus == status) &&
                Ui.Matches(search, e.FileName, e.TxnRefs, e.AssignedUser, e.CustomerMatch, e.OsnText))
            .OrderByDescending(e => e.Day).ThenByDescending(e => e.LoggedAt, StringComparer.Ordinal)
            .ToList();
        FilesGrid.ItemsSource = rows;

        RegistryEntry? again = keep is null ? null : rows.FirstOrDefault(e => SameUser(e.FileName, keep));
        if (again is not null)
        {
            FilesGrid.SelectedItem = again;
            FilesGrid.ScrollIntoView(again);
        }
        _focusFile = null;
        ShowDetail(FilesGrid.SelectedItem as RegistryEntry);

        SummaryText.Text = $"{Ui.Plural(rows.Count, "file")} · {rows.Sum(e => e.Orders):N0} orders " +
                           $"(MT103 {rows.Sum(e => e.Mt103):N0} / MTF {rows.Sum(e => e.Mtf):N0}) · " +
                           $"{rows.Count(e => !e.IsCompleted):N0} not completed · {rows.Count(e => FileStatus.IsSpecial(e.DisplayStatus)):N0} exception(s)";
    }

    public void FocusEntry(string fileName, DateTime? day)
    {
        _suspend = true;
        FromDate.SelectedDate = ToDate.SelectedDate = day ?? DateTime.Today;
        StatusFilter.SelectedIndex = 0;
        if (Session.IsManager) UserFilter.SelectedItem = AllUsers;
        SearchBox.Text = "";
        _suspend = false;
        _focusFile = fileName;
        ApplyFilter();
    }

    // ---------------------------------------------------------------- detail pane

    private void ShowDetail(RegistryEntry? e)
    {
        bool has = e is not null;
        OpenFileButton.IsEnabled = ShowPaymentsButton.IsEnabled = ReassignButton.IsEnabled = has;
        CompleteButton.IsEnabled = has && !e!.IsCompleted && CanChange(e);
        if (e is null)
        {
            DetailTitle.Text = "No file selected";
            DetailText.Text = "Select a file above to see its payment orders.";
            FilePaymentsGrid.ItemsSource = null;
            return;
        }

        DetailTitle.Text = $"{e.FileName} — {e.DisplayStatus}";
        var sb = new StringBuilder();
        sb.Append($"Assigned to {e.AssignedUser} on {e.DayText} ({e.LoggedAt}) · {e.Orders} order(s): MT103 {e.Mt103} / MTF {e.Mtf}");
        if (e.OsnText.Length > 0) sb.Append($" · OSN {e.OsnText}");
        if (e.ExecutionDate.Length > 0) sb.Append($" · exec. date {e.ExecutionDate}");
        if (e.IsDeferred) sb.Append($" · after cut-off, processed {e.AfterCutoff}");
        if (e.CustomerMatch.Length > 0) sb.Append($"\nCustomer check: {e.CustomerMatch}" + (e.Valeur.Length > 0 ? $" · valeur {e.Valeur}" : ""));
        DetailText.Text = sb.ToString();
        FilePaymentsGrid.ItemsSource = WorkbookStore.PaymentsForFile(e);
    }

    private static bool CanChange(RegistryEntry e) => Session.IsManager || SameUser(e.AssignedUser, Session.MyEmail);

    // ---------------------------------------------------------------- events

    private void Filter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (FromDate.SelectedDate > ToDate.SelectedDate)
        {
            _suspend = true;
            ToDate.SelectedDate = FromDate.SelectedDate;
            _suspend = false;
        }
        ApplyFilter();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_suspend) ApplyFilter();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _suspend = true;
        StatusFilter.SelectedIndex = 0;
        if (Session.IsManager) UserFilter.SelectedItem = AllUsers;
        SearchBox.Text = "";
        _suspend = false;
        ApplyFilter();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is { } w) await w.RefreshDataAsync(force: true);
    }

    private void FilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail(FilesGrid.SelectedItem as RegistryEntry);

    private void FilesGrid_DoubleClick(object sender, MouseButtonEventArgs e) => OpenFile_Click(sender, e);

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not RegistryEntry entry) return;
        string path = WorkbookStore.ArchivedFilePath(entry);
        if (path.Length == 0 && entry.Day is { } day) path = Path.Combine(WorkbookLayout.DayFolder(WorkbookStore.ArchiveRoot, day), entry.FileName);
        Ui.OpenInNotepad(path);
    }

    private void ShowPayments_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is RegistryEntry entry) MainWindow.Instance?.NavigateToPayments(entry.FileName, entry.Day);
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not RegistryEntry entry) return;
        if (!CanChange(entry))
        {
            Ui.Warn("You can only complete files assigned to you.");
            return;
        }
        if (!Ui.Confirm($"Mark all {entry.Orders} payment(s) of {entry.FileName} as completed?")) return;
        _focusFile = entry.FileName;
        try
        {
            string? warning = await Task.Run(() => WorkbookStore.MarkFileCompleted(entry));
            if (warning is not null) Ui.Warn(warning);
        }
        catch (IOException ex)
        {
            Ui.Warn($"The workbook could not be updated — it is probably open in Excel on another PC.\n\n{ex.Message}");
        }
        catch (Exception ex)
        {
            Ui.Error("Could not complete the file: " + ex.Message);
        }
        _focusFile = entry.FileName;
        ApplyFilter();
    }

    private async void Reassign_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not RegistryEntry entry || !Session.IsManager) return;
        var dlg = new ReassignDialog(entry) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.NewUser is not { Length: > 0 } newUser) return;

        try
        {
            await Task.Run(() => WorkbookStore.Reassign(entry, newUser));
        }
        catch (IOException ex)
        {
            Ui.Warn($"The workbooks could not be updated — one is probably open in Excel.\n\n{ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            Ui.Error("Could not reassign the file: " + ex.Message);
            return;
        }

        if (dlg.SendEmail)
        {
            string attachment = WorkbookStore.ArchivedFilePath(entry);
            using var mailer = new OutlookMailer(AppDb.GetSetting(SettingKeys.SenderAccount));
            string body = $"The file {entry.FileName} ({entry.Orders} payment order(s), received {entry.DayText}) has been assigned to you.\r\n\r\n" +
                          string.Join("\r\n", WorkbookStore.PaymentsForFile(entry).Select(p =>
                              $"{p.TxnRef,-20} {p.Currency,-3} {p.AmountText,17}  {p.Type,-5}  {p.Beneficiary}")) +
                          "\r\n\r\nThis message was sent by SWIFT Batch Processor.\r\n";
            if (!mailer.SendFile(newUser, $"SWIFT File: {Path.GetFileNameWithoutExtension(entry.FileName)} (reassigned)", body,
                    attachment.Length > 0 ? attachment : null))
                Ui.Warn($"The file was reassigned, but the e-mail could not be sent: {mailer.LastError}");
        }
        _focusFile = entry.FileName;
        ApplyFilter();
    }

    private static bool SameUser(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
