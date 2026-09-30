using SwiftBatchApp.Core;
using static SwiftBatchApp.Core.WorkbookLayout;

namespace SwiftBatchApp.Data;

/// <summary>
/// The UI's view of the shared Excel workbooks (source of truth on every PC, no SQLite needed).
/// Reads are lock-free and cached by file timestamp; the UI polls <see cref="Reload"/> every 60 s.
/// Write-backs (payment status, completion, reassignment) go through <see cref="WorkbookOps"/>.
/// Thread-safe: loads may run on a background thread.
/// </summary>
public static class WorkbookStore
{
    private static readonly object Gate = new();
    private static List<RegistryEntry> _registry = new();
    private static string _registryPath = "";
    private static DateTime _registryStamp;
    private static readonly Dictionary<string, (DateTime Stamp, List<PaymentEntry> Rows)> Days = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Days back for which file statuses are re-derived from the day workbooks on every reload.</summary>
    public static int DeriveWindowDays { get; set; } = 45;

    public static DateTime? LastLoadedAt { get; private set; }
    public static string LastError { get; private set; } = "";

    /// <summary>Raised (on the loading thread) after a reload or a write-back.</summary>
    public static event Action? Changed;

    /// <summary>Overridable in tests; defaults to the local settings / session.</summary>
    internal static Func<string> RegistryPathProvider = () => LocalConfig.RegistryPath;
    internal static Func<string> ArchiveRootProvider = () => Session.ArchiveRoot;

    public static string RegistryPath => RegistryPathProvider();
    public static string ArchiveRoot => ArchiveRootProvider();

    public static IReadOnlyList<RegistryEntry> Registry
    {
        get { lock (Gate) return _registry; }
    }

    // ---------------------------------------------------------------- loading

    /// <summary>Reloads Registry.xlsx when it changed (or always with <paramref name="force"/>).</summary>
    public static void Reload(bool force = false)
    {
        string path = RegistryPath;
        try
        {
            if (path.Length == 0 || !File.Exists(path))
            {
                lock (Gate)
                {
                    _registry = new List<RegistryEntry>();
                    _registryPath = path;
                }
                LastError = path.Length == 0 ? "The registry path is not set." : $"Registry not found: {path}";
            }
            else
            {
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                bool unchanged;
                lock (Gate) unchanged = !force && _registryStamp == stamp && string.Equals(_registryPath, path, StringComparison.OrdinalIgnoreCase);
                if (!unchanged)
                {
                    var list = ExcelReader.ReadTable(path).Rows
                        .Select(RegistryEntry.From)
                        .Where(e => e.FileName.Length > 0)
                        .ToList();
                    DeriveStatuses(list);
                    lock (Gate)
                    {
                        _registry = list;
                        _registryPath = path;
                        _registryStamp = stamp;
                    }
                }
                else
                {
                    DeriveStatuses(Registry);      // day workbooks may have changed on their own
                }
                LastError = "";
            }
        }
        catch (Exception ex)
        {
            LastError = $"Cannot read the registry: {ex.Message}";
        }
        LastLoadedAt = DateTime.Now;
        RaiseChanged();
    }

    /// <summary>Forgets the caches so the next read goes to disk.</summary>
    public static void MarkDirty()
    {
        lock (Gate)
        {
            _registryStamp = default;
            Days.Clear();
        }
    }

