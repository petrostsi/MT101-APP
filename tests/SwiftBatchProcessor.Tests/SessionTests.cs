using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Tests;

public class SessionTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private string _windowsUser = "boss";

    public SessionTests()
    {
        Session.Reset();
        Session.WindowsUserProvider = () => _windowsUser;
        AppDb.Initialize(_tmp["s.db"]);
        _tmp.Dir("share");
        AppDb.SetSetting(SettingKeys.RegistryPath, _tmp[Path.Combine("share", "Registry.xlsx")]);
        AppDb.SetSetting(SettingKeys.ManagerEmail, "boss@x");
        AppDb.SetSetting(SettingKeys.ArchiveRoot, _tmp["share"]);
        AppDb.SaveUser(new AppUser { Email = "alice@x", DisplayName = "Alice", WindowsUser = "alice" });
        AppDb.SaveUser(new AppUser { Email = "bob@x", DisplayName = "Bob" });
    }

    public void Dispose()
    {
        Session.WindowsUserProvider = () => Environment.UserName;
        Session.Reset();
        _tmp.Dispose();
    }

    [Fact]
    public void First_pc_without_a_team_file_claims_manager_and_publishes_the_team()
    {
        Assert.Equal(ResolveOutcome.Resolved, Session.Resolve());
        Assert.True(Session.IsManager);
        Assert.Equal("boss@x", Session.MyEmail);

        TeamFile team = TeamFile.Load(Session.TeamFilePath);
        Assert.Equal("boss", team.ManagerWindowsUser);
        Assert.Equal(_tmp["share"], team.ArchiveRoot);
        Assert.Equal(new[] { "alice@x", "bob@x" }, team.Users.Select(u => u.Email));
    }

    [Fact]
    public void Users_are_recognised_by_windows_account_or_pick_themselves_once()
    {
        Session.Resolve();                                   // boss creates the team file

        _windowsUser = "alice";
        Assert.Equal(ResolveOutcome.Resolved, Session.Resolve());
        Assert.False(Session.IsManager);
        Assert.Equal("alice@x", Session.MyEmail);

        _windowsUser = "bob-laptop";
        Assert.Equal(ResolveOutcome.NeedsIdentity, Session.Resolve());
        Assert.Contains(Session.PickableUsers(), u => u.Email == "bob@x");
        Session.SetIdentity("bob@x");
        Assert.Equal(ResolveOutcome.Resolved, Session.Resolve());
        Assert.Equal("bob@x", Session.MyEmail);
        Assert.False(Session.IsManager);
    }

    [Fact]
    public void Claiming_manager_moves_the_role_to_this_windows_account()
    {
        Session.Resolve();
        _windowsUser = "alice";
        Session.Resolve();
        Session.ClaimManagerOnThisMachine();
        Assert.True(Session.IsManager);
        Assert.Equal("alice", TeamFile.Load(Session.TeamFilePath).ManagerWindowsUser);

        _windowsUser = "boss";
        Session.Resolve();
        Assert.False(Session.IsManager);                     // the old manager account is now a normal user
    }

    [Fact]
    public void Existing_registry_without_team_file_never_claims_manager_silently()
    {
        File.WriteAllText(_tmp[Path.Combine("share", "Registry.xlsx")], "placeholder");
        _windowsUser = "someone";

        Assert.Equal(ResolveOutcome.NeedsIdentity, Session.Resolve());
        Assert.False(Session.IsManager);
        Assert.False(File.Exists(Session.TeamFilePath));
        Assert.Contains("team file", Session.Notice);
    }

    [Fact]
    public void Manager_recreates_a_deleted_team_file()
    {
        Session.Resolve();                                          // boss claims, team file written
        File.WriteAllText(_tmp[Path.Combine("share", "Registry.xlsx")], "placeholder");
        File.Delete(Session.TeamFilePath);

        Assert.Equal(ResolveOutcome.Resolved, Session.Resolve());   // cached manager account
        Assert.True(Session.IsManager);
        Assert.True(File.Exists(Session.TeamFilePath));
        Assert.Equal("", Session.Notice);
    }

    [Fact]
    public void Claiming_on_a_new_pc_keeps_the_team_list()
    {
        Session.Resolve();                                          // boss publishes alice + bob
        foreach (AppUser u in AppDb.GetUsers()) AppDb.DeleteUser(u.Id);   // a fresh DB on another PC
        _windowsUser = "newboss";
        Session.Resolve();
        Session.ClaimManagerOnThisMachine();

        Assert.Equal(new[] { "alice@x", "bob@x" }, AppDb.GetUsers().Select(u => u.Email));
        Assert.Equal("alice", AppDb.GetUser("alice@x")!.WindowsUser);
        Assert.Equal(2, TeamFile.Load(Session.TeamFilePath).Users.Count);
    }

    [Fact]
    public void Unreachable_share_resolves_to_a_user_with_a_notice()
    {
        AppDb.SetSetting(SettingKeys.RegistryPath, _tmp[Path.Combine("missing", "Registry.xlsx")]);
        _windowsUser = "someone";
        Assert.Equal(ResolveOutcome.Resolved, Session.Resolve());
        Assert.False(Session.IsManager);
        Assert.Contains("not reachable", Session.Notice);
    }
}
