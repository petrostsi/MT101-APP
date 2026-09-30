using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class StatisticsView : UserControl, IRefreshable
{
    private bool _suspend;

    public StatisticsView()
    {
        InitializeComponent();
        _suspend = true;
        DateTime today = DateTime.Today;
        FromDate.SelectedDate = new DateTime(today.Year, today.Month, 1);
        ToDate.SelectedDate = today;
        _suspend = false;
    }

    public sealed record UserStats(string User, int Files, int Orders, int Mt103, int Mtf, int Completed, int Open, double Share)
    {
        public string ShareText => Share.ToString("P0", CultureInfo.InvariantCulture);
    }

    public void Refresh()
    {
        DateTime from = (FromDate.SelectedDate ?? DateTime.Today).Date;
        DateTime to = (ToDate.SelectedDate ?? DateTime.Today).Date;
        bool manager = Session.IsManager;
        Subtitle.Text = (manager ? "Whole team" : $"My files ({Session.MyEmail})") +
                        $" · {from:dd/MM/yyyy} – {to:dd/MM/yyyy} · from Registry.xlsx";

        List<RegistryEntry> rows = WorkbookStore.EntriesInRange(from, to)
            .Where(e => manager || string.Equals(e.AssignedUser, Session.MyEmail, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int files = rows.Count, orders = rows.Sum(e => e.Orders);
        KpiFiles.Text = files.ToString("N0");
        KpiOrders.Text = orders.ToString("N0");
        KpiTypes.Text = $"{rows.Sum(e => e.Mt103):N0} / {rows.Sum(e => e.Mtf):N0}";
        KpiCompleted.Text = files == 0 ? "–" : ((double)rows.Count(e => e.IsCompleted) / files).ToString("P0", CultureInfo.InvariantCulture);
        KpiAvg.Text = files == 0 ? "–" : ((double)orders / files).ToString("0.0", CultureInfo.InvariantCulture);
        KpiDeferred.Text = rows.Count(e => e.IsDeferred).ToString("N0");

        ByUserTitle.Text = manager ? "Payment orders by user" : "My payment orders by type";
        ByUserChart.Items = manager
            ? BarItem.From(rows.GroupBy(e => e.AssignedUser, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(e => e.Orders))
                .Select(g => (g.Key, (double)g.Sum(e => e.Orders))))
            : BarItem.From(new[] { ("MT103", (double)rows.Sum(e => e.Mt103)), ("MTF", (double)rows.Sum(e => e.Mtf)) });

        ByDayChart.Items = BarItem.From(rows.Where(e => e.Day.HasValue)
            .GroupBy(e => e.Day!.Value)
            .OrderBy(g => g.Key)
            .TakeLast(31)
            .Select(g => (g.Key.ToString("ddd dd/MM", CultureInfo.InvariantCulture), (double)g.Count())));

        var converter = new StatusBrushConverter();
        var byStatus = rows.GroupBy(e => e.DisplayStatus).OrderByDescending(g => g.Count()).ToList();
        double maxStatus = byStatus.Count == 0 ? 0 : byStatus.Max(g => g.Count());
        ByStatusChart.Items = byStatus.Select(g => new BarItem(g.Key, g.Count(), maxStatus, null,
            (Brush)converter.Convert(g.Key, typeof(Brush), null!, CultureInfo.CurrentCulture))).ToList();

        ByClientChart.Items = BarItem.From(rows.Where(e => e.MatchKind == MatchOutcome.Match)
            .GroupBy(e => e.MatchedCustomer, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => (g.Key, (double)g.Count())));

        UserTable.ItemsSource = rows.GroupBy(e => e.AssignedUser, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UserStats(g.Key, g.Count(), g.Sum(e => e.Orders), g.Sum(e => e.Mt103), g.Sum(e => e.Mtf),
                g.Count(e => e.IsCompleted), g.Count(e => !e.IsCompleted), orders == 0 ? 0 : (double)g.Sum(e => e.Orders) / orders))
            .OrderByDescending(s => s.Orders)
            .ToList();
    }

    private void Dates_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_suspend) Refresh();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        DateTime today = DateTime.Today;
        (DateTime from, DateTime to) = ((sender as Button)?.Tag as string) switch
        {
            "today" => (today, today),
            "7" => (today.AddDays(-6), today),
            "lastmonth" => (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1).AddDays(-1)),
            "year" => (new DateTime(today.Year, 1, 1), today),
            _ => (new DateTime(today.Year, today.Month, 1), today),
        };
        _suspend = true;
        FromDate.SelectedDate = from;
        ToDate.SelectedDate = to;
        _suspend = false;
        Refresh();
    }
}
