using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class UsersView : UserControl, IRefreshable
{
    public UsersView() => InitializeComponent();

    public sealed class UserRow
    {
        public required AppUser User { get; init; }
        public int LoadToday { get; init; }
        public string Email => User.Email;
        public string DisplayName => User.DisplayName;
        public string WindowsUser => User.WindowsUser;
        public bool IsActive => User.IsActive;
        public bool OooToday => User.IsOooOn(DateTime.Today);
        public string ActiveText => IsActive ? "Active" : "Inactive";
        public string ActiveKey => IsActive ? "Completed" : "Stopped";
        public string TodayText => !IsActive ? "—" : OooToday ? "Out of office" : "Available";
        public string TodayKey => !IsActive ? "Stopped" : OooToday ? "ESCALATED" : "Completed";
    }

    public void Refresh()
    {
        long? keep = (UsersGrid.SelectedItem as UserRow)?.User.Id;
        Dictionary<string, int> load = AppDb.GetPaymentCounts(WorkbookLayout.DateKey(DateTime.Today));
        List<UserRow> rows = AppDb.GetUsers().Select(u => new UserRow { User = u, LoadToday = load.GetValueOrDefault(u.Email) }).ToList();
        UsersGrid.ItemsSource = rows;
        if (keep is not null) UsersGrid.SelectedItem = rows.FirstOrDefault(r => r.User.Id == keep);

        int available = rows.Count(r => r.IsActive && !r.OooToday);
        CountText.Text = $"{Ui.Plural(rows.Count, "user")} · {available} available today";
        ManagerText.Text = $"Manager (runs the engine, receives escalations and unmatched customers): {Session.ManagerLabel}";
        TeamFileText.Text = $"Team file shared with the users' PCs: {Session.TeamFilePath}" +
                            (Session.Team?.UpdatedAt is { Length: > 0 } at ? $" · last written {at}" : "");
    }

    private UserRow? Selected(bool warn = true)
    {
        if (UsersGrid.SelectedItem is UserRow r) return r;
        if (warn) Ui.Info("Select a user first.");
        return null;
    }

    private void SaveAndPublish(Action change)
    {
        try
        {
            change();
        }
        catch (Exception ex)
        {
            Ui.Error("Could not save: " + ex.Message);
        }
        if (!Session.SyncTeamFileFromDb())
            Ui.Warn("Saved locally, but the shared team file could not be updated:\n\n" + Session.Notice);
        Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new UserEditDialog(null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) SaveAndPublish(() => AppDb.SaveUser(dlg.Result));
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row) return;
        var dlg = new UserEditDialog(row.User) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) SaveAndPublish(() => AppDb.SaveUser(dlg.Result));
    }

    private void UsersGrid_DoubleClick(object sender, MouseButtonEventArgs e) => Edit_Click(sender, e);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row) return;
        if (!Ui.Confirm($"Delete {row.Email}?\n\nTheir past files stay in the registry. To stop assigning files temporarily, deactivate instead.")) return;
        SaveAndPublish(() => AppDb.DeleteUser(row.User.Id));
    }

    private void ToggleActive_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row) return;
        row.User.IsActive = !row.User.IsActive;
        SaveAndPublish(() => AppDb.SaveUser(row.User));
    }

    private void ToggleOoo_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } row) return;
        string today = DateTime.Today.ToString("yyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            AppDb.SetOoo(row.Email, row.OooToday ? "" : today);
        }
        catch (Exception ex)
        {
            Ui.Error(ex.Message);
        }
        Refresh();
    }
}
