using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class ProcessingView : UserControl, IRefreshable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public ProcessingView()
    {
        InitializeComponent();
        LogList.ItemsSource = App.EngineLog;
        App.EngineLog.CollectionChanged += OnLogChanged;
        _timer.Tick += (_, _) => UpdateState();
        Loaded += (_, _) =>
        {
            _timer.Start();
            ScrollToEnd();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void Refresh()
    {
        UpdateState();
        UpdateSideCards();
    }

    private void UpdateState()
    {
        ProcessingEngine? engine = App.Engine;
        if (engine is null)
        {
            StateText.Text = "The engine is not available on this PC.";
            StartStopButton.IsEnabled = false;
            return;
        }
        bool running = engine.IsLoopRunning;
        string state = engine.State == EngineState.Working ? "Working" : running ? "Idle" : "Stopped";
        StateDot.Fill = (Brush)new StatusBrushConverter().Convert(state, typeof(Brush), null!, CultureInfo.CurrentCulture);
        StateText.Text = state switch
        {
            "Working" => "Processing a cycle…",
            "Idle" => "Running — waiting for the next cycle",
            _ => "Stopped",
        };
        var parts = new List<string>();
        if (engine.LastCycleAt is { } last) parts.Add($"last cycle {last:HH:mm:ss}" + (engine.LastSummary is { } s ? $": {s}" : ""));
        if (running && engine.State == EngineState.Idle && engine.NextCycleAt is { } next) parts.Add($"next at {next:HH:mm:ss}");
        StateDetail.Text = string.Join(" · ", parts);
        StartStopButton.Content = running ? "Stop engine" : "Start engine";

        if (engine.State != EngineState.Working) UpdateSideCards();
    }

    private void UpdateSideCards()
    {
        EngineSettings cfg;
        try { cfg = AppDb.LoadSettings(); }
        catch { return; }

        WatchPath.Text = cfg.WatchFolder;
        try
        {
            if (cfg.WatchFolder.Length > 0 && Directory.Exists(cfg.WatchFolder))
            {
                var files = cfg.FilePatterns
                    .SelectMany(p => Directory.EnumerateFiles(cfg.WatchFolder, p, new EnumerationOptions { MatchType = MatchType.Simple, IgnoreInaccessible = true }))
                    .Select(Path.GetFileName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                WatchCount.Text = files.Count == 0 ? "No files waiting" : $"{Ui.Plural(files.Count, "file")} waiting for the next cycle";
                WatchFiles.Text = string.Join("\n", files.Take(12)) + (files.Count > 12 ? $"\n… and {files.Count - 12} more" : "");
            }
            else
            {
                WatchCount.Text = "Watch folder not reachable";
                WatchFiles.Text = "";
            }
        }
        catch (Exception ex)
        {
            WatchCount.Text = "Cannot list the watch folder: " + ex.Message;
        }

        ScheduleText.Text = $"Cut-off {BusinessCalendar.FormatTime(cfg.Cutoff)} · sent on the next working day at {BusinessCalendar.FormatTime(cfg.DeferredSendTime)}";
        try
        {
            DeferredGrid.ItemsSource = AppDb.GetOpenDeferred();
            List<PendingRow> queued = AppDb.GetPendingRows();
            QueueText.Text = queued.Count == 0
                ? "Empty — every row reached Excel."
                : $"{Ui.Plural(queued.Count, "row")} waiting (oldest {queued[0].CreatedAt}). " +
                  $"Last error: {queued.Last().LastError}\nThey are written automatically at the start of the next cycle.";
        }
        catch (Exception ex)
        {
            QueueText.Text = ex.Message;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && IsLoaded) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (AutoScroll.IsChecked == true && LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        ProcessingEngine? engine = App.Engine;
        if (engine is null) return;
        if (engine.IsLoopRunning) engine.Stop(TimeSpan.Zero);
        else engine.Start();
        UpdateState();
    }

    private void CheckNow_Click(object sender, RoutedEventArgs e) => App.Engine?.TriggerNow();

    private void OpenWatch_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppDb.GetSetting(SettingKeys.WatchFolder));

    private void OpenArchive_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppDb.GetSetting(SettingKeys.ArchiveRoot));

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.LogFolder);
        Ui.OpenFolder(App.LogFolder);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogList.SelectedItems.Count > 0 ? LogList.SelectedItems.Cast<string>() : App.EngineLog;
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => App.EngineLog.Clear();
}
