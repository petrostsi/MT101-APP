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
        LocalConfig.UseFolder(_tmp.Dir("pc"));
        AppDb.Initialize(_tmp["s.db"]);
        _tmp.Dir("share");
        LocalConfig.RegistryPath = _tmp[Path.Combine("share", "Registry.xlsx")];
        AppDb.SetSetting(SettingKeys.ManagerEmail, "boss@x");
        AppDb.SetSetting(SettingKeys.ArchiveRoot, _tmp["share"]);
        AppDb.SaveUser(new AppUser { Email = "alice@x", DisplayName = "Alice", WindowsUser = "alice" });
        AppDb.SaveUser(new AppUser { Email = "bob@x", DisplayName = "Bob" });
    }

    public void Dispose()
    {
        Session.WindowsUserProvider = () => Environment.UserName;
        Session.Reset();
        Session.Defaults = null;
        LocalConfig.UseFolder(Path.Combine(Path.GetTempPath(), "swiftbatch-tests"));
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
        LocalConfig.RegistryPath = _tmp[Path.Combine("missing", "Registry.xlsx")];
        _windowsUser = "someone";
        Assert.Equal(ResolveOutcome.Resolved, Session.Resolve());
        Assert.False(Session.IsManager);
        Assert.Contains("not reachable", Session.Notice);
    }

    // ---------------------------------------------------------------- shared database (v2.5)

    [Fact]
    public void Manager_keeps_the_database_in_the_shared_folder_next_to_the_registry()
    {
        AppDb.Close();
        Session.Defaults = new DefaultsFile
        {
            Settings = new(StringComparer.OrdinalIgnoreCase) { [SettingKeys.ManagerEmail] = "boss@x" },
            Users = { new DefaultsFile.DefaultUser { Email = "carol@x" } },
        };

        Assert.Equal(ResolveOutcome.Resolved, Session.Resolve());   // first PC: claims manager

        string shared = _tmp[Path.Combine("share", AppDb.FileName)];
        Assert.True(Session.IsManager);
        Assert.Equal(shared, AppDb.DbPath);
        Assert.True(File.Exists(shared));
        Assert.Equal("boss@x", Session.MyEmail);
        Assert.Equal(LocalConfig.RegistryPath, AppDb.GetSetting(SettingKeys.RegistryPath));
        Assert.Contains(TeamFile.Load(Session.TeamFilePath).Users, u => u.Email == "carol@x");
    }

    [Fact]
    public void User_pcs_never_open_the_database()
    {
        Session.Resolve();                                          // boss sets everything up
        AppDb.Close();
        _windowsUser = "alice";

        Assert.Equal(ResolveOutcome.Resolved, Session.Resolve());
        Assert.False(Session.IsManager);
        Assert.False(AppDb.IsInitialized);
        Assert.Equal(_tmp["share"], Session.ArchiveRoot);           // from the team file
    }

    [Fact]
    public void Unreachable_share_leaves_the_manager_without_a_database_and_says_why()
    {
        Session.Resolve();
        AppDb.Close();
        LocalConfig.RegistryPath = _tmp[Path.Combine("offline", "Registry.xlsx")];

        Session.Resolve();                                          // cached manager account

        Assert.True(Session.IsManager);
        Assert.False(AppDb.IsInitialized);
        Assert.Contains("not reachable", Session.DatabaseError);
    }

    [Fact]
    public void A_pre_v2_5_local_database_moves_to_the_share_with_its_history()
    {
        // An old install: SwiftBatch.db next to the exe with settings, identity and sent files.
        string pc = _tmp.Dir("oldpc");
        AppDb.Initialize(Path.Combine(pc, LocalConfig.LegacyDatabaseName));
        AppDb.SetSetting(SettingKeys.RegistryPath, _tmp[Path.Combine("share", "Registry.xlsx")]);
        AppDb.SetSetting("ManagerWindowsUser", "boss");
        AppDb.SetSetting(SettingKeys.ManagerEmail, "boss@x");
        AppDb.RecordFile(new FileRecord { FileName = "00660001.prt", AssignedUser = "alice@x", ProcessedDate = "2026-09-01" },
            Array.Empty<PaymentOrder>());
        AppDb.Close();

        LocalConfig.UseFolder(pc);
        LocalConfig.Load(null);
        Assert.Equal(_tmp[Path.Combine("share", "Registry.xlsx")], LocalConfig.RegistryPath);
        Assert.Equal("boss", LocalConfig.ManagerWindowsUser);
        Assert.False(File.Exists(LocalConfig.LegacyDatabasePath));   // nothing business-related stays on the PC
        Assert.True(File.Exists(LocalConfig.LegacyBackupPath));

        Session.Resolve();

        Assert.True(Session.IsManager);
        Assert.Equal(_tmp[Path.Combine("share", AppDb.FileName)], AppDb.DbPath);
        Assert.True(AppDb.IsFileProcessedOrQueued("00660001.prt")); // duplicate protection survived the move
    }

    [Fact]
    public void Local_config_is_seeded_from_the_defaults_file()
    {
        string registry = _tmp[Path.Combine("server", "TestRegistry.xlsx")];
        LocalConfig.UseFolder(_tmp.Dir("newpc"));
        LocalConfig.Load(new DefaultsFile
        {
            Settings = new(StringComparer.OrdinalIgnoreCase) { [SettingKeys.RegistryPath] = registry },
        });
        Assert.Equal(registry, LocalConfig.RegistryPath);
        Assert.Equal(_tmp["server"], LocalConfig.SharedFolder);
        Assert.Equal(Path.Combine(_tmp["server"], AppDb.FileName), LocalConfig.SharedDatabasePath);
        Assert.True(File.Exists(LocalConfig.FilePath));

        LocalConfig.MyEmail = "me@x";
        LocalConfig.UseFolder(_tmp["newpc"]);
        LocalConfig.Load(null);
        Assert.Equal("me@x", LocalConfig.MyEmail);
        Assert.Equal(registry, LocalConfig.RegistryPath);
    }

    [Fact]
    public void Engine_guard_follows_the_team_file()
    {
        Session.Resolve();
        Assert.True(Session.VerifyStillManager());

        TeamFile team = TeamFile.Load(Session.TeamFilePath);
        team.ManagerWindowsUser = "someone-else";
        team.Save(Session.TeamFilePath);
        Assert.False(Session.VerifyStillManager());
    }
}
