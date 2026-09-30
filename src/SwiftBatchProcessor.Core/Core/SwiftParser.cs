using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SwiftBatchApp.Core;

/// <summary>
/// Parses SWIFT MT101 print files (.prt).
///
/// Print-format rules (see docs/MT101_FORMAT.md):
///  * Each message starts with a print header "dd/mm/yy-hh:mm:ss  MT942PMNTS-3459-190768  1" whose
///    trailing number is the OSN. Page breaks may repeat the header with the same OSN.
///  * Tag lines ("  20: Sender's Reference") carry the field DESCRIPTION; the VALUE is on the
///    following line(s), up to the next tag / header / separator line.
///  * Field 21 starts a new payment order. Sequence A fields (20, 30, 50a) seen before it are
///    inherited; sequence B fields after it (32B, 50a, 57a, 59a) belong to that payment.
///  * Raw FIN lines (":20:VALUE") are accepted too: the inline remainder is the first value line.
/// </summary>
public static class SwiftParser
{
    private const RegexOptions Rx = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex HeaderLineRx = new(@"^\s*\d{2}/\d{2}/\d{2}-\d{2}:\d{2}:\d{2}\s+[A-Z0-9_]+(?:-\d+)*-(\d+)\b", Rx);
    private static readonly Regex TagRx = new(@"^\s*(:?)(\d{2}[A-Z]?):(.*)$", Rx);
    private static readonly Regex SeparatorRx = new(@"^\s*[-=*_]{5,}", Rx);
    private static readonly Regex HeaderFieldRx = new(@"^\s*(?:Sender|Receiver)\s*:\s*[A-Z0-9]{8,11}\b", Rx);
    private static readonly Regex SenderBicRx = new(@"Sender\s*:\s*([A-Z0-9]{8,11})\b", Rx);
    private static readonly Regex ReceiverRx = new(@"Receiver\s*:\s*([A-Z0-9]{8,11})\b", Rx);
    private static readonly Regex CurrencyRx = new(@"Currency\s*:\s*([A-Z]{3})\b", Rx);
    private static readonly Regex AmountRx = new(@"#\s*([0-9][0-9.,]*)\s*#", Rx);
    private static readonly Regex RawCcyAmountRx = new(@"^([A-Z]{3})\s*([0-9][0-9.,]*)$", Rx);
    private static readonly Regex BicRx = new(@"^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$", Rx);
    private static readonly Regex YyMmDdRx = new(@"(?<!\d)(\d{6})(?!\d)", Rx);
    private static readonly Regex DmyRx = new(@"(?<!\d)(\d{2})/(\d{2})/(\d{4})(?!\d)", Rx);
    private static readonly Regex IsoDateRx = new(@"(?<!\d)(\d{4})-(\d{2})-(\d{2})(?!\d)", Rx);
    private static readonly Regex NumberedLineRx = new(@"^\d/", Rx);

    // ---------------------------------------------------------------- entry points

    public static ParsedFile ParseFile(string path)
    {
        string text;
        try
        {
            text = ReadText(path);
        }
        catch (Exception ex)
        {
            return new ParsedFile { FileName = Path.GetFileName(path), FilePath = path, Error = "Cannot read file: " + ex.Message };
        }
        return Parse(text, Path.GetFileName(path), path);
    }

