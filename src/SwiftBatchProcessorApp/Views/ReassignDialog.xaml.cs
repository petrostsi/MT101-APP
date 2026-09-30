using System.Windows;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class ReassignDialog : Window
{
    public ReassignDialog(RegistryEntry entry)
    {
        InitializeComponent();
        TitleText.Text = $"Reassign {entry.FileName}";
        InfoText.Text = $"{entry.Orders} payment order(s) · {entry.DisplayStatus} · currently {entry.AssignedUser}";

        string manager = AppDb.GetSetting(SettingKeys.ManagerEmail);
        var choices = AppDb.GetUsers().Where(u => u.IsActive).ToList();
        if (manager.Length > 0 && !choices.Any(u => string.Equals(u.Email, manager, StringComparison.OrdinalIgnoreCase)))
            choices.Add(new AppUser { Email = manager, DisplayName = "Manager" });
        UserCombo.ItemsSource = choices.Where(u => !string.Equals(u.Email, entry.AssignedUser, StringComparison.OrdinalIgnoreCase)).ToList();

        bool archived = WorkbookStore.ArchivedFilePath(entry).Length > 0;
        bool outlook = OutlookMailer.IsAvailable();
        SendMailCheck.IsEnabled = archived && outlook;
        SendMailCheck.IsChecked = archived && outlook;
        MailHint.Text = !outlook ? "Outlook is not installed on this PC."
            : !archived ? "The archived .prt file was not found, so it cannot be attached."
            : "Sent without an out-of-office check.";
    }

    public string? NewUser { get; private set; }
    public bool SendEmail { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (UserCombo.SelectedItem is not AppUser user)
        {
            Ui.Info("Choose the new assignee.");
            return;
        }
        NewUser = user.Email;
        SendEmail = SendMailCheck.IsChecked == true;
        DialogResult = true;
    }
}
