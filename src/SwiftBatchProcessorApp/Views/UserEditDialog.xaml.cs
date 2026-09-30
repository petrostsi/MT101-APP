using System.Windows;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class UserEditDialog : Window
{
    private readonly AppUser? _existing;

    public UserEditDialog(AppUser? existing)
    {
        InitializeComponent();
        _existing = existing;
        TitleText.Text = existing is null ? "Add user" : $"Edit {existing.Email}";
        if (existing is not null)
        {
            EmailBox.Text = existing.Email;
            NameBox.Text = existing.DisplayName;
            WindowsBox.Text = existing.WindowsUser;
            ActiveCheck.IsChecked = existing.IsActive;
        }
        Loaded += (_, _) => EmailBox.Focus();
    }

    public AppUser Result { get; private set; } = new();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string email = EmailBox.Text.Trim();
        if (email.Length < 3 || !email.Contains('@') || email.Contains(' '))
        {
            Ui.Info("Enter a valid e-mail address.");
            return;
        }
        AppUser? clash = AppDb.GetUser(email);
        if (clash is not null && clash.Id != _existing?.Id)
        {
            Ui.Info($"{email} already exists.");
            return;
        }
        string windowsUser = WindowsBox.Text.Trim();
        if (windowsUser.Contains('\\')) windowsUser = windowsUser[(windowsUser.LastIndexOf('\\') + 1)..];   // DOMAIN\user → user

        Result = new AppUser
        {
            Id = _existing?.Id ?? 0,
            Email = email,
            DisplayName = NameBox.Text.Trim(),
            WindowsUser = windowsUser,
            IsActive = ActiveCheck.IsChecked == true,
            OooDate = _existing?.OooDate ?? "",
        };
        DialogResult = true;
    }
}
