using System.Text;
using System.Text.Json;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Core;

public enum AppRole { User, Manager }

public enum ResolveOutcome { Resolved, NeedsIdentity }

/// <summary>
/// Shared team file <c>AppUsers.json</c>, stored next to Registry.xlsx. Written by the manager PC,
/// read by every PC: it tells user PCs who is who and where the archive lives.
/// </summary>
public sealed class TeamFile
{
    public string ManagerWindowsUser { get; set; } = "";
    public string ManagerEmail { get; set; } = "";
    public string ManagerName { get; set; } = "";
    public string ArchiveRoot { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public List<TeamMember> Users { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static TeamFile Load(string path) =>
        JsonSerializer.Deserialize<TeamFile>(Encoding.UTF8.GetString(ExcelReader.ReadAllBytesShared(path)), Json) ?? new TeamFile();

    public void Save(string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json));
        ExcelWriter.WithRetries(() =>
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(bytes, 0, bytes.Length);
        });
    }
}

public sealed class TeamMember
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string WindowsUser { get; set; } = "";
    public bool IsActive { get; set; } = true;

    public string Label => DisplayName.Length > 0 ? $"{DisplayName} <{Email}>" : Email;
}

/// <summary>
/// Who is using this PC. Roles come from the Windows logon only (no PIN, by design):
///  * Manager — the Windows account named in the team file (the first PC that finds no team file claims it).
///    Sees everything, is the ONLY PC that runs the engine and the only one that opens the shared database.
///  * User — mapped by Windows account in the team file, or self-selected once (IdentityPicker). Own work only;
///    reads the shared Excel workbooks and never opens the database.
/// Per-PC facts (shared-folder location, chosen identity) live in <see cref="LocalConfig"/>.
/// </summary>
public static class Session
{
    public const string TeamFileName = "AppUsers.json";

    /// <summary>Overridable in tests.</summary>
    internal static Func<string> WindowsUserProvider = () => Environment.UserName;

    public static string WindowsUser => WindowsUserProvider();
    public static AppRole Role { get; private set; } = AppRole.User;
    public static bool IsManager => Role == AppRole.Manager;
    public static string MyEmail { get; private set; } = "";
    public static TeamFile? Team { get; private set; }
    public static bool SharedFolderReachable { get; private set; }

    /// <summary>Banner text when something needs attention (unreachable share, unreadable team file…).</summary>
    public static string Notice { get; private set; } = "";

    /// <summary>Why the manager could not open the shared database ("" when it is open or not needed).</summary>
    public static string DatabaseError { get; private set; } = "";

    /// <summary>Seeds a brand-new shared database (set by the app from SwiftBatch.defaults.json).</summary>
    public static DefaultsFile? Defaults { get; set; }

    public static event Action? Changed;

    public static string TeamFilePath =>
        LocalConfig.SharedFolder is { Length: > 0 } dir ? Path.Combine(dir, TeamFileName) : "";

    /// <summary>Archive root for reading day workbooks: the manager's setting, else the team file, else the shared folder.</summary>
    public static string ArchiveRoot
    {
        get
        {
            if (IsManager && AppDb.IsInitialized && AppDb.GetSetting(SettingKeys.ArchiveRoot) is { Length: > 0 } configured) return configured;
            if (Team?.ArchiveRoot is { Length: > 0 } shared) return shared;
            return LocalConfig.SharedFolder;
        }
    }

    public static string MyDisplayName
    {
        get
        {
            if (IsManager)
            {
                string name = AppDb.IsInitialized ? AppDb.GetSetting(SettingKeys.ManagerName) : Team?.ManagerName ?? "";
                return name.Length > 0 ? name : MyEmail;
            }
            return Team?.Users.FirstOrDefault(u => Same(u.Email, MyEmail))?.DisplayName is { Length: > 0 } d ? d : MyEmail;
        }
    }

    public static string ManagerLabel
    {
        get
        {
            if (Team is null) return "(unknown — shared folder not reachable)";
            if (Team.ManagerWindowsUser.Length == 0) return "(none — claim manager from Switch account)";
            string who = Team.ManagerName.Length > 0 ? Team.ManagerName : Team.ManagerEmail;
            return $"{who} (Windows: {Team.ManagerWindowsUser})";
        }
    }

    public static IReadOnlyList<TeamMember> PickableUsers() =>
        Team?.Users.Where(u => u.IsActive && u.Email.Length > 0).OrderBy(u => u.Label, StringComparer.OrdinalIgnoreCase).ToList()
        ?? new List<TeamMember>();

    // ---------------------------------------------------------------- resolution

