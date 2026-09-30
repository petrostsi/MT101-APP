using System.Globalization;

namespace SwiftBatchApp.Core;

/// <summary>Column headers and folder layout of the Excel outputs (the business-facing contract).</summary>
public static class WorkbookLayout
{
    // ---------------------------------------------------------------- Registry.xlsx (1 row per file)

    public const string RegistrySheet = "Registry";

    public const string RFileName = "File Name";
    public const string ROsnFrom = "OSN from";
    public const string ROsnTo = "OSN to";
    public const string ROrders = "Orders";
    public const string RLegacyPaymentCount = "Payment Count";
    public const string RExecDate = "Execution Date";
    public const string RAfterCutoff = "After cut-off / Real exec. date";
    public const string RStatus = "Status";
    public const string RMt103 = "MT103";
    public const string RMtf = "MTF";
    public const string RAssignedUser = "Assigned User";
    public const string RDate = "Date";
    public const string RLoggedAt = "Logged At";
    public const string RTxnRefs = "Transaction Refs";
    public const string RCustomerMatch = "Customer Match";
    public const string RValeur = "Valeur";

    /// <summary>The first 10 columns are business-required in this exact order; the rest are internal.</summary>
    public static readonly string[] RegistryHeaders =
    {
        RFileName, ROsnFrom, ROsnTo, ROrders, RExecDate, RAfterCutoff, RStatus, RMt103, RMtf, RAssignedUser,
        RDate, RLoggedAt, RTxnRefs, RCustomerMatch, RValeur,
    };

    // ---------------------------------------------------------------- PaymentOrders_YYMMDD.xlsx (1 row per payment)

    public const string DailySheet = "Payments";

    public const string DAssignedUser = "Assigned User";
    public const string DFileName = "File Name";
    public const string DSenderRef = "Sender Ref (20)";
    public const string DReceiver = "Receiver";
    public const string DTxnRef = "Txn Ref (21)";
    public const string DCurrency = "Currency";
    public const string DAmount = "Amount";
    public const string DLoggedAt = "Logged At";
    public const string DBeneficiary = "Beneficiary";
    public const string DBeneficiaryAcct = "Beneficiary Acct";
    public const string DExecDate = "Exec Date (30)";
    public const string DOrderingCustomer = "Ordering Customer";
    public const string DStatus = "Status";
    public const string DType = "Type";
    public const string DOrderingAcct = "Ordering Acct";
    public const string DCreditorBic = "Creditor BIC";

    /// <summary>Column A must be the assigned user.</summary>
    public static readonly string[] DailyHeaders =
    {
        DAssignedUser, DFileName, DSenderRef, DReceiver, DTxnRef, DCurrency, DAmount, DLoggedAt,
        DBeneficiary, DBeneficiaryAcct, DExecDate, DOrderingCustomer, DStatus, DType,
        DOrderingAcct, DCreditorBic,
    };

    // ---------------------------------------------------------------- folders

    /// <summary>ArchiveRoot\YYYY\MM\DD (e.g. 8 Jul 2026 → …\2026\07\08).</summary>
    public static string DayFolder(string archiveRoot, DateTime day) =>
        Path.Combine(archiveRoot, day.ToString("yyyy", CultureInfo.InvariantCulture),
            day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));

    public static string DailyWorkbookPath(string archiveRoot, DateTime day) =>
        Path.Combine(DayFolder(archiveRoot, day), $"PaymentOrders_{day.ToString("yyMMdd", CultureInfo.InvariantCulture)}.xlsx");

    public static string DuplicatesFolder(string archiveRoot, DateTime day) =>
        Path.Combine(DayFolder(archiveRoot, day), "Duplicates");

    // ---------------------------------------------------------------- formats

    public const string DateKeyFormat = "yyyy-MM-dd";
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    public const string DisplayDateFormat = "dd/MM/yyyy";

    public static string DateKey(DateTime d) => d.ToString(DateKeyFormat, CultureInfo.InvariantCulture);
    public static string Timestamp(DateTime d) => d.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    public static string DisplayDate(DateTime? d) => d?.ToString(DisplayDateFormat, CultureInfo.InvariantCulture) ?? "";

    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string t = s.Trim();
        string[] formats = { DateKeyFormat, TimestampFormat, DisplayDateFormat, "d/M/yyyy", "yyyy-MM-dd HH:mm", "dd/MM/yyyy HH:mm:ss" };
        if (DateTime.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)) return d;
        // Excel serial date (user re-typed the cell)
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double serial) && serial is > 20000 and < 80000)
            return DateTime.FromOADate(serial);
        return null;
    }
}

/// <summary>File-level status values as stored in Registry.xlsx and SQLite.</summary>
public static class FileStatus
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Escalated = "ESCALATED";
    public const string NoMatch = "NO MATCH";
    public const string ParseError = "PARSE ERROR";

    /// <summary>SQLite value for a normally assigned file (displayed as Pending).</summary>
    public const string DbOk = "OK";

    /// <summary>Maps stored values (incl. legacy OK / PROCESSED) to the display vocabulary.</summary>
    public static string Normalize(string? raw)
    {
        string s = (raw ?? "").Trim().ToUpperInvariant();
        return s switch
        {
            "" or "OK" or "PENDING" or "NEW" => Pending,
            "COMPLETED" or "PROCESSED" or "DONE" => Completed,
            "ESCALATED" => Escalated,
            "NO MATCH" or "NOMATCH" or "NO_MATCH" => NoMatch,
            "PARSE ERROR" or "PARSE_ERROR" => ParseError,
            _ => raw!.Trim(),
        };
    }

    /// <summary>Statuses whose files do not count towards a processor's fair share.</summary>
    public static bool ExcludedFromFairness(string? status) =>
        Normalize(status) is Escalated or NoMatch;

    public static bool IsSpecial(string? status) => Normalize(status) is Escalated or NoMatch or ParseError;
}

/// <summary>Payment-level status values in the daily workbook.</summary>
public static class PaymentStatus
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";

    public static string Normalize(string? raw) =>
        (raw ?? "").Trim().ToUpperInvariant() is "COMPLETED" or "DONE" or "PROCESSED" ? Completed : Pending;
}
