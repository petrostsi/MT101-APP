using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class ClientsView : UserControl, IRefreshable
{
    private List<ClientRow> _rows = new();

    public ClientsView() => InitializeComponent();

    public sealed class ClientRow
    {
        public required CustomerRecord Record { get; init; }
        public int Files { get; init; }
        public DateTime? LastFile { get; init; }
        public string LastFileText => WorkbookLayout.DisplayDate(LastFile);
    }

    public void Refresh()
    {
        string path = AppDb.GetSetting(SettingKeys.MasterFilePath);
        MasterPathText.Text = path.Length == 0 ? "No master customer file configured (Settings) — customer validation is off." : path;
        if (path.Length == 0)
        {
            _rows = new List<ClientRow>();
            ApplyFilter();
            return;
        }

        IReadOnlyList<CustomerRecord> customers;
        try
        {
            customers = CustomerMaster.Load(path);
        }
        catch (Exception ex)
        {
            MasterPathText.Text = $"Cannot read {path}: {ex.Message}";
            _rows = new List<ClientRow>();
            ApplyFilter();
            return;
        }

        var byCustomer = WorkbookStore.Registry
            .Where(e => e.MatchKind == MatchOutcome.Match)
            .GroupBy(e => e.MatchedCustomer, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Last: g.Max(e => e.Day)), StringComparer.OrdinalIgnoreCase);

        _rows = customers.Select(c =>
        {
            byCustomer.TryGetValue(c.Customer, out var stats);
            return new ClientRow { Record = c, Files = stats.Count, LastFile = stats.Last };
        }).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string s = SearchBox.Text.Trim();
        var rows = _rows.Where(r => Ui.Matches(s, r.Record.Customer, r.Record.Instructing, r.Record.Bic, r.Record.IbansText,
                r.Record.Asc, r.Record.Crs, r.Record.Valeur, r.Record.IbansText.Replace(" ", "")))
            .OrderBy(r => r.Record.Customer, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ClientsGrid.ItemsSource = rows;
        FilesTitle.Text = $"Files of the selected client · {Ui.Plural(rows.Count, "client")} shown";
    }

    private void ClientsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClientsGrid.SelectedItem is not ClientRow row)
        {
            ClientFilesGrid.ItemsSource = null;
            return;
        }
        FilesTitle.Text = $"Files of {row.Record.Customer}";
        ClientFilesGrid.ItemsSource = WorkbookStore.Registry
            .Where(e => e.MatchKind == MatchOutcome.Match && string.Equals(e.MatchedCustomer, row.Record.Customer, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Day)
            .ToList();
    }

    private void ClientFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClientFilesGrid.SelectedItem is RegistryEntry r) MainWindow.Instance?.NavigateToRegistry(r.FileName, r.Day);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Clear_Click(object sender, RoutedEventArgs e) => SearchBox.Text = "";

    private void Reload_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenMaster_Click(object sender, RoutedEventArgs e) => Ui.OpenWithShell(AppDb.GetSetting(SettingKeys.MasterFilePath));
}