    public static ResolveOutcome Resolve()
    {
        string me = WindowsUser;
        Team = null;
        Notice = "";
        DatabaseError = "";
        string registry = LocalConfig.RegistryPath;
        string? dir = registry.Length > 0 ? Path.GetDirectoryName(registry) : null;
        SharedFolderReachable = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);

        if (SharedFolderReachable)
        {
            string path = TeamFilePath;
            try
            {
                if (File.Exists(path)) Team = TeamFile.Load(path);
                else if (!File.Exists(registry))
                {
                    // Brand-new setup (no registry, no team file): the first PC to get here becomes the manager.
                    ClaimManagerOnThisMachine();
                    return ResolveOutcome.Resolved;
                }
                else
                {
                    // The registry exists but the team file does not: never claim silently (a network hiccup
                    // must not turn a user PC into the manager). The picker offers "Claim manager".
                    Notice = $"The team file {TeamFileName} was not found next to the registry. " +
                             "The manager should open the app once (or use Claim manager).";
                }
            }
            catch (Exception ex)
            {
                Notice = $"Cannot read the team file {path}: {ex.Message}";
            }
        }
        else
        {
            Notice = registry.Length == 0
                ? "The registry path is not set. Use \"Locate registry…\" to point the app at Registry.xlsx."
                : $"The shared folder is not reachable: {dir}";
        }

        string cachedManager = LocalConfig.ManagerWindowsUser;
        string managerAccount = Team?.ManagerWindowsUser ?? cachedManager;
        bool teamMissing = SharedFolderReachable && Team is null;
        if (managerAccount.Length > 0 && Same(managerAccount, me))
        {
            BecomeManager(me);
            if (Team is not null) ImportUsersFromTeam(addMissing: false);
            else if (teamMissing && SyncTeamFileFromDb()) Notice = "";          // the manager restores a deleted team file
            Raise();
            return ResolveOutcome.Resolved;
        }
        if (Team is not null && cachedManager.Length > 0)
            LocalConfig.ManagerWindowsUser = "";                    // manager moved to another PC/account

        Role = AppRole.User;
        string chosen = LocalConfig.MyEmail;
        if (chosen.Length > 0 && (Team is null || Team.Users.Any(u => Same(u.Email, chosen))))
        {
            MyEmail = chosen;
            Raise();
            return ResolveOutcome.Resolved;
        }
        TeamMember? mapped = Team?.Users.FirstOrDefault(u => u.WindowsUser.Length > 0 && Same(u.WindowsUser, me));
        if (mapped is not null)
        {
            MyEmail = mapped.Email;
            Raise();
            return ResolveOutcome.Resolved;
        }

