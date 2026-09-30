using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class DashboardView : UserControl, IRefreshable
{
    private readonly DispatcherTimer _engineTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public DashboardView()
    {
        InitializeComponent();
        _engineTimer.Tick += (_, _) => UpdateEngine();
        Loaded += (_, _) => _engineTimer.Start();
        Unloaded += (_, _) => _engineTimer.Stop();
    }

    public void Refresh()
    {
        bool manager = Session.IsManager;
        string me = Session.MyEmail;
        DateTime today = DateTime.Today;
        bool Mine(string user) => manager || string.Equals(user, me, StringComparison.OrdinalIgnoreCase);

        Subtitle.Text = $"{today.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)} · " +
                        (manager ? "manager view — whole team" : "my work");

        List<RegistryEntry> todays = WorkbookStore.Registry
            .Where(e => e.Day == today && Mine(e.AssignedUser))
            .OrderByDescending(e => e.LoggedAt, StringComparer.Ordinal)
            .ToList();
        List<PendingWork> pending = WorkbookStore.PendingByUser().Where(w => Mine(w.User)).ToList();

        KpiFiles.Text = todays.Count.ToString("N0");
        KpiOrders.Text = todays.Sum(e => e.Orders).ToString("N0");
        KpiTypes.Text = $"{todays.Sum(e => e.Mt103):N0} / {todays.Sum(e => e.Mtf):N0}";
        KpiPending.Text = pending.Sum(w => w.PendingPayments).ToString("N0");
        KpiPendingLabel.Text = manager ? "Pending payments (all days)" : "My pending payments";
        KpiCompleted.Text = todays.Count == 0 ? "–" : $"{todays.Count(e => e.IsCompleted)} / {todays.Count}";
        KpiExceptions.Text = todays.Count(e => FileStatus.IsSpecial(e.DisplayStatus)).ToString("N0");

        PendingTitle.Text = manager ? "Pending work by user" : "My pending work";
        TodayTitle.Text = manager ? "Today's files" : "My files today";
        PendingGrid.ItemsSource = pending;
        TodayGrid.ItemsSource = todays;
        UpdateEngine();
    }

    private void UpdateEngine()
    {
        ProcessingEngine? engine = App.Engine;
        EngineCard.Visibility = engine is null ? Visibility.Collapsed : Visibility.Visible;
        if (engine is null) return;

        bool running = engine.IsLoopRunning;
        string state = engine.State == EngineState.Working ? "Working" : running ? "Idle" : "Stopped";
        EngineDot.Fill = (Brush)new StatusBrushConverter().Convert(state, typeof(Brush), null!, CultureInfo.CurrentCulture);
        EngineStatus.Text = state switch
        {
            "Working" => "Engine is processing…",
            "Idle" => "Engine running — watching for files",
            _ => "Engine stopped — files are not being distributed",
        };

        var parts = new List<string>();
        if (engine.LastCycleAt is { } last) parts.Add($"last cycle {last:HH:mm}" + (engine.LastSummary is { } s ? $" ({s.Processed} sent, {s.Deferred} deferred)" : ""));
        if (running && engine.State == EngineState.Idle && engine.NextCycleAt is { } next) parts.Add($"next {next:HH:mm}");
        try
        {
            int deferred = AppDb.GetOpenDeferred().Count;
            int queued = AppDb.CountPendingRows();
            if (deferred > 0) parts.Add($"{deferred} deferred file(s) waiting");
            if (queued > 0) parts.Add($"{queued} Excel row(s) queued");
        }
        catch
        {
            // status only
        }
        EngineDetail.Text = string.Join(" · ", parts);
        StartStopButton.Content = running ? "Stop engine" : "Start engine";
        StartStopButton.IsEnabled = !(engine.State == EngineState.Working && !running);
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        ProcessingEngine? engine = App.Engine;
        if (engine is null) return;
        if (engine.IsLoopRunning) engine.Stop(TimeSpan.Zero);
        else engine.Start();
        UpdateEngine();
    }

    private void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        App.Engine?.TriggerNow();
        UpdateEngine();
    }

    private void OpenProcessing_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateToProcessing();

    private void PendingGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PendingGrid.SelectedItem is PendingWork w) MainWindow.Instance?.NavigateToPendingPayments(w.User, w.OldestDay);
    }

    private void TodayGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TodayGrid.SelectedItem is RegistryEntry r) MainWindow.Instance?.NavigateToRegistry(r.FileName, r.Day);
    }
}
