using Microsoft.Data.Sqlite;
using SwiftBatchApp.Core;
using SwiftBatchApp.Data;

namespace SwiftBatchApp.Tests;

public class AppDbTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private static DefaultsFile Defaults(string watch, params string[] users) => new()
    {
        Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["WatchFolder"] = watch, ["CutoffTime"] = "14:30" },
        Users = users.Select(u => new DefaultsFile.DefaultUser { Email = u, DisplayName = u.Split('@')[0] }).ToList(),
    };

    [Fact]
    public void New_database_is_seeded_from_the_defaults_file_and_later_runs_keep_edits()
    {
        string db = _tmp["a.db"];
        AppDb.Initialize(db, Defaults(@"\\server\watch", "u1@x", "u2@x"));

        Assert.Equal(@"\\server\watch", AppDb.GetSetting(SettingKeys.WatchFolder));
        Assert.Equal("14:30", AppDb.GetSetting(SettingKeys.CutoffTime));
        Assert.Equal("300", AppDb.GetSetting(SettingKeys.PollSeconds));        // built-in default
        Assert.Equal(2, AppDb.GetUsers().Count);

        AppDb.SetSetting(SettingKeys.WatchFolder, @"\\server\changed");
        AppDb.Initialize(db, Defaults(@"\\server\other", "u3@x"));
        Assert.Equal(@"\\server\changed", AppDb.GetSetting(SettingKeys.WatchFolder));
        Assert.Equal(2, AppDb.GetUsers().Count);                                 // users seeded only when empty
    }

    [Fact]
    public void The_example_defaults_file_is_valid_and_seeds_a_database()
    {
        DefaultsFile? defaults = DefaultsFile.Load(Fixtures.Path("SwiftBatch.defaults.example.json"));
        Assert.NotNull(defaults);
        Assert.Equal(2, defaults!.Users.Count);

        AppDb.Initialize(_tmp["example.db"], defaults);
        Assert.Equal(@"\\fileserver\share\MT942PMNTS\Registry.xlsx", AppDb.GetSetting(SettingKeys.RegistryPath));
        Assert.Equal("processor1", AppDb.GetUser("processor1@example.com")!.WindowsUser);
        Assert.Null(DefaultsFile.Load(_tmp["missing.json"]));
    }

    [Fact]
    public void Old_v2_3_database_is_migrated_in_place()
    {
        string db = _tmp["old.db"];
        using (var c = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE Users (Id INTEGER PRIMARY KEY AUTOINCREMENT, Email TEXT NOT NULL UNIQUE COLLATE NOCASE,
                                    DisplayName TEXT NOT NULL DEFAULT '', IsActive INTEGER NOT NULL DEFAULT 1, OooDate TEXT NOT NULL DEFAULT '');
                CREATE TABLE Files (Id INTEGER PRIMARY KEY AUTOINCREMENT, FileName TEXT NOT NULL, AssignedUser TEXT NOT NULL DEFAULT '',
                                    ProcessedDate TEXT NOT NULL, PaymentCount INTEGER NOT NULL DEFAULT 0, TxnRefs TEXT NOT NULL DEFAULT '',
                                    Status TEXT NOT NULL DEFAULT 'OK', LoggedAt TEXT NOT NULL DEFAULT '');
                INSERT INTO Users(Email, DisplayName) VALUES('old@x', 'Old');
                INSERT INTO Files(FileName, AssignedUser, ProcessedDate, PaymentCount) VALUES('00660001.prt', 'old@x', '2026-09-01', 2);
                """;
            cmd.ExecuteNonQuery();
        }

        AppDb.Initialize(db);

        AppUser u = Assert.Single(AppDb.GetUsers());
        Assert.Equal("old@x", u.Email);
        Assert.Equal("", u.WindowsUser);
        Assert.True(AppDb.IsFileProcessedOrQueued("00660001.PRT"));
        Assert.Equal("", AppDb.GetFileRecord("00660001.prt")!.ArchivePath);
    }

    [Fact]
    public void Idempotency_covers_sent_and_deferred_files()
    {
        AppDb.Initialize(_tmp["b.db"]);
        Assert.False(AppDb.IsFileProcessedOrQueued("x.prt"));

        long id = AppDb.AddDeferred(new DeferredItem { FileName = "x.prt", FilePath = "p", ReceivedAt = "r", ScheduledFor = "2026-07-08 07:00", TargetYmd = "260708" });
        Assert.True(AppDb.IsFileProcessedOrQueued("x.prt"));
        AppDb.MarkDeferredDone(id);
        Assert.False(AppDb.IsFileProcessedOrQueued("x.prt"));

        AppDb.RecordFile(new FileRecord { FileName = "x.prt", AssignedUser = "a@x", ProcessedDate = "2026-07-08" }, Array.Empty<PaymentOrder>());
        Assert.True(AppDb.IsFileProcessedOrQueued("X.PRT"));
    }

    [Fact]
    public void Fairness_counts_exclude_escalations_and_count_empty_files_as_one()
    {
        AppDb.Initialize(_tmp["c.db"]);
        void Rec(string user, int n, string status) =>
            AppDb.RecordFile(new FileRecord { FileName = Guid.NewGuid() + ".prt", AssignedUser = user, ProcessedDate = "2026-07-07", PaymentCount = n, Status = status },
                Array.Empty<PaymentOrder>());

        Rec("a@x", 3, "OK");
        Rec("A@X", 2, "Completed");
        Rec("b@x", 0, "PARSE ERROR");
        Rec("m@x", 9, "ESCALATED");
        Rec("m@x", 9, "NO MATCH");
        AppDb.RecordFile(new FileRecord { FileName = "other-day.prt", AssignedUser = "b@x", ProcessedDate = "2026-07-06", PaymentCount = 50 },
            Array.Empty<PaymentOrder>());

        var counts = AppDb.GetPaymentCounts("2026-07-07");
        Assert.Equal(5, counts["a@x"]);
        Assert.Equal(1, counts["B@X"]);
        Assert.False(counts.ContainsKey("m@x"));
    }

    [Fact]
    public void Assignable_users_are_active_and_not_out_of_office_today()
    {
        AppDb.Initialize(_tmp["d.db"]);
        AppDb.SaveUser(new AppUser { Email = "a@x" });
        AppDb.SaveUser(new AppUser { Email = "b@x", IsActive = false });
        AppDb.SaveUser(new AppUser { Email = "c@x", OooDate = "260707" });
        AppDb.SaveUser(new AppUser { Email = "d@x", OooDate = "260706" });   // yesterday's OOO has expired

        Assert.Equal(new[] { "a@x", "d@x" }, AppDb.GetAssignableUsers("260707"));
        AppDb.SetOoo("A@X", "260707");
        Assert.Equal(new[] { "d@x" }, AppDb.GetAssignableUsers("260707"));
    }

    [Fact]
    public void Pending_rows_queue_round_trip()
    {
        AppDb.Initialize(_tmp["e.db"]);
        AppDb.EnqueuePendingRow("p1", "{\"a\":1}", PendingRowKind.Registry, "locked");
        AppDb.EnqueuePendingRow("p2", "{\"b\":2}", PendingRowKind.Daily);
        var rows = AppDb.GetPendingRows();
        Assert.Equal(2, rows.Count);

        AppDb.MarkPendingRowsFailed(new[] { rows[0].Id }, "still locked");
        Assert.Equal(1, AppDb.GetPendingRows()[0].Attempts);
        Assert.Equal("still locked", AppDb.GetPendingRows()[0].LastError);

        AppDb.DeletePendingRows(rows.Select(r => r.Id));
        Assert.Equal(0, AppDb.CountPendingRows());
    }
}
