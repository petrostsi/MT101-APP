using SwiftBatchApp.Core;
using SwiftBatchApp.Data;
using static SwiftBatchApp.Core.WorkbookLayout;

namespace SwiftBatchApp.Tests;

/// <summary>The UI data layer on top of workbooks produced by a real engine cycle.</summary>
public class WorkbookStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly DateTime _day = new(2026, 7, 7);
    private string Archive => _tmp["archive"];
    private string RegistryFile => Path.Combine(Archive, "Registry.xlsx");

    public WorkbookStoreTests()
    {
        ExcelWriter.RetryCount = 2;
        ExcelWriter.RetryDelayMs = 5;
        _tmp.Dir("watch");
        _tmp.Dir("archive");
        AppDb.Initialize(_tmp["store.db"]);
        AppDb.SetSettings(new Dictionary<string, string>
        {
            [SettingKeys.WatchFolder] = _tmp["watch"],
            [SettingKeys.ArchiveRoot] = Archive,
            [SettingKeys.RegistryPath] = RegistryFile,
            [SettingKeys.ManagerEmail] = "manager@x",
            [SettingKeys.OooWaitSeconds] = "0",
        });
        AppDb.SaveUser(new AppUser { Email = "a@x" });
        WorkbookStore.RegistryPathProvider = () => RegistryFile;
        WorkbookStore.ArchiveRootProvider = () => Archive;

        var mailer = new FakeMailer();
        using var engine = new ProcessingEngine(_ => mailer, () => _day.AddHours(10));
        Fixtures.CopyTo(Fixtures.SingleMt103, _tmp["watch"]);
        Fixtures.CopyTo(Fixtures.MultiMtf, _tmp["watch"]);
        engine.RunCycle();

        WorkbookStore.MarkDirty();
        WorkbookStore.Reload(force: true);
    }

    public void Dispose()
    {
        WorkbookStore.RegistryPathProvider = () => AppDb.GetSetting(SettingKeys.RegistryPath);
        WorkbookStore.ArchiveRootProvider = () => Session.ArchiveRoot;
        WorkbookStore.MarkDirty();
        _tmp.Dispose();
    }

    [Fact]
    public void Registry_and_day_workbooks_are_loaded()
    {
        Assert.Equal("", WorkbookStore.LastError);
        Assert.Equal(2, WorkbookStore.Registry.Count);
        Assert.All(WorkbookStore.Registry, e => Assert.Equal(_day, e.Day));
        Assert.All(WorkbookStore.Registry, e => Assert.Equal(FileStatus.Pending, e.DisplayStatus));
        Assert.Equal(4, WorkbookStore.PaymentsForDay(_day).Count);
        Assert.Equal(3, WorkbookStore.PaymentsForFile(WorkbookStore.FindEntry(Fixtures.MultiMtf)!).Count);

        PendingWork w = Assert.Single(WorkbookStore.PendingByUser());
        Assert.Equal("a@x", w.User);
        Assert.Equal(4, w.PendingPayments);
        Assert.Equal(2, w.PendingFiles);
    }

    [Fact]
    public void Completing_payments_derives_the_file_status()
    {
        RegistryEntry multi = WorkbookStore.FindEntry(Fixtures.MultiMtf)!;
        var payments = WorkbookStore.PaymentsForFile(multi);

        Assert.Null(WorkbookStore.SetPaymentStatus(payments.Take(2), PaymentStatus.Completed));
        Assert.Equal(FileStatus.Pending, WorkbookStore.FindEntry(Fixtures.MultiMtf)!.DisplayStatus);

        Assert.Null(WorkbookStore.SetPaymentStatus(payments.Skip(2), PaymentStatus.Completed));
        Assert.Equal(FileStatus.Completed, WorkbookStore.FindEntry(Fixtures.MultiMtf)!.DisplayStatus);
        Assert.Equal(FileStatus.Completed,
            ExcelReader.ReadTable(RegistryFile).Rows.Single(r => r[RFileName] == Fixtures.MultiMtf)[RStatus]);
        Assert.Equal(1, WorkbookStore.PendingByUser().Single().PendingPayments);
    }

    [Fact]
    public void Mark_file_completed_completes_every_payment()
    {
        Assert.Null(WorkbookStore.MarkFileCompleted(WorkbookStore.FindEntry(Fixtures.SingleMt103)!));
        Assert.True(WorkbookStore.FindEntry(Fixtures.SingleMt103)!.IsCompleted);
        Assert.All(WorkbookStore.PaymentsForFile(WorkbookStore.FindEntry(Fixtures.SingleMt103)!), p => Assert.True(p.IsCompleted));
    }

    [Fact]
    public void Locked_registry_on_status_change_returns_a_warning_but_keeps_the_payment_update()
    {
        RegistryEntry single = WorkbookStore.FindEntry(Fixtures.SingleMt103)!;
        string? warning;
        using (new FileStream(RegistryFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            warning = WorkbookStore.SetPaymentStatus(WorkbookStore.PaymentsForFile(single), PaymentStatus.Completed);
        }
        Assert.NotNull(warning);
        WorkbookStore.MarkDirty();
        WorkbookStore.Reload(force: true);
        Assert.Equal(FileStatus.Completed, WorkbookStore.FindEntry(Fixtures.SingleMt103)!.DisplayStatus);   // derived from payments
    }

    [Fact]
    public void Reassign_rewrites_both_workbooks()
    {
        WorkbookStore.Reassign(WorkbookStore.FindEntry(Fixtures.MultiMtf)!, "b@x");

        Assert.Equal("b@x", WorkbookStore.FindEntry(Fixtures.MultiMtf)!.AssignedUser);
        Assert.All(WorkbookStore.PaymentsForFile(WorkbookStore.FindEntry(Fixtures.MultiMtf)!), p => Assert.Equal("b@x", p.AssignedUser));
        Assert.Equal("a@x", WorkbookStore.FindEntry(Fixtures.SingleMt103)!.AssignedUser);
    }

    [Fact]
    public void Archived_file_is_located_for_open_file()
    {
        string path = WorkbookStore.ArchivedFilePath(WorkbookStore.FindEntry(Fixtures.SingleMt103)!);
        Assert.Equal(Path.Combine(DayFolder(Archive, _day), Fixtures.SingleMt103), path);
    }
}
