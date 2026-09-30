using System.Windows;
using System.Windows.Controls;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class PaymentsView : UserControl, IRefreshable
{
    private const string AllUsers = "All users";
    private const string All = "All";

    private List<PaymentEntry> _loaded = new();
    private bool _suspend;
    private int _loadVersion;

    public PaymentsView()
    {
        InitializeComponent();
        _suspend = true;
        StatusFilter.ItemsSource = new[] { All, PaymentStatus.Pending, PaymentStatus.Completed };
        StatusFilter.SelectedIndex = 0;
        TypeFilter.ItemsSource = new[] { All, PaymentTypes.Mt103, PaymentTypes.Mtf };
        TypeFilter.SelectedIndex = 0;
        FromDate.SelectedDate = DateTime.Today;
        ToDate.SelectedDate = DateTime.Today;
        UserFilterPanel.Visibility = Session.IsManager ? Visibility.Visible : Visibility.Collapsed;
        _suspend = false;
    }

    // ---------------------------------------------------------------- loading

    public async void Refresh()
    {
        int version = ++_loadVersion;
        DateTime from = FromDate.SelectedDate ?? DateTime.Today;
        DateTime to = ToDate.SelectedDate ?? from;
        SummaryText.Text = "Loading…";

        List<PaymentEntry> data;
        try
        {
            data = await Task.Run(() => WorkbookStore.PaymentsInRange(from, to));
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Cannot load payments: " + ex.Message;
            return;
        }
        if (version != _loadVersion) return;           // a newer load started meanwhile

        if (!Session.IsManager) data = data.Where(p => SameUser(p.AssignedUser, Session.MyEmail)).ToList();
        _loaded = data;
        FillUserFilter();
        ApplyFilter();
    }

    private void FillUserFilter()
    {
        if (!Session.IsManager) return;
        string? selected = UserFilter.SelectedItem as string;
        var users = _loaded.Select(p => p.AssignedUser)
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
        string user = UserFilter.SelectedItem as string ?? AllUsers;
        string status = StatusFilter.SelectedItem as string ?? All;
        string type = TypeFilter.SelectedItem as string ?? All;
        string search = SearchBox.Text.Trim();

        List<PaymentEntry> rows = _loaded.Where(p =>
                (user == AllUsers || SameUser(p.AssignedUser, user)) &&
                (status == All || p.Status == status) &&
                (type == All || string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase)) &&
                Ui.Matches(search, p.FileName, p.TxnRef, p.SenderRef, p.Beneficiary, p.BeneficiaryAcct, p.OrderingCustomer,
                    p.OrderingAcct, p.AmountText, p.Currency, p.CreditorBic, p.AssignedUser))
            .OrderBy(p => p.Day).ThenBy(p => p.LoggedAt, StringComparer.Ordinal).ThenBy(p => p.FileName).ThenBy(p => p.TxnRef)
            .ToList();
        PaymentsGrid.ItemsSource = rows;

        int pending = rows.Count(p => !p.IsCompleted);
        string totals = string.Join("; ", rows.Where(p => p.Amount.HasValue)
            .GroupBy(p => p.Currency)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {DisplayFormats.Amount(g.Sum(p => p.Amount!.Value))}"));
        SummaryText.Text = $"{Ui.Plural(rows.Count, "payment")} · {pending:N0} pending · " +
                           $"{rows.Select(p => p.FileName).Distinct().Count():N0} file(s)" + (totals.Length > 0 ? $" · {totals}" : "");
    }

    // ---------------------------------------------------------------- navigation entry points

    public void FocusFile(string fileName, DateTime? day)
    {
        _suspend = true;
        FromDate.SelectedDate = ToDate.SelectedDate = day ?? DateTime.Today;
        StatusFilter.SelectedIndex = 0;
        TypeFilter.SelectedIndex = 0;
        if (Session.IsManager) UserFilter.SelectedItem = AllUsers;
        SearchBox.Text = fileName;
        _suspend = false;
        Refresh();
    }

    public void ShowPending(string user, DateTime? from)
    {
        _suspend = true;
        FromDate.SelectedDate = (from ?? DateTime.Today.AddDays(-30)).Date;
        ToDate.SelectedDate = DateTime.Today;
        StatusFilter.SelectedItem = PaymentStatus.Pending;
        TypeFilter.SelectedIndex = 0;
        SearchBox.Text = "";
        if (Session.IsManager)
        {
            var items = (UserFilter.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string> { AllUsers };
            if (!items.Contains(user, StringComparer.OrdinalIgnoreCase)) UserFilter.ItemsSource = items.Append(user).ToList();
            UserFilter.SelectedItem = user;
        }
        _suspend = false;
        Refresh();
    }

    // ---------------------------------------------------------------- events

    private void Dates_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suspend) return;
        if (FromDate.SelectedDate > ToDate.SelectedDate)
        {
            _suspend = true;
            ToDate.SelectedDate = FromDate.SelectedDate;
            _suspend = false;
        }
        Refresh();
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_suspend) ApplyFilter();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_suspend) ApplyFilter();
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _suspend = true;
        StatusFilter.SelectedIndex = 0;
        TypeFilter.SelectedIndex = 0;
        if (Session.IsManager) UserFilter.SelectedItem = AllUsers;
        SearchBox.Text = "";
        _suspend = false;
        ApplyFilter();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        WorkbookStore.MarkDirty();
        Refresh();
    }

    private void MarkCompleted_Click(object sender, RoutedEventArgs e) => SetStatus(PaymentStatus.Completed);

    private void MarkPending_Click(object sender, RoutedEventArgs e) => SetStatus(PaymentStatus.Pending);

    private async void SetStatus(string status)
    {
        List<PaymentEntry> selected = PaymentsGrid.SelectedItems.Cast<PaymentEntry>().Where(p => p.Status != status).ToList();
        if (selected.Count == 0)
        {
            Ui.Info(PaymentsGrid.SelectedItems.Count == 0 ? "Select one or more payments first." : $"The selected payments are already {status.ToLowerInvariant()}.");
            return;
        }
        if (!Session.IsManager && selected.Any(p => !SameUser(p.AssignedUser, Session.MyEmail)))
        {
            Ui.Warn("You can only change the status of payments assigned to you.");
            return;
        }

        SummaryText.Text = "Saving…";
        try
        {
            string? warning = await Task.Run(() => WorkbookStore.SetPaymentStatus(selected, status));
            if (warning is not null) Ui.Warn(warning);
        }
        catch (IOException ex)
        {
            Ui.Warn($"The day workbook could not be updated — it is probably open in Excel on another PC.\n\n{ex.Message}\n\nClose it and try again.");
        }
        catch (Exception ex)
        {
            Ui.Error("Could not update the payments: " + ex.Message);
        }
        Refresh();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (PaymentsGrid.SelectedItem is not PaymentEntry p)
        {
            Ui.Info("Select a payment first.");
            return;
        }
        RegistryEntry? entry = WorkbookStore.FindEntry(p.FileName);
        string path = entry is null ? "" : WorkbookStore.ArchivedFilePath(entry);
        if (path.Length == 0) path = Path.Combine(WorkbookLayout.DayFolder(WorkbookStore.ArchiveRoot, p.Day), p.FileName);
        Ui.OpenInNotepad(path);
    }

    private static bool SameUser(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
