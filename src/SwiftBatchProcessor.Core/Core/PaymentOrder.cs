using System.Globalization;

namespace SwiftBatchApp.Core;

/// <summary>One payment order (one field-21 transaction) inside an MT101 file.</summary>
public sealed class PaymentOrder
{
    /// <summary>1-based position inside the file.</summary>
    public int Index { get; init; }

    public string SenderRef { get; set; } = "";          // 20
    public string SenderBic { get; set; } = "";          // print header "Sender :"
    public string Receiver { get; set; } = "";           // print header "Receiver :"
    public string TxnRef { get; set; } = "";             // 21
    public string Currency { get; set; } = "";           // 32B
    public decimal? Amount { get; set; }                 // 32B
    public string ExecDateRaw { get; set; } = "";        // 30 (YYMMDD)
    public string OrderingAccount { get; set; } = "";    // 50a "/account" line
    public string OrderingName { get; set; } = "";       // 50a first name line
    public string BeneficiaryAccount { get; set; } = ""; // 59a "/account" line
    public string BeneficiaryName { get; set; } = "";    // 59a first name line
    public string Bic57 { get; set; } = "";              // 57A first BIC-shaped line
    public string Bic59 { get; set; } = "";              // 59A first BIC-shaped line

    /// <summary>Creditor bank BIC: 57A when present, otherwise the BIC in 59/59A.</summary>
    public string CreditorBic => Bic57.Length > 0 ? Bic57 : Bic59;

    public bool IsMtf => PaymentTypes.IsMtf(CreditorBic);

    /// <summary>"MTF" (on-us, creditor bank is the house BIC) or "MT103".</summary>
    public string Type => IsMtf ? PaymentTypes.Mtf : PaymentTypes.Mt103;

    public DateTime? ExecutionDate => SwiftParser.ParseYyMmDd(ExecDateRaw);
}

/// <summary>Everything extracted from one .prt file.</summary>
public sealed class ParsedFile
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public List<PaymentOrder> Payments { get; } = new();

    public string SenderRef => Payments.Count > 0 ? Payments[0].SenderRef : "";
    public string SenderBic { get; set; } = "";
    public string ReceiverBic { get; set; } = "";
    public long? OsnFrom { get; set; }
    public long? OsnTo { get; set; }

    /// <summary>Set when the file could not be read or holds no payment orders.</summary>
    public string? Error { get; set; }
    public bool HasError => Error is not null;

    public int PaymentCount => Payments.Count;
    public int Mt103Count => Payments.Count(p => !p.IsMtf);
    public int MtfCount => Payments.Count(p => p.IsMtf);

    public PaymentOrder? First => Payments.Count > 0 ? Payments[0] : null;
    public DateTime? ExecutionDate => Payments.Select(p => p.ExecutionDate).FirstOrDefault(d => d.HasValue);
    public string OrderingAccount => Payments.Select(p => p.OrderingAccount).FirstOrDefault(s => s.Length > 0) ?? "";
    public string OrderingName => Payments.Select(p => p.OrderingName).FirstOrDefault(s => s.Length > 0) ?? "";

    public string TxnRefs => string.Join(", ", Payments.Select(p => p.TxnRef).Where(r => r.Length > 0));

    public IReadOnlyDictionary<string, decimal> TotalsByCurrency =>
        Payments.Where(p => p.Amount.HasValue)
                .GroupBy(p => p.Currency.Length > 0 ? p.Currency : "???")
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount!.Value));

    public string TotalsText =>
        string.Join("; ", TotalsByCurrency.OrderBy(k => k.Key)
            .Select(k => $"{k.Key} {k.Value.ToString("N2", CultureInfo.InvariantCulture)}"));
}

public static class PaymentTypes
{
    public const string Mt103 = "MT103";
    public const string Mtf = "MTF";

    /// <summary>The house BIC. MTF iff the creditor BIC is exactly this (8 or 11 with XXX).</summary>
    public const string HouseBic8 = "PIRBGRAA";

    public static bool IsMtf(string? creditorBic)
    {
        string bic = (creditorBic ?? "").Trim().ToUpperInvariant();
        return bic == HouseBic8 || bic == HouseBic8 + "XXX";
    }
}