        MyEmail = "";
        Raise();
        return Team is null && !teamMissing ? ResolveOutcome.Resolved : ResolveOutcome.NeedsIdentity;
    }

    /// <summary>This PC's user picks who they are (remembered locally).</summary>
    public static void SetIdentity(string email)
    {
        if (IsManager) ReleaseManager();
        LocalConfig.MyEmail = email.Trim();
        Role = AppRole.User;
        MyEmail = email.Trim();
        Raise();
    }

    /// <summary>Forget the local identity choice (used by "Switch account" before relaunching).</summary>
    public static void ClearIdentity() => LocalConfig.MyEmail = "";

    /// <summary>Makes the current Windows account the manager (recovery path; no PIN by design).</summary>
    public static void ClaimManagerOnThisMachine()
    {
        string me = WindowsUser;
        BecomeManager(me);
        if (Team is not null) ImportUsersFromTeam(addMissing: true);   // e.g. a new manager PC with an empty DB
        Team ??= new TeamFile();
        Team.ManagerWindowsUser = me;
        SyncTeamFileFromDb();
        Raise();
    }

    /// <summary>Manager: publishes users and shared settings to the team file (best effort).</summary>
    public static bool SyncTeamFileFromDb()
    {
        if (!IsManager || !AppDb.IsInitialized) return false;
        string path = TeamFilePath;
        if (path.Length == 0 || !Directory.Exists(Path.GetDirectoryName(path))) return false;
        try
        {
            var settings = AppDb.LoadSettings();
            Team ??= File.Exists(path) ? TeamFile.Load(path) : new TeamFile();
            Team.ManagerWindowsUser = WindowsUser;
            Team.ManagerEmail = settings.ManagerEmail;
            Team.ManagerName = settings.ManagerName;
            Team.ArchiveRoot = settings.ArchiveRoot;
            Team.UpdatedAt = WorkbookLayout.Timestamp(DateTime.Now);
            Team.UpdatedBy = WindowsUser;
            Team.Users = AppDb.GetUsers().Select(u => new TeamMember
            {
                Email = u.Email, DisplayName = u.DisplayName, WindowsUser = u.WindowsUser, IsActive = u.IsActive,
            }).ToList();
            Team.Save(path);
            SharedFolderReachable = true;
            return true;
        }
        catch (Exception ex)
        {
            Notice = $"Could not update the team file: {ex.Message}";
            return false;
        }
    }

    // ---------------------------------------------------------------- internals

    private static void BecomeManager(string me)
    {
        Role = AppRole.Manager;
        LocalConfig.ManagerWindowsUser = me;
        OpenSharedDatabase();
        MyEmail = AppDb.IsInitialized ? AppDb.GetSetting(SettingKeys.ManagerEmail) : "";
        if (MyEmail.Length == 0 && Team is not null) MyEmail = Team.ManagerEmail;
    }

    /// <summary>
    /// Manager only: opens SwiftBatch.db in the shared folder (created on first use, seeded from the defaults file).
    /// A pre-v2.5 database found next to the exe is moved there first, keeping its history.
    /// </summary>
    private static void OpenSharedDatabase()
    {
        if (AppDb.IsInitialized) return;
        string path = LocalConfig.SharedDatabasePath;
        if (path.Length == 0)
        {
            DatabaseError = "The shared folder is not set (registry path is empty).";
            return;
        }
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                throw new DirectoryNotFoundException($"The shared folder is not reachable: {folder}");
            if (!File.Exists(path) && File.Exists(LocalConfig.LegacyBackupPath))
                File.Copy(LocalConfig.LegacyBackupPath, path);          // bring the old local history along
            AppDb.Initialize(path, Defaults);
            if (!string.Equals(AppDb.GetSetting(SettingKeys.RegistryPath), LocalConfig.RegistryPath, StringComparison.OrdinalIgnoreCase))
                AppDb.SetSetting(SettingKeys.RegistryPath, LocalConfig.RegistryPath);
            DatabaseError = "";
        }
        catch (Exception ex)
        {
            DatabaseError = $"The shared database {path} could not be opened: {ex.Message}";
            Notice = DatabaseError;
        }
    }

    /// <summary>
    /// Engine guard, checked before every cycle: false when the team file now names another manager
    /// (someone claimed manager elsewhere) — two engines would e-mail files twice and share one database.
    /// An unreadable team file does not stop the engine (the share may just be slow).
    /// </summary>
    public static bool VerifyStillManager()
    {
        try
        {
            string path = TeamFilePath;
            if (path.Length == 0 || !File.Exists(path)) return true;
            string manager = TeamFile.Load(path).ManagerWindowsUser;
            return manager.Length == 0 ? false : Same(manager, WindowsUser);
        }
        catch
        {
            return true;
        }
    }

    private static void ReleaseManager()
    {
        LocalConfig.ManagerWindowsUser = "";
        if (Team is null || TeamFilePath.Length == 0) return;
        try
        {
            Team.ManagerWindowsUser = "";
            Team.UpdatedAt = WorkbookLayout.Timestamp(DateTime.Now);
            Team.UpdatedBy = WindowsUser;
            Team.Save(TeamFilePath);
        }
        catch (Exception ex)
        {
            Notice = $"Could not update the team file: {ex.Message}";
        }
    }

    /// <summary>
    /// The manager's DB is the master user list; the team file may know more (Windows-account mappings, or all
    /// users when a new manager PC starts with an empty DB). Missing mappings are always taken over; missing users
    /// only when <paramref name="addMissing"/> (claiming manager).
    /// </summary>
    private static void ImportUsersFromTeam(bool addMissing)
    {
        if (Team is null || !AppDb.IsInitialized) return;
        List<AppUser> users = AppDb.GetUsers();
        foreach (TeamMember m in Team.Users.Where(m => m.Email.Length > 0))
        {
            AppUser? u = users.FirstOrDefault(x => Same(x.Email, m.Email));
            if (u is null)
            {
                if (addMissing)
                    AppDb.SaveUser(new AppUser { Email = m.Email, DisplayName = m.DisplayName, WindowsUser = m.WindowsUser, IsActive = m.IsActive });
            }
            else if (u.WindowsUser.Length == 0 && m.WindowsUser.Length > 0)
            {
                u.WindowsUser = m.WindowsUser;
                AppDb.SaveUser(u);
            }
        }
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void Raise() => Changed?.Invoke();

    /// <summary>Test hook: resets the static state.</summary>
    internal static void Reset()
    {
        Role = AppRole.User;
        MyEmail = "";
        Team = null;
        Notice = "";
        DatabaseError = "";
        SharedFolderReachable = false;
    }
}
