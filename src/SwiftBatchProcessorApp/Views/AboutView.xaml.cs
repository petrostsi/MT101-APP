using System.Windows;
using System.Windows.Controls;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Views;

public partial class AboutView : UserControl, IRefreshable
{
    public AboutView() => InitializeComponent();

    public void Refresh()
    {
        VersionText.Text = $"Version {App.Version} · .NET {Environment.Version} · no admin rights required";
        string registry = WorkbookStore.RegistryPath;
        EnvironmentText.Text = string.Join(Environment.NewLine,
            $"Role              {(Session.IsManager ? "Manager (runs the engine)" : "User")}",
            $"Identity          {(Session.MyEmail.Length > 0 ? Session.MyEmail : "(not set)")}",
            $"Windows user      {Session.WindowsUser} on {Environment.MachineName}",
            $"Manager           {Session.ManagerLabel}",
            $"Program folder    {App.AppFolder}",
            $"Local database    {AppDb.DbPath}",
            $"Registry          {registry}",
            $"Team file         {Session.TeamFilePath}",
            $"Archive root      {WorkbookStore.ArchiveRoot}",
            $"Log folder        {App.LogFolder}",
            $"Outlook           {(OutlookMailer.IsAvailable() ? "installed" : "not found")}");
    }

    private void OpenAppFolder_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(App.AppFolder);

    private void OpenRegistryFolder_Click(object sender, RoutedEventArgs e) =>
        Ui.OpenFolder(Path.GetDirectoryName(WorkbookStore.RegistryPath) ?? "");

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.LogFolder);
        Ui.OpenFolder(App.LogFolder);
    }
}
