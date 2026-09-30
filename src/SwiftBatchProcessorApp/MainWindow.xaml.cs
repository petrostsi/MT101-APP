using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;
using SwiftBatchApp.Views;

namespace SwiftBatchApp;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _views = new();
    private readonly DispatcherTimer _dataTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _refreshing;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"SWIFT Batch Processor v{App.Version}";
        ApplyRole();

        _dataTimer.Tick += async (_, _) => await RefreshDataAsync();
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        App.CycleCompleted += async () =>
        {
            WorkbookStore.MarkDirty();
            await RefreshDataAsync();
        };

        Loaded += async (_, _) =>
        {
            NavDashboard.IsChecked = true;
            UpdateStatusBar();
            _dataTimer.Start();
            _statusTimer.Start();
            await RefreshDataAsync();
        };
        Closing += OnClosing;
    }

    public static MainWindow? Instance => Application.Current?.MainWindow as MainWindow;

    // ---------------------------------------------------------------- role

    private void ApplyRole()
    {
        bool manager = Session.IsManager;
        foreach (RadioButton nav in new[] { NavProcessing, NavClients, NavUsers, NavSettings })
            nav.Visibility = manager ? Visibility.Visible : Visibility.Collapsed;

        UserNameText.Text = Session.MyDisplayName.Length > 0 ? Session.MyDisplayName : "(identity not set)";
        UserEmailText.Text = Session.MyEmail.Length > 0 ? Session.MyEmail : Session.WindowsUser;
        RoleText.Text = manager ? "MANAGER" : "USER";
        RoleBadge.Background = manager ? (Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xC0));
        UpdateNotice();
    }

    private void UpdateNotice()
    {
        string notice = Session.Notice.Length > 0 ? Session.Notice : WorkbookStore.LastError;
        NoticeText.Text = notice;
        NoticeBanner.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- data refresh

    /// <summary>Reloads the workbooks in the background, then refreshes the visible view.</summary>
    public async Task RefreshDataAsync(bool force = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (force) WorkbookStore.MarkDirty();
            await Task.Run(() => WorkbookStore.Reload(force));
        }
        finally
        {
            _refreshing = false;
        }
        RefreshText.Text = $"Data refreshed {DateTime.Now:HH:mm:ss}";
        UpdateNotice();
        RefreshCurrentView();
    }

    private void UpdateStatusBar()
    {
        ProcessingEngine? engine = App.Engine;
        string state;
        if (engine is null)
        {
            state = "Stopped";
            EngineText.Text = Session.IsManager ? "Engine not created" : "Processing runs on the manager's PC";
        }
        else
        {
            state = engine.IsLoopRunning ? engine.State.ToString() : "Stopped";
            string last = engine.LastCycleAt is { } l ? $" · last cycle {l:HH:mm}" : "";
            string next = engine.IsLoopRunning && engine.State == EngineState.Idle && engine.NextCycleAt is { } n ? $" · next {n:HH:mm}" : "";
            EngineText.Text = $"Engine: {(engine.State == EngineState.Working ? "working…" : state.ToLowerInvariant())}{last}{next}";
            if (engine.State == EngineState.Working) state = "Working";
        }
        EngineDot.Fill = (Brush)new StatusBrushConverter().Convert(state, typeof(Brush), null!, System.Globalization.CultureInfo.CurrentCulture);
        RegistryText.Text = $"Registry: {WorkbookStore.RegistryPath}";
    }

    // ---------------------------------------------------------------- navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb) ShowView(rb.Name["Nav".Length..]);
    }

    private UserControl GetView(string key)
    {
        if (_views.TryGetValue(key, out UserControl? view)) return view;
        view = key switch
        {
            "Dashboard" => new DashboardView(),
            "Processing" => new ProcessingView(),
            "Payments" => new PaymentsView(),
            "Registry" => new RegistryView(),
            "Clients" => new ClientsView(),
            "Statistics" => new StatisticsView(),
            "Users" => new UsersView(),
            "Settings" => new SettingsView(),
            _ => new AboutView(),
        };
        _views[key] = view;
        return view;
    }

    private void ShowView(string key)
    {
        UserControl view = GetView(key);
        ViewHost.Content = view;
        RefreshCurrentView();
    }

    /// <summary>A view that cannot load (e.g. the share dropped) reports it in the banner instead of a dialog.</summary>
    private void RefreshCurrentView()
    {
        try
        {
            (ViewHost.Content as IRefreshable)?.Refresh();
        }
        catch (Exception ex)
        {
            NoticeText.Text = "Could not load this page: " + ex.Message;
            NoticeBanner.Visibility = Visibility.Visible;
        }
    }

    private T Navigate<T>(RadioButton nav) where T : UserControl
    {
        string key = nav.Name["Nav".Length..];
        if (nav.IsChecked == true) ShowView(key);
        else nav.IsChecked = true;
        return (T)GetView(key);
    }

    public void NavigateToPayments(string fileName, DateTime? day) => Navigate<PaymentsView>(NavPayments).FocusFile(fileName, day);

    public void NavigateToPendingPayments(string user, DateTime? from) => Navigate<PaymentsView>(NavPayments).ShowPending(user, from);

    public void NavigateToRegistry(string fileName, DateTime? day) => Navigate<RegistryView>(NavRegistry).FocusEntry(fileName, day);

    public void NavigateToProcessing() => Navigate<ProcessingView>(NavProcessing);

    // ---------------------------------------------------------------- account & location

    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine?.State == EngineState.Working)
        {
            Ui.Warn("A processing cycle is running. Wait until it finishes, then switch account.");
            return;
        }
        if (!Ui.Confirm("The app will restart so you can choose who you are on this PC.\n\nContinue?")) return;
        App.Relaunch(App.SwitchAccountArg);
    }

    private void LocateRegistry_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.PickRegistry(this) is not { } registry) return;
        LocalConfig.RegistryPath = registry;
        if (Ui.Confirm("Registry location saved. Restart the app now to apply it?")) App.Relaunch();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        ProcessingEngine? engine = App.Engine;
        if (engine?.State == EngineState.Working &&
            !Ui.Confirm("A processing cycle is running (e-mails may be in flight). Close anyway?\n\n" +
                        "Files that were not finished stay in the watch folder and are picked up next time."))
        {
            e.Cancel = true;
            return;
        }
        _dataTimer.Stop();
        _statusTimer.Stop();
        engine?.Stop(TimeSpan.FromSeconds(3));
    }
}
