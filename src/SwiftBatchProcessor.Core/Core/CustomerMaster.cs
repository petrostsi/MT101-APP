using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SwiftBatchApp.Core;

/// <summary>One row of the master customer workbook.</summary>
public sealed record CustomerRecord(
    int RowNumber,
    string Asc,
    string Crs,
    string Customer,
    string Instructing,
    string Bic,
    IReadOnlyList<string> Ibans,
    string Valeur)
{
    public string IbansText => string.Join(", ", Ibans);
}

public enum MatchOutcome { NotChecked, Match, NoMatch }

public sealed record MatchResult(MatchOutcome Outcome, CustomerRecord? Customer, string Reason)
{
    public bool IsNoMatch => Outcome == MatchOutcome.NoMatch;

    /// <summary>Text for the registry "Customer Match" column.</summary>
    public string RegistryText => Outcome switch
    {
        MatchOutcome.Match => "MATCH: " + Customer!.Customer,
        MatchOutcome.NoMatch => "NO MATCH: " + Reason,
        _ => "NOT CHECKED",
    };

    public static MatchResult NotChecked(string reason = "Master file not configured") => new(MatchOutcome.NotChecked, null, reason);
}

/// <summary>
/// Validates the ordering customer against the master customer workbook (first worksheet, row 1 = headers).
/// Columns are read BY POSITION: A = ASC, B = CRS, C = customer, F = instructing party, G = BIC,
/// H = IBANs (any separators), J = valeur.
/// A file matches when, for one master row, ALL of:
///   * the ordering account of field 50 is one of the row's IBANs,
///   * the first 8 characters of the sender BIC equal those of the row's BIC,
///   * the ordering name agrees with the customer (or instructing) name — normalised "contains" either way.
/// </summary>
public static class CustomerMaster
{
    private const int ColAsc = 0, ColCrs = 1, ColCustomer = 2, ColInstructing = 5, ColBic = 6, ColIbans = 7, ColValeur = 9;

    private static readonly Regex IbanSplitRx = new(@"[,;|\r\n\t]+|\s{2,}", RegexOptions.Compiled);
    private static readonly Regex IbanStartRx = new(@"^[A-Z]{2}\d{2}[A-Z0-9]{11,30}$", RegexOptions.Compiled);
    private static readonly Regex NonAlnumRx = new(@"[^\p{L}\p{N} ]+", RegexOptions.Compiled);
    private static readonly Regex SpacesRx = new(@"\s+", RegexOptions.Compiled);

    private static readonly object CacheLock = new();
    private static string _cachePath = "";
    private static DateTime _cacheStamp;
    private static IReadOnlyList<CustomerRecord> _cache = Array.Empty<CustomerRecord>();

    /// <summary>Loads (and caches by path + last write time) the master workbook.</summary>
    public static IReadOnlyList<CustomerRecord> Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Master customer file not found.", path);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        lock (CacheLock)
        {
            if (string.Equals(path, _cachePath, StringComparison.OrdinalIgnoreCase) && stamp == _cacheStamp)
                return _cache;
        }

        List<string[]> grid = ExcelReader.ReadGrid(path);
        var list = new List<CustomerRecord>();
        for (int r = 1; r < grid.Count; r++)
        {
            string[] row = grid[r];
            string Cell(int c) => c < row.Length ? row[c].Trim() : "";
            if (Cell(ColCustomer).Length == 0 && Cell(ColIbans).Length == 0) continue;
            list.Add(new CustomerRecord(r + 1, Cell(ColAsc), Cell(ColCrs), Cell(ColCustomer), Cell(ColInstructing),
                Cell(ColBic).ToUpperInvariant(), SplitIbans(Cell(ColIbans)), Cell(ColValeur)));
        }

        lock (CacheLock)
        {
            _cachePath = path;
            _cacheStamp = stamp;
            _cache = list;
        }
        return list;
    }

    /// <summary>Throws when the master file is configured but cannot be read (the engine retries the file later).</summary>
    public static MatchResult Match(ParsedFile parsed, string masterPath)
    {
        if (string.IsNullOrWhiteSpace(masterPath)) return MatchResult.NotChecked();
        return Match(parsed, Load(masterPath));
    }

    public static MatchResult Match(ParsedFile parsed, IReadOnlyList<CustomerRecord> customers)
    {
        string account = NormalizeAccount(parsed.OrderingAccount);
        string senderBic8 = Bic8(parsed.SenderBic);
        string name = NormalizeName(parsed.OrderingName);

        if (account.Length == 0) return new MatchResult(MatchOutcome.NoMatch, null, "no ordering account in field 50");

        var byIban = customers.Where(c => c.Ibans.Any(i => i == account)).ToList();
        if (byIban.Count == 0) return new MatchResult(MatchOutcome.NoMatch, null, $"account {account} not in master file");

        var byBic = byIban.Where(c => Bic8(c.Bic).Length > 0 && Bic8(c.Bic) == senderBic8).ToList();
        if (byBic.Count == 0)
            return new MatchResult(MatchOutcome.NoMatch, byIban[0],
                $"sender BIC {(parsed.SenderBic.Length > 0 ? parsed.SenderBic : "(none)")} ≠ master BIC {byIban[0].Bic}");

        CustomerRecord? hit = byBic.FirstOrDefault(c => NamesAgree(name, c.Customer) || NamesAgree(name, c.Instructing));
        if (hit is null)
            return new MatchResult(MatchOutcome.NoMatch, byBic[0],
                $"name \"{parsed.OrderingName}\" ≠ \"{byBic[0].Customer}\"");

        return new MatchResult(MatchOutcome.Match, hit, "");
    }

    // ---------------------------------------------------------------- normalisation

    internal static IReadOnlyList<string> SplitIbans(string cell)
    {
        var result = new List<string>();
        foreach (string piece in IbanSplitRx.Split(cell.ToUpperInvariant()))
        {
            string p = piece.Trim().Trim('/');
            if (p.Length == 0) continue;
            string[] words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1 && words.All(w => IbanStartRx.IsMatch(w)))
                result.AddRange(words);                        // "GR16... GR17..." single-space separated
            else
                result.Add(NormalizeAccount(p));               // "GR16 0110 1250 ..." grouped IBAN
        }
        return result.Where(s => s.Length > 0).Distinct().ToList();
    }

    internal static string NormalizeAccount(string? s) =>
        SpacesRx.Replace((s ?? "").ToUpperInvariant(), "").Trim('/');

    internal static string Bic8(string? bic)
    {
        string b = (bic ?? "").Trim().ToUpperInvariant();
        return b.Length >= 8 ? b[..8] : b;
    }

    /// <summary>Upper case, no diacritics (Greek tonos too), no punctuation, single spaces.</summary>
    internal static string NormalizeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        string decomposed = s.ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        string noMarks = sb.ToString().Normalize(NormalizationForm.FormC);
        return SpacesRx.Replace(NonAlnumRx.Replace(noMarks, ""), " ").Trim();
    }

    internal static bool NamesAgree(string normalizedOrdering, string masterName)
    {
        string m = NormalizeName(masterName);
        if (normalizedOrdering.Length == 0 || m.Length == 0) return false;
        return m.Contains(normalizedOrdering, StringComparison.Ordinal) || normalizedOrdering.Contains(m, StringComparison.Ordinal);
    }
}
