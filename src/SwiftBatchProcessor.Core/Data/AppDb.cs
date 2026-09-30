using System.Globalization;
using Microsoft.Data.Sqlite;
using SwiftBatchApp.Core;

namespace SwiftBatchApp.Data;

/// <summary>
/// The team's engine database <c>SwiftBatch.db</c>, kept in the SHARED folder next to Registry.xlsx
/// (never on a PC). Only the manager's app opens it — one writer, so SQLite is safe on the network share
/// (rollback journal, no WAL). It holds settings, users/OOO, idempotency, fairness counts, deferred files,
/// the Excel write-behind queue and a history mirror (Files, Payments). The UI on every PC reads the
/// Excel workbooks instead, so user PCs never open this file.
/// </summary>
public static class AppDb
{
    public const string FileName = "SwiftBatch.db";

    private static string _connectionString = "";

    public static string DbPath { get; private set; } = "";
    public static bool IsInitialized => _connectionString.Length > 0;

    // ---------------------------------------------------------------- setup

    /// <summary>Opens (creating/upgrading if needed) the database at <paramref name="path"/>.</summary>
    public static void Initialize(string path, DefaultsFile? defaults = null)
    {
        string full = Path.GetFullPath(path);
        string connection = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,              // never keep the network file open between operations
            DefaultTimeout = 30,          // busy timeout (seconds) — the share can be slow
        }.ToString();

