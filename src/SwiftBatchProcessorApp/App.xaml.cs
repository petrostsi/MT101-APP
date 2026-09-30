using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;
using SwiftBatchApp.Views;

namespace SwiftBatchApp;

public partial class App : Application
{
    private const string SingleInstanceName = "SwiftBatchProcessorApp_SingleInstance";
    public const string SwitchAccountArg = "--switch-account";
    private const string RelaunchArg = "--relaunch";

    private static Mutex? _singleInstance;

    /// <summary>The engine exists only on the manager's PC.</summary>
    public static ProcessingEngine? Engine { get; private set; }

    /// <summary>Engine log lines (UI thread), kept while the app runs.</summary>
    public static ObservableCollection<string> EngineLog { get; } = new();

    /// <summary>Raised on the UI thread after each engine cycle.</summary>
    public static event Action? CycleCompleted;

    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
        .Split('+')[0];

    public static string AppFolder => AppContext.BaseDirectory;
    public static string LogFolder => Path.Combine(AppFolder, "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _singleInstance = new Mutex(true, SingleInstanceName, out bool first);
        if (!first && e.Args.Contains(RelaunchArg, StringComparer.OrdinalIgnoreCase))
        {
            try { first = _singleInstance.WaitOne(TimeSpan.FromSeconds(10)); }      // the old instance is closing
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            MessageBox.Show("SWIFT Batch Processor is already running on this PC.", "SWIFT Batch Processor",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstance = null;
            Shutdown();
            return;
        }

        try
        {
            AppDb.Initialize(null, DefaultsFile.Load());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The local database next to the program could not be opened:\n\n{ex.Message}\n\n" +
                            "Run the program from a folder you can write to (not Program Files).",
                "SWIFT Batch Processor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        bool switchAccount = e.Args.Contains(SwitchAccountArg, StringComparer.OrdinalIgnoreCase);
        ResolveOutcome outcome = Session.Resolve();
        if (switchAccount || outcome == ResolveOutcome.NeedsIdentity)
        {
            var picker = new IdentityPickerDialog(allowCancel: switchAccount);
            if (picker.ShowDialog() != true && outcome == ResolveOutcome.NeedsIdentity)
            {
                Shutdown();
                return;
            }
        }

        if (Session.IsManager) CreateEngine();

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();

        if (Engine is not null && AppDb.LoadSettings().AutoStart) Engine.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Engine?.Dispose(); } catch { /* shutting down */ }
        try
        {
            _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
        }
        catch
        {
            // not owned any more
        }
        base.OnExit(e);
    }

    /// <summary>Creates the engine (manager PC only). Called at startup or after claiming manager.</summary>
    public static void CreateEngine()
    {
        if (Engine is not null) return;
        Engine = new ProcessingEngine(cfg => new OutlookMailer(cfg.SenderAccount)) { LogDirectory = LogFolder };
        Engine.Log += line => Current?.Dispatcher.BeginInvoke(() =>
        {
            EngineLog.Add(line);
            while (EngineLog.Count > 3000) EngineLog.RemoveAt(0);
        });
        Engine.CycleCompleted += _ => Current?.Dispatcher.BeginInvoke(() => CycleCompleted?.Invoke());
    }

    /// <summary>Restarts the exe once this instance has exited (the single-instance mutex must be free first).</summary>
    public static void Relaunch(string arguments = "")
    {
        string? exe = Environment.ProcessPath;
        if (exe is null) return;
        // ping as a 1-second delay: `timeout` refuses to run without console input.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 2 127.0.0.1 >nul & start \"\" \"{exe}\" {RelaunchArg} {arguments}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        Current.Shutdown();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Ui.Error("Unexpected error:\n\n" + e.Exception.Message);
        e.Handled = true;
    }
}
