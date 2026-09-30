using SwiftBatchApp.Core;
using SwiftBatchApp.Data;
using static SwiftBatchApp.Core.WorkbookLayout;

namespace SwiftBatchApp.Tests;

/// <summary>End-to-end engine cycles against temp folders, a temp SQLite DB and a fake mailer.</summary>
public class ProcessingEngineTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly FakeMailer _mail = new();
    private readonly ProcessingEngine _engine;
    private readonly List<string> _log = new();
    private DateTime _now = new(2026, 7, 7, 10, 0, 0);          // Tuesday

    private string Watch => _tmp["watch"];
    private string Archive => _tmp["archive"];
    private string RegistryFile => Path.Combine(Archive, "Registry.xlsx");

    public ProcessingEngineTests()
    {
        ExcelWriter.RetryCount = 2;
        ExcelWriter.RetryDelayMs = 5;
        _tmp.Dir("watch");
        _tmp.Dir("archive");
        AppDb.Initialize(_tmp["engine.db"]);
        AppDb.SetSettings(new Dictionary<string, string>
        {
            [SettingKeys.WatchFolder] = Watch,
            [SettingKeys.ArchiveRoot] = Archive,
            [SettingKeys.RegistryPath] = RegistryFile,
            [SettingKeys.ManagerEmail] = "manager@x",
            [SettingKeys.OooWaitSeconds] = "1",
            [SettingKeys.CutoffTime] = "13:00",
            [SettingKeys.DeferredSendTime] = "07:00",
        });
        foreach (string u in new[] { "a@x", "b@x", "c@x", "d@x" }) AppDb.SaveUser(new AppUser { Email = u });

        _engine = new ProcessingEngine(_ => _mail, () => _now);
        _engine.Log += line => _log.Add(line);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _tmp.Dispose();
    }

    private CycleSummary Cycle() => _engine.RunCycle() ?? throw new InvalidOperationException("cycle did not run");

    private static SheetTable Read(string path) => ExcelReader.ReadTable(path);

    private string DayDir(DateTime d) => DayFolder(Archive, d);

    private void KeepOnly(params string[] activeUsers)
    {
        foreach (AppUser u in AppDb.GetUsers())
        {
            u.IsActive = activeUsers.Contains(u.Email);
            AppDb.SaveUser(u);
        }
    }

    [Fact]
    public void A_new_file_is_mailed_to_one_user_archived_and_logged_in_both_workbooks()
    {
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);

        CycleSummary s = Cycle();

        Assert.Equal(1, s.Processed);
        var mail = Assert.Single(_mail.Sent);
        Assert.StartsWith("SWIFT File: synthetic_single_mt103 [", mail.Subject);
        Assert.Contains("TXN0000001", mail.Body);
        Assert.Empty(Directory.GetFiles(Watch));
        string archived = Path.Combine(DayDir(_now), Fixtures.SingleMt103);
        Assert.True(File.Exists(archived));
        Assert.EndsWith(Fixtures.SingleMt103, mail.Attachment);

        SheetTable reg = Read(RegistryFile);
        Assert.Equal(RegistryHeaders.Take(10), reg.Headers.Take(10));      // business-required order
        var r = Assert.Single(reg.Rows);
        Assert.Equal(Fixtures.SingleMt103, r[RFileName]);
        Assert.Equal("190768", r[ROsnFrom]);
        Assert.Equal("190768", r[ROsnTo]);
        Assert.Equal("1", r[ROrders]);
        Assert.Equal("06/07/2026", r[RExecDate]);
        Assert.Equal("", r[RAfterCutoff]);
        Assert.Equal(FileStatus.Pending, r[RStatus]);
        Assert.Equal("1", r[RMt103]);
        Assert.Equal("0", r[RMtf]);
        Assert.Equal(mail.To, r[RAssignedUser]);
        Assert.Equal("2026-07-07", r[RDate]);
        Assert.Equal("NOT CHECKED", r[RCustomerMatch]);

        SheetTable daily = Read(DailyWorkbookPath(Archive, _now));
        Assert.Equal(DAssignedUser, daily.Headers[0]);                      // column A = assigned user
        var p = Assert.Single(daily.Rows);
        Assert.Equal(mail.To, p[DAssignedUser]);
        Assert.Equal("TXN0000001", p[DTxnRef]);
        Assert.Equal("12345.67", p[DAmount]);
        Assert.Equal("MT103", p[DType]);
        Assert.Equal(PaymentStatus.Pending, p[DStatus]);

        FileRecord rec = AppDb.GetFileRecord(Fixtures.SingleMt103)!;
        Assert.Equal(mail.To, rec.AssignedUser);
        Assert.Equal(archived, rec.ArchivePath);
    }

    [Fact]
    public void A_duplicate_is_moved_aside_without_any_email()
    {
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);
        Cycle();
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);

        CycleSummary s = Cycle();

        Assert.Equal(1, s.Duplicates);
        Assert.Single(_mail.Sent);
        Assert.True(File.Exists(Path.Combine(DuplicatesFolder(Archive, _now), Fixtures.SingleMt103)));
        Assert.Single(Read(RegistryFile).Rows);
    }

    [Fact]
    public void Out_of_office_user_is_skipped_marked_and_the_final_assignee_is_logged()
    {
        KeepOnly("a@x", "b@x");
        AppDb.RecordFile(new FileRecord { FileName = "earlier.prt", AssignedUser = "b@x", ProcessedDate = DateKey(_now), PaymentCount = 5 },
            Array.Empty<PaymentOrder>());                                    // b is busier → a is tried first
        _mail.OutOfOffice.Add("a@x");
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);

        Cycle();

        Assert.Equal(new[] { "a@x", "b@x" }, _mail.Sent.Select(m => m.To));
        Assert.True(AppDb.GetUser("a@x")!.IsOooOn(_now));
        Assert.Equal("b@x", Read(RegistryFile).Rows[0][RAssignedUser]);
        Assert.Equal("b@x", Read(DailyWorkbookPath(Archive, _now)).Rows[0][DAssignedUser]);

        // Later the same day a is excluded without probing again.
        _mail.Sent.Clear();
        Fixtures.CopyTo(Fixtures.MultiMtf, Watch);
        Cycle();
        Assert.Equal(new[] { "b@x" }, _mail.Sent.Select(m => m.To));
    }

    [Fact]
    public void Everyone_out_of_office_escalates_to_the_manager()
    {
        KeepOnly("a@x", "b@x");
        _mail.OutOfOffice.UnionWith(new[] { "a@x", "b@x" });
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);

        Cycle();

        Assert.Equal("manager@x", _mail.Sent[^1].To);
        Assert.StartsWith("ESCALATION - no user available", _mail.Sent[^1].Subject);
        var r = Read(RegistryFile).Rows[0];
        Assert.Equal(FileStatus.Escalated, r[RStatus]);
        Assert.Equal("manager@x", r[RAssignedUser]);
    }

    [Fact]
    public void Send_failure_leaves_the_file_for_the_next_cycle()
    {
        _mail.FailAll = true;
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);

        CycleSummary s = Cycle();

        Assert.Equal(1, s.Failed);
        Assert.Single(Directory.GetFiles(Watch));
        Assert.False(File.Exists(RegistryFile));
        Assert.False(AppDb.IsFileProcessedOrQueued(Fixtures.SingleMt103));

        _mail.FailAll = false;
        Assert.Equal(1, Cycle().Processed);
        Assert.Empty(Directory.GetFiles(Watch));
    }

    [Fact]
    public void After_cutoff_files_wait_in_the_next_working_day_folder_until_the_send_time()
    {
        _now = new DateTime(2026, 7, 10, 14, 5, 0);                        // Friday after 13:00
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);

        Assert.Equal(1, Cycle().Deferred);
        DateTime monday = new(2026, 7, 13);
        Assert.Empty(_mail.Sent);
        Assert.True(File.Exists(Path.Combine(DayDir(monday), Fixtures.SingleMt103)));
        DeferredItem d = Assert.Single(AppDb.GetOpenDeferred());
        Assert.Equal("2026-07-13 07:00", d.ScheduledFor);

        _now = new DateTime(2026, 7, 13, 6, 55, 0);
        Cycle();
        Assert.Empty(_mail.Sent);

        _now = new DateTime(2026, 7, 13, 7, 2, 0);
        Assert.Equal(1, Cycle().Processed);
        Assert.Single(_mail.Sent);
        Assert.Empty(AppDb.GetOpenDeferred());
        var r = Read(RegistryFile).Rows[0];
        Assert.Equal("13/07/2026", r[RAfterCutoff]);
        Assert.Equal("2026-07-13", r[RDate]);
        Assert.Single(Read(DailyWorkbookPath(Archive, monday)).Rows);
        Assert.True(File.Exists(Path.Combine(DayDir(monday), Fixtures.SingleMt103)));
    }

    [Fact]
    public void Unknown_customer_goes_to_the_manager_as_no_match()
    {
        string master = CustomerMasterTests.CreateMaster(_tmp["master.xlsx"], ("Somebody Else", "ACMEGRA1", "GR0000000000000000000000001", ""));
        AppDb.SetSetting(SettingKeys.MasterFilePath, master);
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);

        Cycle();

        var mail = Assert.Single(_mail.Sent);
        Assert.Equal("manager@x", mail.To);
        Assert.StartsWith("NO CUSTOMER MATCH", mail.Subject);
        var r = Read(RegistryFile).Rows[0];
        Assert.Equal(FileStatus.NoMatch, r[RStatus]);
        Assert.StartsWith("NO MATCH:", r[RCustomerMatch]);
        Assert.Empty(AppDb.GetPaymentCounts(DateKey(_now)));                // not part of anyone's fair share
    }

    [Fact]
    public void Known_customer_is_distributed_with_its_valeur()
    {
        string master = CustomerMasterTests.CreateMaster(_tmp["master.xlsx"], ("ACME TRADING SA", "ACMEGRA1XXX", "GR7201100000000012345678901", "T+2"));
        AppDb.SetSetting(SettingKeys.MasterFilePath, master);
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);

        Cycle();

        Assert.NotEqual("manager@x", Assert.Single(_mail.Sent).To);
        var r = Read(RegistryFile).Rows[0];
        Assert.Equal("MATCH: ACME TRADING SA", r[RCustomerMatch]);
        Assert.Equal("T+2", r[RValeur]);
    }

    [Fact]
    public void Unreadable_master_file_keeps_the_file_waiting()
    {
        AppDb.SetSetting(SettingKeys.MasterFilePath, _tmp["missing-master.xlsx"]);
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);

        Assert.Equal(1, Cycle().Failed);
        Assert.Empty(_mail.Sent);
        Assert.Single(Directory.GetFiles(Watch));
    }

    [Fact]
    public void Locked_registry_queues_the_row_and_a_later_cycle_writes_it()
    {
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);
        Cycle();
        Fixtures.CopyTo(Fixtures.MultiMtf, Watch);

        using (new FileStream(RegistryFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // a user has it open
        {
            Assert.Equal(1, Cycle().Processed);
        }
        Assert.Equal(1, AppDb.CountPendingRows());
        Assert.Equal(2, _mail.Sent.Count);
        Assert.Contains(_log, l => l.Contains("[QUEUE]") && l.Contains("queued"));

        CycleSummary s = Cycle();
        Assert.Equal(1, s.Flushed);
        Assert.Equal(0, AppDb.CountPendingRows());
        var rows = Read(RegistryFile).Rows;
        Assert.Equal(new[] { Fixtures.SingleMt103, Fixtures.MultiMtf }, rows.Select(r => r[RFileName]));
        Assert.Equal("200100", rows[1][ROsnFrom]);

        Cycle();                                                             // flushing twice never duplicates
        Assert.Equal(2, Read(RegistryFile).Rows.Count);
    }

    [Fact]
    public void Files_in_a_burst_are_spread_evenly()
    {
        for (int i = 0; i < 8; i++) Fixtures.CopyTo(Fixtures.SingleMt103, Watch, $"0066{i:D4}.prt");

        Assert.Equal(8, Cycle().Processed);

        Assert.Equal(8, _mail.Sent.Count);
        Assert.All(_mail.Sent.GroupBy(m => m.To), g => Assert.Equal(2, g.Count()));
        Assert.Equal(8, Read(RegistryFile).Rows.Count);
        Assert.Equal(8, Read(DailyWorkbookPath(Archive, _now)).Rows.Count);
    }

    [Fact]
    public void Multi_payment_file_writes_one_row_per_payment()
    {
        Fixtures.CopyTo(Fixtures.MultiMtf, Watch);
        Cycle();

        var reg = Read(RegistryFile).Rows[0];
        Assert.Equal("3", reg[ROrders]);
        Assert.Equal("1", reg[RMt103]);
        Assert.Equal("2", reg[RMtf]);
        Assert.Equal("200100", reg[ROsnFrom]);
        Assert.Equal("200102", reg[ROsnTo]);
        Assert.Equal("TXN0000101, TXN0000102, TXN0000103", reg[RTxnRefs]);

        var rows = Read(DailyWorkbookPath(Archive, _now)).Rows;
        Assert.Equal(new[] { "MT103", "MTF", "MTF" }, rows.Select(r => r[DType]));
        Assert.Single(rows.Select(r => r[DAssignedUser]).Distinct());      // one file = one user
    }

    [Fact]
    public void Parse_errors_are_still_distributed_and_flagged()
    {
        Fixtures.CopyTo(Fixtures.NotMt101, Watch);
        Cycle();

        Assert.Single(_mail.Sent);
        var r = Read(RegistryFile).Rows[0];
        Assert.Equal(FileStatus.ParseError, r[RStatus]);
        Assert.Equal("0", r[ROrders]);
        Assert.False(File.Exists(DailyWorkbookPath(Archive, _now)));
    }

    [Fact]
    public void Registry_status_follows_payment_completion()
    {
        Fixtures.CopyTo(Fixtures.MultiMtf, Watch);
        Cycle();
        string daily = DailyWorkbookPath(Archive, _now);

        WorkbookOps.SetPaymentStatus(daily, new[] { (Fixtures.MultiMtf, "TXN0000101") }, PaymentStatus.Completed);
        Cycle();
        Assert.Equal(FileStatus.Pending, Read(RegistryFile).Rows[0][RStatus]);

        WorkbookOps.SetFilePaymentsStatus(daily, Fixtures.MultiMtf, PaymentStatus.Completed);
        Cycle();
        Assert.Equal(FileStatus.Completed, Read(RegistryFile).Rows[0][RStatus]);

        WorkbookOps.SetPaymentStatus(daily, new[] { (Fixtures.MultiMtf, "TXN0000102") }, PaymentStatus.Pending);
        Cycle();
        Assert.Equal(FileStatus.Pending, Read(RegistryFile).Rows[0][RStatus]);
    }

    [Fact]
    public void Missing_watch_folder_is_reported_not_thrown()
    {
        AppDb.SetSetting(SettingKeys.WatchFolder, _tmp["nope"]);
        Cycle();
        Assert.Contains(_log, l => l.Contains("[ERROR]") && l.Contains("not reachable"));
    }

    [Fact]
    public void Mail_body_lists_every_payment()
    {
        ParsedFile parsed = SwiftParser.ParseFile(Fixtures.Path(Fixtures.MultiMtf));
        string body = ProcessingEngine.BuildBody(parsed, MatchResult.NotChecked(), null);
        Assert.Contains("Orders: 3 (MT103: 1, MTF: 2)", body);
        Assert.Contains("OSN: 200100 – 200102", body);
        Assert.Contains("99.999,99", body);
        Assert.Contains("TXN0000103", body);
    }

    [Fact]
    public void Engine_stops_when_this_pc_is_no_longer_the_manager()
    {
        Fixtures.CopyTo(Fixtures.SingleMt103, Watch);
        _engine.MayRun = () => false;

        Assert.Null(_engine.RunCycle());

        Assert.Empty(_mail.Sent);
        Assert.Single(Directory.GetFiles(Watch));
        Assert.Contains(_log, l => l.Contains("no longer the manager"));
    }
}
