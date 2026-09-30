using SwiftBatchApp.Core;
using static SwiftBatchApp.Core.WorkbookLayout;

namespace SwiftBatchApp.Data;

/// <summary>
/// Stateless write-backs to the Excel workbooks shared by the engine (manager PC) and the UI (every PC).
/// File status is DERIVED from payment status: a file is Completed iff all its payments are Completed.
/// </summary>
public static class WorkbookOps
{
    /// <summary>Sets the status of the given payments (FileName + Txn Ref) in a day workbook. Returns rows changed.</summary>
    public static int SetPaymentStatus(string dailyPath, IEnumerable<(string FileName, string TxnRef)> payments, string status)
    {
        var keys = new HashSet<string>(payments.Select(p => Key(p.FileName, p.TxnRef)), StringComparer.OrdinalIgnoreCase);
        return ExcelWriter.UpdateRows(dailyPath, row =>
            keys.Contains(Key(row.GetValueOrDefault(DFileName, ""), row.GetValueOrDefault(DTxnRef, ""))) && PaymentStatus.Normalize(row.GetValueOrDefault(DStatus, "")) != status
                ? new Dictionary<string, object?> { [DStatus] = status }
                : null);
    }

    /// <summary>Sets the status of every payment of one file in a day workbook.</summary>
    public static int SetFilePaymentsStatus(string dailyPath, string fileName, string status) =>
        ExcelWriter.UpdateRows(dailyPath, row =>
            Same(row.GetValueOrDefault(DFileName, ""), fileName) && PaymentStatus.Normalize(row.GetValueOrDefault(DStatus, "")) != status
                ? new Dictionary<string, object?> { [DStatus] = status }
                : null);

    /// <summary>
    /// Brings the registry Status column in line with the payments of the given days.
    /// Reads lock-free first and writes only when something differs. Returns rows changed.
    /// </summary>
    public static int SyncRegistryStatuses(string registryPath, string archiveRoot, IEnumerable<DateTime> days)
    {
        if (!File.Exists(registryPath)) return 0;
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SheetTable registry = ExcelReader.ReadTable(registryPath);

        foreach (DateTime day in days.Select(d => d.Date).Distinct())
        {
            string dailyPath = DailyWorkbookPath(archiveRoot, day);
            if (!File.Exists(dailyPath)) continue;
            Dictionary<string, bool> allDone = ExcelReader.ReadTable(dailyPath).Rows
                .Where(r => r.GetValueOrDefault(DFileName, "").Length > 0)
                .GroupBy(r => r[DFileName], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.All(r => PaymentStatus.Normalize(r.GetValueOrDefault(DStatus)) == PaymentStatus.Completed),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var row in registry.Rows)
            {
                string file = row.GetValueOrDefault(RFileName, "");
                if (!allDone.TryGetValue(file, out bool done)) continue;
                if (ParseDate(row.GetValueOrDefault(RDate))?.Date is { } d && d != day) continue;
                string current = FileStatus.Normalize(row.GetValueOrDefault(RStatus));
                string target = Derive(current, done);
                if (target != current) desired[file] = target;
            }
        }
        if (desired.Count == 0) return 0;

        return ExcelWriter.UpdateRows(registryPath, row =>
            desired.TryGetValue(row.GetValueOrDefault(RFileName, ""), out string? s) && FileStatus.Normalize(row.GetValueOrDefault(RStatus, "")) != s
                ? new Dictionary<string, object?> { [RStatus] = s }
                : null);
    }

    /// <summary>Sets the registry status of one file directly (files without payment rows).</summary>
    public static int SetRegistryStatus(string registryPath, string fileName, string status) =>
        ExcelWriter.UpdateRows(registryPath, row =>
            Same(row.GetValueOrDefault(RFileName, ""), fileName) && FileStatus.Normalize(row.GetValueOrDefault(RStatus, "")) != status
                ? new Dictionary<string, object?> { [RStatus] = status }
                : null);

    /// <summary>Rewrites the assigned user in the day workbook (column A) and the registry.</summary>
    public static void Reassign(string registryPath, string dailyPath, string fileName, string newUser)
    {
        if (File.Exists(dailyPath))
        {
            ExcelWriter.UpdateRows(dailyPath, row =>
                Same(row.GetValueOrDefault(DFileName, ""), fileName) ? new Dictionary<string, object?> { [DAssignedUser] = newUser } : null);
        }
        ExcelWriter.UpdateRows(registryPath, row =>
        {
            if (!Same(row.GetValueOrDefault(RFileName, ""), fileName)) return null;
            var updates = new Dictionary<string, object?> { [RAssignedUser] = newUser };
            string status = FileStatus.Normalize(row.GetValueOrDefault(RStatus, ""));
            if (status is FileStatus.Escalated or FileStatus.NoMatch) updates[RStatus] = FileStatus.Pending;
            return updates;
        });
    }

    /// <summary>Completed iff all payments are completed; a Completed file with open payments reverts to Pending.</summary>
    public static string Derive(string currentNormalized, bool allPaymentsCompleted) =>
        allPaymentsCompleted ? FileStatus.Completed
        : currentNormalized == FileStatus.Completed ? FileStatus.Pending
        : currentNormalized;

    public static string Key(string fileName, string txnRef) => $"{fileName.Trim()}|{txnRef.Trim()}";

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
