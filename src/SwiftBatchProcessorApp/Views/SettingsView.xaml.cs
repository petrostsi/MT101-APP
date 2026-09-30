using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private bool _loaded;

    public SettingsView()
    {
        InitializeComponent();
        int year = DateTime.Today.Year;
        HolidayYear.ItemsSource = Enumerable.Range(year - 1, 4).ToList();
        HolidayYear.SelectedItem = DateTime.Today.Month >= 10 ? year + 1 : year;
    }

    /// <summary>Loads the stored values once; later refreshes keep unsaved edits.</summary>
    public void Refresh()
    {
        if (!_loaded) Load();
    }

    private void Load()
    {
        Dictionary<string, string> s = AppDb.GetAllSettings();
        string Get(string key) => s.GetValueOrDefault(key, "");
        WatchFolder.Text = Get(SettingKeys.WatchFolder);
        ArchiveRoot.Text = Get(SettingKeys.ArchiveRoot);
        RegistryPath.Text = Get(SettingKeys.RegistryPath);
        MasterFilePath.Text = Get(SettingKeys.MasterFilePath);
        FileFilter.Text = Get(SettingKeys.FileFilter);
        SenderAccount.Text = Get(SettingKeys.SenderAccount);
        ManagerEmail.Text = Get(SettingKeys.ManagerEmail);
        ManagerName.Text = Get(SettingKeys.ManagerName);
        OooWaitSeconds.Text = Get(SettingKeys.OooWaitSeconds);
        PollSeconds.Text = Get(SettingKeys.PollSeconds);
        CutoffTime.Text = Get(SettingKeys.CutoffTime);
        DeferredSendTime.Text = Get(SettingKeys.DeferredSendTime);
        AutoStart.IsChecked = EngineSettings.From(s).AutoStart;
        Holidays.Text = string.Join(Environment.NewLine, BusinessCalendar.ParseHolidays(Get(SettingKeys.Holidays)).OrderBy(h => h));
        UpdateHolidayInfo();
        StatusText.Text = "";
        _loaded = true;
    }

    // ---------------------------------------------------------------- save

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        TimeSpan invalid = TimeSpan.FromDays(-1);
        TimeSpan cutoff = BusinessCalendar.ParseTime(CutoffTime.Text, invalid);
        TimeSpan sendAt = BusinessCalendar.ParseTime(DeferredSendTime.Text, invalid);
        if (cutoff == invalid) errors.Add("Cut-off time must look like 13:00.");
        if (sendAt == invalid) errors.Add("Deferred send time must look like 07:00.");
        if (!int.TryParse(PollSeconds.Text.Trim(), out int poll) || poll < 10) errors.Add("Check interval must be a number of seconds (10 or more).");
        if (!int.TryParse(OooWaitSeconds.Text.Trim(), out int ooo) || ooo < 0 || ooo > 600) errors.Add("Out-of-office wait must be 0–600 seconds.");
        if (ManagerEmail.Text.Trim() is { Length: > 0 } m && !m.Contains('@')) errors.Add("Manager e-mail does not look like an e-mail address.");
        if (WatchFolder.Text.Trim().Length == 0) errors.Add("Watch folder is required.");
        if (ArchiveRoot.Text.Trim().Length == 0) errors.Add("Archive root is required.");
        if (RegistryPath.Text.Trim().Length == 0) errors.Add("Registry path is required.");
        if (errors.Count > 0)
        {
            Ui.Warn(string.Join("\n", errors));
            return;
        }

        HashSet<string> holidays = BusinessCalendar.ParseHolidays(Holidays.Text);
        var values = new Dictionary<string, string>
        {
            [SettingKeys.WatchFolder] = WatchFolder.Text.Trim(),
            [SettingKeys.ArchiveRoot] = ArchiveRoot.Text.Trim(),
            [SettingKeys.RegistryPath] = RegistryPath.Text.Trim(),
            [SettingKeys.MasterFilePath] = MasterFilePath.Text.Trim(),
            [SettingKeys.FileFilter] = FileFilter.Text.Trim().Length > 0 ? FileFilter.Text.Trim() : "*.prt",
            [SettingKeys.SenderAccount] = SenderAccount.Text.Trim(),
            [SettingKeys.ManagerEmail] = ManagerEmail.Text.Trim(),
            [SettingKeys.ManagerName] = ManagerName.Text.Trim(),
            [SettingKeys.OooWaitSeconds] = ooo.ToString(CultureInfo.InvariantCulture),
            [SettingKeys.PollSeconds] = poll.ToString(CultureInfo.InvariantCulture),
            [SettingKeys.CutoffTime] = BusinessCalendar.FormatTime(cutoff),
            [SettingKeys.DeferredSendTime] = BusinessCalendar.FormatTime(sendAt),
            [SettingKeys.AutoStart] = AutoStart.IsChecked == true ? "1" : "0",
            [SettingKeys.Holidays] = BusinessCalendar.FormatHolidays(holidays),
        };

        try
        {
            AppDb.SetSettings(values);
        }
        catch (Exception ex)
        {
            Ui.Error("Could not save the settings: " + ex.Message);
            return;
        }

        bool published = Session.SyncTeamFileFromDb();
        WorkbookStore.MarkDirty();
        _loaded = false;
        Load();
        StatusText.Text = $"Saved at {DateTime.Now:HH:mm:ss}" + (published ? " · team file updated" : " · the shared team file could not be updated");
        _ = MainWindow.Instance?.RefreshDataAsync(force: true);
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        Load();
        StatusText.Text = "Reverted to the saved values.";
    }

    private void TestMail_Click(object sender, RoutedEventArgs e)
    {
        string to = ManagerEmail.Text.Trim();
        if (to.Length == 0)
        {
            Ui.Info("Enter the manager e-mail first; the test message is sent there.");
            return;
        }
        try
        {
            using var mailer = new OutlookMailer(SenderAccount.Text.Trim());
            bool ok = mailer.SendFile(to, "SWIFT Batch Processor — test message",
                $"This is a test message sent by SWIFT Batch Processor v{App.Version} from {Environment.MachineName}.\r\n", null);
            if (ok) Ui.Info($"Test message sent to {to}.");
            else Ui.Warn("Outlook could not send the message:\n\n" + mailer.LastError);
        }
        catch (Exception ex)
        {
            Ui.Warn("Outlook is not available on this PC:\n\n" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- browse

    private void BrowseWatch_Click(object sender, RoutedEventArgs e) => BrowseFolder(WatchFolder, "Choose the watch folder");

    private void BrowseArchive_Click(object sender, RoutedEventArgs e) => BrowseFolder(ArchiveRoot, "Choose the archive root");

    private void BrowseFolder(TextBox target, string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (Directory.Exists(target.Text.Trim())) dlg.InitialDirectory = target.Text.Trim();
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) target.Text = dlg.FolderName;
    }

    private void BrowseRegistry_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Registry workbook (created automatically if it does not exist)",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = Path.GetFileName(RegistryPath.Text.Trim()) is { Length: > 0 } n ? n : "Registry.xlsx",
            OverwritePrompt = false,
        };
        string? dir = Path.GetDirectoryName(RegistryPath.Text.Trim());
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) RegistryPath.Text = dlg.FileName;
    }

    private void BrowseMaster_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Master customer file", Filter = "Excel workbook (*.xlsx)|*.xlsx" };
        string? dir = Path.GetDirectoryName(MasterFilePath.Text.Trim());
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) MasterFilePath.Text = dlg.FileName;
    }

    // ---------------------------------------------------------------- holidays

    private void AddGreek_Click(object sender, RoutedEventArgs e) =>
        AddHolidays(BusinessCalendar.GreekBankHolidays(SelectedYear()), "Greek bank holidays");

    private void AddTarget2_Click(object sender, RoutedEventArgs e) =>
        AddHolidays(BusinessCalendar.Target2Holidays(SelectedYear()), "TARGET2 closing days");

    private int SelectedYear() => HolidayYear.SelectedItem is int y ? y : DateTime.Today.Year;

    private void AddHolidays(IEnumerable<(DateTime Day, string Name)> days, string what)
    {
        HashSet<string> set = BusinessCalendar.ParseHolidays(Holidays.Text);
        var list = days.ToList();
        int added = list.Count(d => set.Add(BusinessCalendar.Key(d.Day)));
        Holidays.Text = string.Join(Environment.NewLine, set.OrderBy(h => h));
        HolidayInfo.Text = $"{added} {what} {SelectedYear()} added: " +
                           string.Join(", ", list.Select(d => $"{d.Day:dd/MM} {d.Name}")) + ". Remember to save.";
    }

    private void CleanHolidays_Click(object sender, RoutedEventArgs e)
    {
        Holidays.Text = string.Join(Environment.NewLine, BusinessCalendar.ParseHolidays(Holidays.Text).OrderBy(h => h));
        UpdateHolidayInfo();
    }

    private void UpdateHolidayInfo()
    {
        var set = BusinessCalendar.ParseHolidays(Holidays.Text);
        string next = set.Where(h => string.CompareOrdinal(h, BusinessCalendar.Key(DateTime.Today)) >= 0).OrderBy(h => h).FirstOrDefault() ?? "none";
        HolidayInfo.Text = $"{set.Count} holiday(s) · next: {next}";
    }
}