    /// <summary>Payments of one day (lazy, cached by the workbook's timestamp).</summary>
    public static IReadOnlyList<PaymentEntry> PaymentsForDay(DateTime day)
    {
        string root = ArchiveRoot;
        if (root.Length == 0) return Array.Empty<PaymentEntry>();
        string path = DailyWorkbookPath(root, day);
        try
        {
            if (!File.Exists(path)) return Array.Empty<PaymentEntry>();
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            lock (Gate)
            {
                if (Days.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Rows;
            }
            var rows = ExcelReader.ReadTable(path).Rows
                .Select(r => PaymentEntry.From(r, day))
                .Where(p => p.FileName.Length > 0)
                .ToList();
            lock (Gate) Days[path] = (stamp, rows);
            return rows;
        }
        catch (Exception ex)
        {
            LastError = $"Cannot read {Path.GetFileName(path)}: {ex.Message}";
            return Array.Empty<PaymentEntry>();
        }
    }

    public static List<PaymentEntry> PaymentsInRange(DateTime from, DateTime to)
    {
        var list = new List<PaymentEntry>();
        foreach (DateTime day in EachDay(from, to)) list.AddRange(PaymentsForDay(day));
        return list;
    }

    public static List<PaymentEntry> PaymentsForFile(RegistryEntry entry) =>
        entry.Day is { } day
            ? PaymentsForDay(day).Where(p => Same(p.FileName, entry.FileName)).ToList()
            : new List<PaymentEntry>();

    public static List<RegistryEntry> EntriesInRange(DateTime from, DateTime to) =>
        Registry.Where(e => e.Day is { } d && d >= from.Date && d <= to.Date).ToList();

    public static RegistryEntry? FindEntry(string fileName) =>
        Registry.LastOrDefault(e => Same(e.FileName, fileName));

    /// <summary>Open work per user, computed from the payments of every day that still has open files.</summary>
    public static List<PendingWork> PendingByUser()
    {
        var days = Registry.Where(e => e.Day.HasValue && e.DisplayStatus != FileStatus.Completed)
                           .Select(e => e.Day!.Value).Distinct().ToList();
        var open = days.SelectMany(PaymentsForDay).Where(p => !p.IsCompleted).ToList();

        // Files without payment rows (e.g. parse errors) still count as open files.
        var emptyFiles = Registry.Where(e => e.DisplayStatus != FileStatus.Completed && e.Orders == 0).ToList();

        return open.Select(p => (p.AssignedUser, p.FileName, p.Day, Payments: 1))
            .Concat(emptyFiles.Select(e => (e.AssignedUser, e.FileName, Day: e.Day ?? DateTime.Today, Payments: 0)))
            .GroupBy(x => x.AssignedUser, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PendingWork(g.Key, g.Sum(x => x.Payments),
                g.Select(x => x.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count(), g.Min(x => x.Day)))
            .OrderByDescending(w => w.PendingPayments)
            .ToList();
    }

    // ---------------------------------------------------------------- write-backs

    /// <summary>Sets payment statuses and re-derives the files' statuses. Returns a warning (or null).</summary>
    public static string? SetPaymentStatus(IEnumerable<PaymentEntry> payments, string status)
    {
        var byDay = payments.GroupBy(p => p.Day.Date).ToList();
        foreach (var day in byDay)
            WorkbookOps.SetPaymentStatus(DailyWorkbookPath(ArchiveRoot, day.Key), day.Select(p => (p.FileName, p.TxnRef)), status);
        return AfterWrite(byDay.Select(d => d.Key));
    }

    /// <summary>Marks every payment of a file (and the file) as completed. Returns a warning (or null).</summary>
    public static string? MarkFileCompleted(RegistryEntry entry)
    {
        if (entry.Day is not { } day) throw new InvalidOperationException("The registry row has no date.");
        string dailyPath = DailyWorkbookPath(ArchiveRoot, day);
        bool hasPayments = File.Exists(dailyPath) && PaymentsForFile(entry).Count > 0;
        if (hasPayments)
        {
            WorkbookOps.SetFilePaymentsStatus(dailyPath, entry.FileName, PaymentStatus.Completed);
            return AfterWrite(new[] { day });
        }
        WorkbookOps.SetRegistryStatus(RegistryPath, entry.FileName, FileStatus.Completed);
        return AfterWrite(Array.Empty<DateTime>());
    }

    /// <summary>Rewrites the assignee in both workbooks (and the local DB on the manager PC).</summary>
    public static void Reassign(RegistryEntry entry, string newUser)
    {
        if (entry.Day is not { } day) throw new InvalidOperationException("The registry row has no date.");
        WorkbookOps.Reassign(RegistryPath, DailyWorkbookPath(ArchiveRoot, day), entry.FileName, newUser);
        if (Session.IsManager)
        {
            string? status = entry.DisplayStatus is FileStatus.Escalated or FileStatus.NoMatch ? FileStatus.DbOk : null;
            try { AppDb.UpdateFileAssignment(entry.FileName, newUser, status); } catch { /* history mirror only */ }
        }
        MarkDirty();
        Reload(force: true);
    }

    /// <summary>Path of the archived .prt of a registry entry ("" when it cannot be located).</summary>
    public static string ArchivedFilePath(RegistryEntry entry)
    {
        if (entry.Day is not { } day || ArchiveRoot.Length == 0) return "";
        string path = Path.Combine(DayFolder(ArchiveRoot, day), entry.FileName);
        return File.Exists(path) ? path : "";
    }

    private static string? AfterWrite(IEnumerable<DateTime> days)
    {
        string? warning = null;
        try
        {
            WorkbookOps.SyncRegistryStatuses(RegistryPath, ArchiveRoot, days);
        }
        catch (Exception ex)
        {
            warning = $"The payments were updated, but Registry.xlsx could not be updated right now ({ex.Message}). " +
                      "Its Status column will catch up automatically.";
        }
        MarkDirty();
        Reload(force: true);
        return warning;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>File status shown in the UI follows the payments of its day workbook.</summary>
    private static void DeriveStatuses(IEnumerable<RegistryEntry> entries)
    {
        // Only recent days and files the registry still shows as open: old, completed history is not re-read.
        DateTime since = DateTime.Today.AddDays(-DeriveWindowDays);
        foreach (var group in entries
                     .Where(e => e.Day is { } d && (d >= since || FileStatus.Normalize(e.Status) != FileStatus.Completed))
                     .GroupBy(e => e.Day!.Value))
        {
            var byFile = PaymentsForDay(group.Key)
                .GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.All(p => p.IsCompleted), StringComparer.OrdinalIgnoreCase);
            foreach (RegistryEntry e in group)
            {
                string stored = FileStatus.Normalize(e.Status);
                e.DisplayStatus = byFile.TryGetValue(e.FileName, out bool done) ? WorkbookOps.Derive(stored, done) : stored;
            }
        }
    }

    public static IEnumerable<DateTime> EachDay(DateTime from, DateTime to)
    {
        DateTime start = from.Date, end = to.Date;
        if (end < start) (start, end) = (end, start);
        if ((end - start).TotalDays > 400) start = end.AddDays(-400);
        for (DateTime d = start; d <= end; d = d.AddDays(1)) yield return d;
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* UI handler errors must not break loading */ }
    }
}
