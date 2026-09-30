using System.Globalization;
using SwiftBatchApp.Core;
using static SwiftBatchApp.Core.WorkbookLayout;

namespace SwiftBatchApp.Data;

/// <summary>One Registry.xlsx row (one file) as the UI sees it.</summary>
public sealed class RegistryEntry
{
    public string FileName { get; init; } = "";
    public long? OsnFrom { get; init; }
    public long? OsnTo { get; init; }
    public int Orders { get; init; }
    public string ExecutionDate { get; init; } = "";
    public string AfterCutoff { get; init; } = "";
    public string Status { get; init; } = "";
    public int Mt103 { get; init; }
    public int Mtf { get; init; }
    public string AssignedUser { get; init; } = "";
    public DateTime? Day { get; init; }
    public string LoggedAt { get; init; } = "";
    public string TxnRefs { get; init; } = "";
    public string CustomerMatch { get; init; } = "";
    public string Valeur { get; init; } = "";

    /// <summary>Status shown in the UI: derived from the payments when the day workbook is available.</summary>
    public string DisplayStatus { get; set; } = "";

    public bool IsCompleted => DisplayStatus == FileStatus.Completed;
    public bool IsDeferred => AfterCutoff.Length > 0;
    public string DayText => DisplayDate(Day);
    public string OsnText => OsnFrom is null ? "" : OsnFrom == OsnTo || OsnTo is null ? $"{OsnFrom}" : $"{OsnFrom}–{OsnTo}";

    public MatchOutcome MatchKind =>
        CustomerMatch.StartsWith("NO MATCH", StringComparison.OrdinalIgnoreCase) ? MatchOutcome.NoMatch
        : CustomerMatch.StartsWith("MATCH", StringComparison.OrdinalIgnoreCase) ? MatchOutcome.Match
        : MatchOutcome.NotChecked;

    public string MatchedCustomer =>
        MatchKind == MatchOutcome.Match && CustomerMatch.IndexOf(':') is var i and >= 0 ? CustomerMatch[(i + 1)..].Trim() : "";

    public static RegistryEntry From(IReadOnlyDictionary<string, string> row)
    {
        string Get(string h) => row.TryGetValue(h, out string? v) ? v.Trim() : "";
        string orders = Get(ROrders);
        if (orders.Length == 0) orders = Get(RLegacyPaymentCount);
        string status = Get(RStatus);
        return new RegistryEntry
        {
            FileName = Get(RFileName),
            OsnFrom = ParseLong(Get(ROsnFrom)),
            OsnTo = ParseLong(Get(ROsnTo)),
            Orders = (int)(ParseLong(orders) ?? 0),
            ExecutionDate = Get(RExecDate),
            AfterCutoff = Get(RAfterCutoff),
            Status = status,
            DisplayStatus = FileStatus.Normalize(status),
            Mt103 = (int)(ParseLong(Get(RMt103)) ?? 0),
            Mtf = (int)(ParseLong(Get(RMtf)) ?? 0),
            AssignedUser = Get(RAssignedUser),
            Day = (ParseDate(Get(RDate)) ?? ParseDate(Get(RLoggedAt)))?.Date,
            LoggedAt = Get(RLoggedAt),
            TxnRefs = Get(RTxnRefs),
            CustomerMatch = Get(RCustomerMatch),
            Valeur = Get(RValeur),
        };
    }

    internal static long? ParseLong(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (long)Math.Round(d) : null;
}

/// <summary>One PaymentOrders_YYMMDD.xlsx row (one payment).</summary>
public sealed class PaymentEntry
{
    public DateTime Day { get; init; }
    public string AssignedUser { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SenderRef { get; init; } = "";
    public string Receiver { get; init; } = "";
    public string TxnRef { get; init; } = "";
    public string Currency { get; init; } = "";
    public decimal? Amount { get; init; }
    public string LoggedAt { get; init; } = "";
    public string Beneficiary { get; init; } = "";
    public string BeneficiaryAcct { get; init; } = "";
    public string ExecDate { get; init; } = "";
    public string OrderingCustomer { get; init; } = "";
    public string OrderingAcct { get; init; } = "";
    public string CreditorBic { get; init; } = "";
    public string Status { get; init; } = PaymentStatus.Pending;
    public string Type { get; init; } = "";

    public bool IsCompleted => Status == PaymentStatus.Completed;
    public string AmountText => DisplayFormats.Amount(Amount);
    public string DayText => DisplayDate(Day);

    public static PaymentEntry From(IReadOnlyDictionary<string, string> row, DateTime day)
    {
        string Get(string h) => row.TryGetValue(h, out string? v) ? v.Trim() : "";
        string amount = Get(DAmount);
        decimal? value = decimal.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) ? d : SwiftParser.ParseAmount(amount);
        return new PaymentEntry
        {
            Day = day.Date,
            AssignedUser = Get(DAssignedUser),
            FileName = Get(DFileName),
            SenderRef = Get(DSenderRef),
            Receiver = Get(DReceiver),
            TxnRef = Get(DTxnRef),
            Currency = Get(DCurrency),
            Amount = value,
            LoggedAt = Get(DLoggedAt),
            Beneficiary = Get(DBeneficiary),
            BeneficiaryAcct = Get(DBeneficiaryAcct),
            ExecDate = Get(DExecDate),
            OrderingCustomer = Get(DOrderingCustomer),
            OrderingAcct = Get(DOrderingAcct),
            CreditorBic = Get(DCreditorBic),
            Status = PaymentStatus.Normalize(Get(DStatus)),
            Type = Get(DType),
        };
    }
}

/// <summary>Pending work of one user (dashboard grid).</summary>
public sealed record PendingWork(string User, int PendingPayments, int PendingFiles, DateTime? OldestDay)
{
    public string OldestText => DisplayDate(OldestDay);
}