        using (var c = new SqliteConnection(connection))
        {
            c.Open();
            Exec(c, "PRAGMA journal_mode=DELETE");      // WAL needs shared memory: unsafe on network shares
            EnsureSchema(c);
            Seed(c, defaults);
        }
        DbPath = full;
        _connectionString = connection;
    }

    /// <summary>Forgets the open database (tests; switching shared folder).</summary>
    public static void Close()
    {
        _connectionString = "";
        DbPath = "";
    }

    private static SqliteConnection Open()
    {
        if (!IsInitialized) throw new InvalidOperationException("AppDb.Initialize has not been called.");
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void EnsureSchema(SqliteConnection c)
    {
        Exec(c, """
            CREATE TABLE IF NOT EXISTS Users (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Email       TEXT NOT NULL UNIQUE COLLATE NOCASE,
                DisplayName TEXT NOT NULL DEFAULT '',
                IsActive    INTEGER NOT NULL DEFAULT 1,
                OooDate     TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS Settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS Files (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                FileName      TEXT NOT NULL,
                AssignedUser  TEXT NOT NULL DEFAULT '',
                ProcessedDate TEXT NOT NULL,
                PaymentCount  INTEGER NOT NULL DEFAULT 0,
                TxnRefs       TEXT NOT NULL DEFAULT '',
                Status        TEXT NOT NULL DEFAULT 'OK',
                LoggedAt      TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS Payments (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                FileId        INTEGER NOT NULL,
                FileName      TEXT NOT NULL,
                AssignedUser  TEXT NOT NULL DEFAULT '',
                ProcessedDate TEXT NOT NULL,
                SenderRef     TEXT NOT NULL DEFAULT '',
                Receiver      TEXT NOT NULL DEFAULT '',
                TxnRef        TEXT NOT NULL DEFAULT '',
                Currency      TEXT NOT NULL DEFAULT '',
                Amount        REAL,
                LoggedAt      TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS Deferred (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                FileName     TEXT NOT NULL,
                FilePath     TEXT NOT NULL,
                ReceivedAt   TEXT NOT NULL,
                ScheduledFor TEXT NOT NULL,
                TargetYmd    TEXT NOT NULL,
                Done         INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS PendingRows (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Path      TEXT NOT NULL,
                RowJson   TEXT NOT NULL,
                Kind      TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Attempts  INTEGER NOT NULL DEFAULT 0);
            """);

        // Additive migrations (v2.4): older databases keep working.
        AddColumnIfMissing(c, "Users", "WindowsUser", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(c, "Files", "ArchivePath", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(c, "Files", "CustomerMatch", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(c, "PendingRows", "LastError", "TEXT NOT NULL DEFAULT ''");

        Exec(c, """
            CREATE INDEX IF NOT EXISTS IX_Files_ProcessedDate ON Files(ProcessedDate);
            CREATE INDEX IF NOT EXISTS IX_Files_AssignedUser ON Files(AssignedUser);
            CREATE INDEX IF NOT EXISTS IX_Files_FileName ON Files(FileName);
            CREATE INDEX IF NOT EXISTS IX_Payments_ProcessedDate ON Payments(ProcessedDate);
            CREATE INDEX IF NOT EXISTS IX_Payments_AssignedUser ON Payments(AssignedUser);
            CREATE INDEX IF NOT EXISTS IX_Deferred_Done_ScheduledFor ON Deferred(Done, ScheduledFor);
            """);
    }

    private static void AddColumnIfMissing(SqliteConnection c, string table, string column, string definition)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }
        Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static void Seed(SqliteConnection c, DefaultsFile? defaults)
    {
        using var tx = c.BeginTransaction();
        foreach (var (key, builtIn) in SettingKeys.Defaults)
        {
            string value = defaults?.Settings.GetValueOrDefault(key) ?? builtIn;
            Exec(c, "INSERT OR IGNORE INTO Settings(Key, Value) VALUES(@k, @v)", tx, ("@k", key), ("@v", value));
        }

        long users = Scalar<long>(c, "SELECT COUNT(*) FROM Users", tx);
        if (users == 0 && defaults is not null)
        {
            foreach (var u in defaults.Users.Where(u => !string.IsNullOrWhiteSpace(u.Email)))
                Exec(c, "INSERT OR IGNORE INTO Users(Email, DisplayName, IsActive, WindowsUser) VALUES(@e, @n, 1, @w)", tx,
                    ("@e", u.Email.Trim()), ("@n", u.DisplayName.Trim()), ("@w", u.WindowsUser.Trim()));
        }
        tx.Commit();
    }

    // ---------------------------------------------------------------- settings

    public static string GetSetting(string key, string fallback = "")
    {
        using var c = Open();
        return Scalar<string?>(c, "SELECT Value FROM Settings WHERE Key = @k", null, ("@k", key)) ?? fallback;
    }

    public static void SetSetting(string key, string value)
    {
        using var c = Open();
        Exec(c, "INSERT INTO Settings(Key, Value) VALUES(@k, @v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
            null, ("@k", key), ("@v", value ?? ""));
    }

    public static void SetSettings(IReadOnlyDictionary<string, string> values)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var (k, v) in values)
            Exec(c, "INSERT INTO Settings(Key, Value) VALUES(@k, @v) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
                tx, ("@k", k), ("@v", v ?? ""));
        tx.Commit();
    }

    public static Dictionary<string, string> GetAllSettings()
    {
        using var c = Open();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM Settings";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    public static EngineSettings LoadSettings() => EngineSettings.From(GetAllSettings());

    // ---------------------------------------------------------------- users

    public static List<AppUser> GetUsers()
    {
        using var c = Open();
        return Query(c, "SELECT Id, Email, DisplayName, IsActive, OooDate, WindowsUser FROM Users ORDER BY Email COLLATE NOCASE", ReadUser);
    }

    public static AppUser? GetUser(string email)
    {
        using var c = Open();
        return Query(c, "SELECT Id, Email, DisplayName, IsActive, OooDate, WindowsUser FROM Users WHERE Email = @e COLLATE NOCASE",
            ReadUser, ("@e", email)).FirstOrDefault();
    }

    /// <summary>Inserts (Id == 0) or updates a user. Returns the Id.</summary>
    public static long SaveUser(AppUser u)
    {
        using var c = Open();
        if (u.Id == 0)
        {
            Exec(c, "INSERT INTO Users(Email, DisplayName, IsActive, OooDate, WindowsUser) VALUES(@e, @n, @a, @o, @w)", null,
                ("@e", u.Email.Trim()), ("@n", u.DisplayName.Trim()), ("@a", u.IsActive ? 1 : 0), ("@o", u.OooDate), ("@w", u.WindowsUser.Trim()));
            u.Id = Scalar<long>(c, "SELECT last_insert_rowid()");
        }
        else
        {
            Exec(c, "UPDATE Users SET Email = @e, DisplayName = @n, IsActive = @a, OooDate = @o, WindowsUser = @w WHERE Id = @id", null,
                ("@e", u.Email.Trim()), ("@n", u.DisplayName.Trim()), ("@a", u.IsActive ? 1 : 0), ("@o", u.OooDate),
                ("@w", u.WindowsUser.Trim()), ("@id", u.Id));
        }
        return u.Id;
    }

    public static void DeleteUser(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM Users WHERE Id = @id", null, ("@id", id));
    }

    /// <summary>Marks a user out of office for the given day (yyMMdd); "" clears it.</summary>
    public static void SetOoo(string email, string yyMMdd)
    {
        using var c = Open();
        Exec(c, "UPDATE Users SET OooDate = @d WHERE Email = @e COLLATE NOCASE", null, ("@d", yyMMdd), ("@e", email));
    }

    /// <summary>Active users not marked out of office on <paramref name="yyMMdd"/>.</summary>
    public static List<string> GetAssignableUsers(string yyMMdd)
    {
        using var c = Open();
        return Query(c, "SELECT Email FROM Users WHERE IsActive = 1 AND OooDate <> @d ORDER BY Email COLLATE NOCASE",
            r => r.GetString(0), ("@d", yyMMdd));
    }

    // ---------------------------------------------------------------- files

    /// <summary>Idempotency: the file was already distributed, or is waiting in the deferred queue.</summary>
    public static bool IsFileProcessedOrQueued(string fileName)
    {
        using var c = Open();
        return Scalar<long>(c, """
            SELECT (SELECT COUNT(*) FROM Files WHERE FileName = @f COLLATE NOCASE)
                 + (SELECT COUNT(*) FROM Deferred WHERE FileName = @f COLLATE NOCASE AND Done = 0)
            """, null, ("@f", fileName)) > 0;
    }

    public static FileRecord? GetFileRecord(string fileName)
    {
        using var c = Open();
        return Query(c, $"{FileSelect} WHERE FileName = @f COLLATE NOCASE ORDER BY Id DESC LIMIT 1", ReadFile, ("@f", fileName)).FirstOrDefault();
    }

    public static List<FileRecord> GetFiles(string dateKey)
    {
        using var c = Open();
        return Query(c, $"{FileSelect} WHERE ProcessedDate = @d ORDER BY Id", ReadFile, ("@d", dateKey));
    }

    /// <summary>Records a distributed file and its payments (one transaction). Returns the file Id.</summary>
    public static long RecordFile(FileRecord f, IEnumerable<PaymentOrder> payments)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, """
            INSERT INTO Files(FileName, AssignedUser, ProcessedDate, PaymentCount, TxnRefs, Status, LoggedAt, ArchivePath, CustomerMatch)
            VALUES(@f, @u, @d, @n, @t, @s, @l, @a, @m)
            """, tx,
            ("@f", f.FileName), ("@u", f.AssignedUser), ("@d", f.ProcessedDate), ("@n", f.PaymentCount), ("@t", f.TxnRefs),
            ("@s", f.Status), ("@l", f.LoggedAt), ("@a", f.ArchivePath), ("@m", f.CustomerMatch));
        long fileId = Scalar<long>(c, "SELECT last_insert_rowid()", tx);
        foreach (var p in payments)
        {
            Exec(c, """
                INSERT INTO Payments(FileId, FileName, AssignedUser, ProcessedDate, SenderRef, Receiver, TxnRef, Currency, Amount, LoggedAt)
                VALUES(@id, @f, @u, @d, @sr, @r, @t, @c, @a, @l)
                """, tx,
                ("@id", fileId), ("@f", f.FileName), ("@u", f.AssignedUser), ("@d", f.ProcessedDate), ("@sr", p.SenderRef),
                ("@r", p.Receiver), ("@t", p.TxnRef), ("@c", p.Currency), ("@a", p.Amount.HasValue ? (double)p.Amount.Value : null),
                ("@l", f.LoggedAt));
        }
        tx.Commit();
        f.Id = fileId;
        return fileId;
    }

    public static void UpdateArchivePath(string fileName, string archivePath)
    {
        using var c = Open();
        Exec(c, "UPDATE Files SET ArchivePath = @a WHERE FileName = @f COLLATE NOCASE", null, ("@a", archivePath), ("@f", fileName));
    }

    /// <summary>Manual reassignment: moves the file (and its payments) to another user; optionally changes the status.</summary>
    public static void UpdateFileAssignment(string fileName, string newUser, string? newStatus = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "UPDATE Files SET AssignedUser = @u, Status = COALESCE(@s, Status) WHERE FileName = @f COLLATE NOCASE", tx,
            ("@u", newUser), ("@s", newStatus), ("@f", fileName));
        Exec(c, "UPDATE Payments SET AssignedUser = @u WHERE FileName = @f COLLATE NOCASE", tx, ("@u", newUser), ("@f", fileName));
        tx.Commit();
    }

    public static void UpdateFileStatus(string fileName, string status)
    {
        using var c = Open();
        Exec(c, "UPDATE Files SET Status = @s WHERE FileName = @f COLLATE NOCASE", null, ("@s", status), ("@f", fileName));
    }

    /// <summary>Fairness load per user for a day: SUM of payments (min 1 per file), excluding ESCALATED / NO MATCH.</summary>
    public static Dictionary<string, int> GetPaymentCounts(string dateKey)
    {
        using var c = Open();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT AssignedUser, SUM(MAX(PaymentCount, 1)) FROM Files
            WHERE ProcessedDate = @d AND UPPER(Status) NOT IN ('ESCALATED', 'NO MATCH')
            GROUP BY AssignedUser COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("@d", dateKey);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string user = r.GetString(0);
            map[user] = map.GetValueOrDefault(user) + r.GetInt32(1);
        }
        return map;
    }

    // ---------------------------------------------------------------- deferred

    public static long AddDeferred(DeferredItem d)
    {
        using var c = Open();
        Exec(c, "INSERT INTO Deferred(FileName, FilePath, ReceivedAt, ScheduledFor, TargetYmd, Done) VALUES(@f, @p, @r, @s, @t, 0)", null,
            ("@f", d.FileName), ("@p", d.FilePath), ("@r", d.ReceivedAt), ("@s", d.ScheduledFor), ("@t", d.TargetYmd));
        d.Id = Scalar<long>(c, "SELECT last_insert_rowid()");
        return d.Id;
    }

    /// <summary>Open deferred files whose ScheduledFor (yyyy-MM-dd HH:mm) is at or before <paramref name="now"/>.</summary>
    public static List<DeferredItem> GetDueDeferred(DateTime now)
    {
        using var c = Open();
        return Query(c, $"{DeferredSelect} WHERE Done = 0 AND ScheduledFor <= @n ORDER BY ScheduledFor, Id", ReadDeferred,
            ("@n", now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
    }

    public static List<DeferredItem> GetOpenDeferred()
    {
        using var c = Open();
        return Query(c, $"{DeferredSelect} WHERE Done = 0 ORDER BY ScheduledFor, Id", ReadDeferred);
    }

    public static void MarkDeferredDone(long id)
    {
        using var c = Open();
        Exec(c, "UPDATE Deferred SET Done = 1 WHERE Id = @id", null, ("@id", id));
    }

    // ---------------------------------------------------------------- write-behind queue

    public static void EnqueuePendingRow(string path, string rowJson, string kind, string error = "")
    {
        using var c = Open();
        Exec(c, "INSERT INTO PendingRows(Path, RowJson, Kind, CreatedAt, Attempts, LastError) VALUES(@p, @j, @k, @c, 0, @e)", null,
            ("@p", path), ("@j", rowJson), ("@k", kind), ("@c", WorkbookLayout.Timestamp(DateTime.Now)), ("@e", error));
    }

    public static List<PendingRow> GetPendingRows()
    {
        using var c = Open();
        return Query(c, "SELECT Id, Path, RowJson, Kind, CreatedAt, Attempts, LastError FROM PendingRows ORDER BY Id", r => new PendingRow
        {
            Id = r.GetInt64(0), Path = r.GetString(1), RowJson = r.GetString(2), Kind = r.GetString(3),
            CreatedAt = r.GetString(4), Attempts = r.GetInt32(5), LastError = r.GetString(6),
        });
    }

    public static int CountPendingRows()
    {
        using var c = Open();
        return (int)Scalar<long>(c, "SELECT COUNT(*) FROM PendingRows");
    }

    public static void DeletePendingRows(IEnumerable<long> ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (long id in ids) Exec(c, "DELETE FROM PendingRows WHERE Id = @id", tx, ("@id", id));
        tx.Commit();
    }

    public static void MarkPendingRowsFailed(IEnumerable<long> ids, string error)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (long id in ids)
            Exec(c, "UPDATE PendingRows SET Attempts = Attempts + 1, LastError = @e WHERE Id = @id", tx, ("@e", error), ("@id", id));
        tx.Commit();
    }

    // ---------------------------------------------------------------- helpers

    private const string FileSelect =
        "SELECT Id, FileName, AssignedUser, ProcessedDate, PaymentCount, TxnRefs, Status, LoggedAt, ArchivePath, CustomerMatch FROM Files";

    private const string DeferredSelect =
        "SELECT Id, FileName, FilePath, ReceivedAt, ScheduledFor, TargetYmd, Done FROM Deferred";

    private static AppUser ReadUser(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Email = r.GetString(1), DisplayName = r.GetString(2), IsActive = r.GetInt64(3) != 0,
        OooDate = r.GetString(4), WindowsUser = r.GetString(5),
    };

    private static FileRecord ReadFile(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), FileName = r.GetString(1), AssignedUser = r.GetString(2), ProcessedDate = r.GetString(3),
        PaymentCount = r.GetInt32(4), TxnRefs = r.GetString(5), Status = r.GetString(6), LoggedAt = r.GetString(7),
        ArchivePath = r.GetString(8), CustomerMatch = r.GetString(9),
    };

    private static DeferredItem ReadDeferred(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), FileName = r.GetString(1), FilePath = r.GetString(2), ReceivedAt = r.GetString(3),
        ScheduledFor = r.GetString(4), TargetYmd = r.GetString(5), Done = r.GetInt64(6) != 0,
    };

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, sql, tx, args);
        object? v = cmd.ExecuteScalar();
        if (v is null || v is DBNull) return default!;
        Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(v, target, CultureInfo.InvariantCulture);
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, sql, null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(read(r));
        return list;
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? tx, (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