    /// <summary>Reads a print file tolerating locks by other readers, NUL padding and non-UTF-8 bytes.</summary>
    public static string ReadText(string path)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }
        return text.Replace("\0", "").TrimStart('﻿');
    }

    public static ParsedFile Parse(string text, string fileName = "", string filePath = "")
    {
        var file = new ParsedFile { FileName = fileName, FilePath = filePath };
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var ctx = new MessageContext();
        PaymentOrder? cur = null;
        var osns = new List<long>();
        long? currentOsn = null;

        void Close()
        {
            if (cur is null) return;
            if (cur.Currency.Length == 0) cur.Currency = ctx.Currency;
            file.Payments.Add(cur);
            cur = null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Print header with OSN: a different OSN means a new message.
            Match h = HeaderLineRx.Match(line);
            if (h.Success)
            {
                if (long.TryParse(h.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long osn))
                {
                    if (cur is not null && currentOsn.HasValue && osn != currentOsn.Value) Close();
                    currentOsn = osn;
                    if (!osns.Contains(osn)) osns.Add(osn);
                }
                continue;
            }

            Match tm = TagRx.Match(line);
            if (tm.Success)
            {
                string tag = tm.Groups[2].Value;
                bool raw = tm.Groups[1].Value == ":";
                int last = CollectValues(lines, i, out List<string> values);
                string inline = tm.Groups[3].Value.Trim();
                if (inline.Length > 0 && (raw || values.Count == 0)) values.Insert(0, inline);

                if (tag == "20" || tag == "21") Close();
                if (tag == "21")
                    cur = ctx.StartPayment(file.Payments.Count + 1, First(values));
                else
                    ApplyTag(tag, values, ctx, cur);

                i = last;
                continue;
            }

            // Lines outside any tag: message header information.
            Match sm = SenderBicRx.Match(line);
            if (sm.Success && HeaderFieldRx.IsMatch(line))
            {
                Close();
                ctx.SenderBic = sm.Groups[1].Value;
                if (file.SenderBic.Length == 0) file.SenderBic = ctx.SenderBic;
                continue;
            }
            Match rm = ReceiverRx.Match(line);
            if (rm.Success && HeaderFieldRx.IsMatch(line))
            {
                Close();
                ctx.Receiver = rm.Groups[1].Value;
                if (file.ReceiverBic.Length == 0) file.ReceiverBic = ctx.Receiver;
                continue;
            }
            Match cm = CurrencyRx.Match(line);
            if (cm.Success)
            {
                if (cur is null) ctx.Currency = cm.Groups[1].Value;
                else if (cur.Currency.Length == 0) cur.Currency = cm.Groups[1].Value;
            }
            if (cur is not null && cur.Amount is null)
            {
                Match am = AmountRx.Match(line);
                if (am.Success) cur.Amount = ParseAmount(am.Groups[1].Value);
            }
        }
        Close();

        if (osns.Count > 0)
        {
            file.OsnFrom = osns.Min();
            file.OsnTo = osns.Count > 1 ? osns.Max() : osns[0] + Math.Max(1, file.Payments.Count) - 1;
        }
        if (file.Payments.Count == 0)
            file.Error = "No payment orders (field 21) found.";
        return file;
    }

    // ---------------------------------------------------------------- tags

    private static void ApplyTag(string tag, List<string> values, MessageContext ctx, PaymentOrder? cur)
    {
        string num = tag[..2];
        char option = tag.Length > 2 ? tag[2] : ' ';
        switch (num)
        {
            case "20":
                ctx.StartMessage(First(values));
                break;

            case "30":
            {
                string ymd = NormalizeDate(string.Join(" ", values));
                if (cur is not null) cur.ExecDateRaw = ymd;
                else ctx.ExecDate = ymd;
                break;
            }

            case "50":
            {
                // 50C / 50L = instructing party, not the ordering customer.
                if (option is 'C' or 'L') break;
                var (acct, name) = AccountAndName(values);
                if (cur is not null) { cur.OrderingAccount = acct; cur.OrderingName = name; }
                else { ctx.OrderingAccount = acct; ctx.OrderingName = name; }
                break;
            }

            case "32":
                if (cur is not null && option == 'B') ApplyAmount(values, cur);
                break;

            case "57":
                if (cur is not null) cur.Bic57 = FirstBic(values);
                break;

            case "59":
                if (cur is not null)
                {
                    var (acct, name) = AccountAndName(values);
                    cur.BeneficiaryAccount = acct;
                    cur.BeneficiaryName = name;
                    cur.Bic59 = FirstBic(values);
                }
                break;
        }
    }

    private static void ApplyAmount(List<string> values, PaymentOrder cur)
    {
        foreach (string v in values)
        {
            Match c = CurrencyRx.Match(v);
            if (c.Success && cur.Currency.Length == 0) cur.Currency = c.Groups[1].Value;
            Match a = AmountRx.Match(v);
            if (a.Success && cur.Amount is null) cur.Amount = ParseAmount(a.Groups[1].Value);
            Match r = RawCcyAmountRx.Match(v);
            if (r.Success)
            {
                if (cur.Currency.Length == 0) cur.Currency = r.Groups[1].Value;
                cur.Amount ??= ParseAmount(r.Groups[2].Value);
            }
        }
    }

    /// <summary>Value lines after the tag line, up to the next tag / header / separator.</summary>
    private static int CollectValues(string[] lines, int tagIndex, out List<string> values)
    {
        values = new List<string>();
        int j = tagIndex + 1;
        for (; j < lines.Length; j++)
        {
            string l = lines[j];
            if (TagRx.IsMatch(l) || HeaderLineRx.IsMatch(l) || SeparatorRx.IsMatch(l) || HeaderFieldRx.IsMatch(l)) break;
            if (!string.IsNullOrWhiteSpace(l)) values.Add(l.Trim());
        }
        return j - 1;
    }

    // ---------------------------------------------------------------- helpers

    private static string First(List<string> values) => values.Count > 0 ? values[0] : "";

    /// <summary>("/account" line without the slash, first name line) for 50a / 59a.</summary>
    internal static (string Account, string Name) AccountAndName(IEnumerable<string> values)
    {
        string acct = "", name = "";
        foreach (string v in values)
        {
            if (v.StartsWith('/'))
            {
                if (acct.Length == 0) acct = v.TrimStart('/').Replace(" ", "");
            }
            else if (name.Length == 0)
            {
                name = NumberedLineRx.IsMatch(v) ? v[2..].Trim() : v;
            }
        }
        return (acct, name);
    }

    /// <summary>First BIC-shaped line, skipping "/account" lines.</summary>
    internal static string FirstBic(IEnumerable<string> values)
    {
        foreach (string v in values)
        {
            if (v.StartsWith('/')) continue;
            string t = v.Trim().ToUpperInvariant();
            if (BicRx.IsMatch(t)) return t;
        }
        return "";
    }

    /// <summary>Parses "1.234,56" (SWIFT print), "1234,56" (FIN) and "1,234.56".</summary>
    public static decimal? ParseAmount(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
        string normalized;
        if (lastComma >= 0 && lastDot >= 0)
        {
            normalized = lastComma > lastDot
                ? s.Replace(".", "").Replace(',', '.')
                : s.Replace(",", "");
        }
        else if (lastComma >= 0)
        {
            normalized = s.Replace(',', '.');
        }
        else if (lastDot >= 0 && s.Count(ch => ch == '.') == 1 && s.Length - lastDot - 1 != 3)
        {
            normalized = s;                      // "1234.5" decimal point
        }
        else
        {
            normalized = s.Replace(".", "");     // "1.234" / "1.234.567" thousands
        }
        normalized = normalized.TrimEnd('.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal d) ? d : null;
    }

    /// <summary>Normalizes a date value to YYMMDD ("" when none is found).</summary>
    internal static string NormalizeDate(string s)
    {
        Match m = YyMmDdRx.Match(s);
        if (m.Success && ParseYyMmDd(m.Groups[1].Value).HasValue) return m.Groups[1].Value;
        m = DmyRx.Match(s);
        if (m.Success) return m.Groups[3].Value[2..] + m.Groups[2].Value + m.Groups[1].Value;
        m = IsoDateRx.Match(s);
        if (m.Success) return m.Groups[1].Value[2..] + m.Groups[2].Value + m.Groups[3].Value;
        return "";
    }

    public static DateTime? ParseYyMmDd(string? raw) =>
        !string.IsNullOrEmpty(raw) &&
        DateTime.TryParseExact(raw, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)
            ? d : null;

    // ---------------------------------------------------------------- state

    private sealed class MessageContext
    {
        public string SenderRef = "", SenderBic = "", Receiver = "", Currency = "", ExecDate = "";
        public string OrderingAccount = "", OrderingName = "";

        public void StartMessage(string senderRef)
        {
            SenderRef = senderRef;
            ExecDate = "";
            OrderingAccount = "";
            OrderingName = "";
        }

        public PaymentOrder StartPayment(int index, string txnRef) => new()
        {
            Index = index,
            TxnRef = txnRef,
            SenderRef = SenderRef,
            SenderBic = SenderBic,
            Receiver = Receiver,
            ExecDateRaw = ExecDate,
            OrderingAccount = OrderingAccount,
            OrderingName = OrderingName,
        };
    }
}
