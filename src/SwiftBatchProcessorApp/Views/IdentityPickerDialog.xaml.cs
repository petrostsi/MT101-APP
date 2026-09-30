using System.Windows;
using System.Windows.Input;
using SwiftBatchApp.Core;

namespace SwiftBatchApp.Views;

/// <summary>First run on a PC (or "Switch account"): the user picks themselves from the team, or claims manager.</summary>
public partial class IdentityPickerDialog : Window
{
    public IdentityPickerDialog(bool allowCancel)
    {
        InitializeComponent();
        var users = Session.PickableUsers();
        UserList.ItemsSource = users;
        TeamMember? current = users.FirstOrDefault(u => string.Equals(u.Email, Session.MyEmail, StringComparison.OrdinalIgnoreCase));
        if (current is not null) UserList.SelectedItem = current;

        bool teamAvailable = Session.Team is not null;
        ManualPanel.Visibility = teamAvailable ? Visibility.Collapsed : Visibility.Visible;
        IntroText.Text = teamAvailable
            ? $"Pick yourself from the team list. This is remembered for the Windows account \"{Session.WindowsUser}\" on this PC."
            : $"The shared team list could not be read ({Session.Notice}). Type your e-mail address to continue.";
        CancelButton.Content = allowCancel ? "Cancel" : "Exit";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string email = (UserList.SelectedItem as TeamMember)?.Email ?? ManualEmail.Text.Trim();
        if (email.Length == 0 || !email.Contains('@'))
        {
            Ui.Info("Select your name in the list first.");
            return;
        }
        if (Session.IsManager &&
            !Ui.Confirm($"This Windows account is currently the manager.\n\nContinuing as {email} removes the manager rights, " +
                        "and the engine will not run until someone claims manager again.\n\nContinue?"))
            return;

        Session.SetIdentity(email);
        DialogResult = true;
    }

    private void ClaimManager_Click(object sender, RoutedEventArgs e)
    {
        string current = Session.Team?.ManagerWindowsUser ?? "";
        string warning = current.Length > 0 && !string.Equals(current, Session.WindowsUser, StringComparison.OrdinalIgnoreCase)
            ? $"The manager is currently the Windows account \"{current}\". It will lose manager rights and its engine will stop distributing files.\n\n"
            : "";
        if (!Ui.Confirm(warning + $"Make \"{Session.WindowsUser}\" the manager?")) return;

        Session.ClaimManagerOnThisMachine();
        if (Session.Notice.Length > 0) Ui.Warn(Session.Notice);
        DialogResult = true;
    }

    private void UserList_DoubleClick(object sender, MouseButtonEventArgs e) => Ok_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
