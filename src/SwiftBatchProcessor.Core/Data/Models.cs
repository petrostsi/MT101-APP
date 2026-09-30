using System.Globalization;
using SwiftBatchApp.Core;

namespace SwiftBatchApp.Data;

/// <summary>A processor (or the manager) in the local SQLite user table.</summary>
public sealed class AppUser
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsActive { get; set; } = true;

    /// <summary>yyMMdd of the day the user was detected (or marked) out of office; resets automatically next day.</summary>
    public string OooDate { get; set; } = "";

    /// <summary>Windows logon name used to recognise this user on their own PC.</summary>
    public string WindowsUser { get; set; } = "";

    public bool IsOooOn(DateTime day) => OooDate == day.ToString("yyMMdd", CultureInfo.InvariantCulture);
    public string Label => DisplayName.Length > 0 ? $"{DisplayName} <{Email}>" : Email;
}

/// <summary>A distributed file (SQLite Files table — engine state, idempotency and fairness counts).</summary>
public sealed class FileRecord
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string AssignedUser { get; set; } = "";
    public string ProcessedDate { get; set; } = "";   // yyyy-MM-dd
    public int PaymentCount { get; set; }
    public string TxnRefs { get; set; } = "";
    public string Status { get; set; } = FileStatus.DbOk;
    public string LoggedAt { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public string CustomerMatch { get; set; } = "";
}

/// <summary>A file received after the cut-off (or on a non-working day), waiting for its send time.</summary>
public sealed class DeferredItem
{
    public long Id { get; set; }
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string ReceivedAt { get; set; } = "";      // yyyy-MM-dd HH:mm:ss
    public string ScheduledFor { get; set; } = "";    // yyyy-MM-dd HH:mm
    public string TargetYmd { get; set; } = "";       // yyMMdd
    public bool Done { get; set; }

    public DateTime? TargetDay =>
        DateTime.TryParseExact(TargetYmd, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d) ? d : null;
}

/// <summary>An Excel row that could not be written (workbook locked) and waits in the write-behind queue.</summary>
public sealed class PendingRow
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public string RowJson { get; set; } = "";
    public string Kind { get; set; } = "";            // PendingRowKind
    public string CreatedAt { get; set; } = "";
    public int Attempts { get; set; }
    public string LastError { get; set; } = "";
}

public static class PendingRowKind
{
    public const string Registry = "registry";
    public const string Daily = "daily";
}

public static class SettingKeys
{
    public const string WatchFolder = "WatchFolder";
    public const string ArchiveRoot = "ArchiveRoot";
    public const string RegistryPath = "RegistryPath";
    public const string SenderAccount = "SenderAccount";
    public const string ManagerEmail = "ManagerEmail";
    public const string ManagerName = "ManagerName";
    public const string PollSeconds = "PollSeconds";
    public const string OooWaitSeconds = "OooWaitSeconds";
    public const string FileFilter = "FileFilter";
    public const string CutoffTime = "CutoffTime";
    public const string DeferredSendTime = "DeferredSendTime";
    public const string MasterFilePath = "MasterFilePath";
    public const string Holidays = "Holidays";
    public const string AutoStart = "AutoStart";

    /// <summary>
    /// Built-in defaults. Organisation-specific values come from SwiftBatch.defaults.json. RegistryPath is kept in
    /// the shared database for the engine, but each PC finds the shared folder through LocalConfig.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        [WatchFolder] = "",
        [ArchiveRoot] = "",
        [RegistryPath] = "",
        [SenderAccount] = "",
        [ManagerEmail] = "",
        [ManagerName] = "",
        [PollSeconds] = "300",
        [OooWaitSeconds] = "60",
        [FileFilter] = "*.prt",
        [CutoffTime] = "13:00",
        [DeferredSendTime] = "07:00",
        [MasterFilePath] = "",
        [Holidays] = "",
        [AutoStart] = "0",
    };
}

/// <summary>Typed snapshot of the engine settings (reloaded every cycle).</summary>
public sealed class EngineSettings
{
    public string WatchFolder { get; init; } = "";
    public string ArchiveRoot { get; init; } = "";
    public string RegistryPath { get; init; } = "";
    public string SenderAccount { get; init; } = "";
    public string ManagerEmail { get; init; } = "";
    public string ManagerName { get; init; } = "";
    public int PollSeconds { get; init; } = 300;
    public int OooWaitSeconds { get; init; } = 60;
    public string FileFilter { get; init; } = "*.prt";
    public TimeSpan Cutoff { get; init; } = new(13, 0, 0);
    public TimeSpan DeferredSendTime { get; init; } = new(7, 0, 0);
    public string MasterFilePath { get; init; } = "";
    public HashSet<string> Holidays { get; init; } = new();
    public bool AutoStart { get; init; }

    /// <summary>File patterns from FileFilter ("*.prt;*.txt").</summary>
    public IReadOnlyList<string> FilePatterns
    {
        get
        {
            var list = FileFilter.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return list.Count > 0 ? list : new List<string> { "*.prt" };
        }
    }

    public static EngineSettings From(IReadOnlyDictionary<string, string> kv)
    {
        string S(string key) => kv.TryGetValue(key, out string? v) ? v.Trim() : SettingKeys.Defaults.GetValueOrDefault(key, "");
        int I(string key, int fallback, int min, int max) =>
            int.TryParse(S(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, min, max) : fallback;

        return new EngineSettings
        {
            WatchFolder = S(SettingKeys.WatchFolder),
            ArchiveRoot = S(SettingKeys.ArchiveRoot),
            RegistryPath = S(SettingKeys.RegistryPath),
            SenderAccount = S(SettingKeys.SenderAccount),
            ManagerEmail = S(SettingKeys.ManagerEmail),
            ManagerName = S(SettingKeys.ManagerName),
            PollSeconds = I(SettingKeys.PollSeconds, 300, 10, 86_400),
            OooWaitSeconds = I(SettingKeys.OooWaitSeconds, 60, 0, 600),
            FileFilter = S(SettingKeys.FileFilter) is { Length: > 0 } f ? f : "*.prt",
            Cutoff = BusinessCalendar.ParseTime(S(SettingKeys.CutoffTime), new TimeSpan(13, 0, 0)),
            DeferredSendTime = BusinessCalendar.ParseTime(S(SettingKeys.DeferredSendTime), new TimeSpan(7, 0, 0)),
            MasterFilePath = S(SettingKeys.MasterFilePath),
            Holidays = BusinessCalendar.ParseHolidays(S(SettingKeys.Holidays)),
            AutoStart = S(SettingKeys.AutoStart) is "1" or "true" or "True" or "yes",
        };
    }
}
