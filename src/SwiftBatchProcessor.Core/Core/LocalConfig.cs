using System.Text.Json;
using Microsoft.Data.Sqlite;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Core;

/// <summary>
/// The only thing stored on the PC itself: <c>SwiftBatch.local.json</c> next to the exe with
/// where the shared folder is (the registry path) and who uses this PC. No business data lives here —
/// the database, team file, workbooks and logs are all in the shared folder on the server.
/// </summary>
public static class LocalConfig
{
    public const string FileName = "SwiftBatch.local.json";

    /// <summary>A pre-v2.5 database next to the exe; migrated to the share and then renamed to *.old.</summary>
    public const string LegacyDatabaseName = "SwiftBatch.db";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object Gate = new();
    private static Data _data = new();

    /// <summary>Overridable in tests.</summary>
    internal static string Folder { get; set; } = AppContext.BaseDirectory;

    public static string FilePath => Path.Combine(Folder, FileName);
    public static string LegacyDatabasePath => Path.Combine(Folder, LegacyDatabaseName);
    public static string LegacyBackupPath => LegacyDatabasePath + ".old";

    public sealed class Data
    {
        public string RegistryPath { get; set; } = "";
        public string MyEmail { get; set; } = "";
        public string ManagerWindowsUser { get; set; } = "";
    }

    // ---------------------------------------------------------------- values

    /// <summary>Registry.xlsx on the shared folder. Its folder also holds AppUsers.json, SwiftBatch.db and logs\.</summary>
    public static string RegistryPath
    {
        get { lock (Gate) return _data.RegistryPath; }
        set { lock (Gate) _data.RegistryPath = (value ?? "").Trim(); Save(); }
    }

    /// <summary>The identity picked on this PC ("" = recognise by Windows account).</summary>
    public static string MyEmail
    {
        get { lock (Gate) return _data.MyEmail; }
        set { lock (Gate) _data.MyEmail = (value ?? "").Trim(); Save(); }
    }

    /// <summary>Remembers that this PC's Windows account was the manager (used while the share is unreachable).</summary>
    public static string ManagerWindowsUser
    {
        get { lock (Gate) return _data.ManagerWindowsUser; }
        set { lock (Gate) _data.ManagerWindowsUser = (value ?? "").Trim(); Save(); }
    }

    public static string SharedFolder
    {
        get
        {
            string registry = RegistryPath;
            return registry.Length > 0 ? Path.GetDirectoryName(registry) ?? "" : "";
        }
    }

    /// <summary>The team's database on the server: next to Registry.xlsx.</summary>
    public static string SharedDatabasePath => SharedFolder.Length > 0 ? Path.Combine(SharedFolder, AppDb.FileName) : "";

    public static string SharedLogFolder => SharedFolder.Length > 0 ? Path.Combine(SharedFolder, "logs") : "";

    // ---------------------------------------------------------------- load / save

    /// <summary>
    /// Reads SwiftBatch.local.json. On first start it is created from a pre-v2.5 local database (if any —
    /// that file is then renamed to SwiftBatch.db.old so the manager can move its history to the share)
    /// and from the registry path in SwiftBatch.defaults.json.
    /// </summary>
    public static void Load(DefaultsFile? defaults)
    {
        Data data = new();
        bool fresh = !File.Exists(FilePath);
        if (!fresh)
        {
            try { data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), Json) ?? new Data(); }
            catch { data = new Data(); fresh = true; }
        }

        if (File.Exists(LegacyDatabasePath))
        {
            if (fresh) ImportLegacy(data, LegacyDatabasePath);
            RetireLegacyDatabase();
        }
        if (data.RegistryPath.Length == 0 && defaults?.Settings.GetValueOrDefault(SettingKeys.RegistryPath) is { Length: > 0 } seeded)
            data.RegistryPath = seeded.Trim();

        lock (Gate) _data = data;
        Save();
    }

    /// <summary>Best effort: false when the exe folder is not writable.</summary>
    public static bool Save()
    {
        try
        {
            string json;
            lock (Gate) json = JsonSerializer.Serialize(_data, Json);
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ImportLegacy(Data data, string dbPath)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
            }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Key, Value FROM Settings WHERE Key IN ('RegistryPath', 'MyEmail', 'ManagerWindowsUser')";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string value = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                switch (r.GetString(0))
                {
                    case "RegistryPath": data.RegistryPath = value; break;
                    case "MyEmail": data.MyEmail = value; break;
                    case "ManagerWindowsUser": data.ManagerWindowsUser = value; break;
                }
            }
        }
        catch
        {
            // unreadable legacy file: start clean
        }
    }

    private static void RetireLegacyDatabase()
    {
        try
        {
            string target = LegacyBackupPath;
            if (File.Exists(target)) target = $"{LegacyDatabasePath}.{DateTime.Now:yyyyMMddHHmmss}.old";
            File.Move(LegacyDatabasePath, target);
        }
        catch
        {
            // in use or read-only folder: try again next start
        }
    }

    /// <summary>Test hook: use another folder and forget the loaded values.</summary>
    internal static void UseFolder(string folder)
    {
        Folder = folder;
        lock (Gate) _data = new Data();
    }
}
