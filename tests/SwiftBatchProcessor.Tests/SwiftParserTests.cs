using SwiftBatchApp.Core;

namespace SwiftBatchApp.Tests;

public class SwiftParserTests
{
    [Fact]
    public void Single_payment_file_extracts_every_field()
    {
        ParsedFile f = SwiftParser.ParseFile(Fixtures.Path(Fixtures.SingleMt103));

        Assert.False(f.HasError, f.Error);
        Assert.Single(f.Payments);
        Assert.Equal(190768, f.OsnFrom);
        Assert.Equal(190768, f.OsnTo);
        Assert.Equal("ACMEGRA1XXX", f.SenderBic);
        Assert.Equal("PIRBGRAAXXX", f.ReceiverBic);
        Assert.Equal("REF0663459", f.SenderRef);
        Assert.Equal(new DateTime(2026, 7, 6), f.ExecutionDate);
        Assert.Equal("GR7201100000000012345678901", f.OrderingAccount);
        Assert.Equal("ACME TRADING S.A.", f.OrderingName);

        PaymentOrder p = f.Payments[0];
        Assert.Equal(1, p.Index);
        Assert.Equal("TXN0000001", p.TxnRef);
        Assert.Equal("EUR", p.Currency);
        Assert.Equal(12345.67m, p.Amount);
        Assert.Equal("CHASGB2L", p.CreditorBic);
        Assert.Equal(PaymentTypes.Mt103, p.Type);
        Assert.Equal("GB29NWBK60161331926819", p.BeneficiaryAccount);
        Assert.Equal("BENEFICIARY LTD", p.BeneficiaryName);
        Assert.Equal("PIRBGRAAXXX", p.Receiver);
        Assert.Equal(1, f.Mt103Count);
        Assert.Equal(0, f.MtfCount);
    }

    [Fact]
    public void Multi_payment_file_classifies_MT103_and_MTF_and_derives_OSN_range()
    {
        ParsedFile f = SwiftParser.ParseFile(Fixtures.Path(Fixtures.MultiMtf));

        Assert.Equal(3, f.PaymentCount);
        Assert.Equal(200100, f.OsnFrom);
        Assert.Equal(200102, f.OsnTo);                      // single header + 3 orders → from + N − 1
        Assert.Equal(new[] { "MT103", "MTF", "MTF" }, f.Payments.Select(p => p.Type));
        Assert.Equal(1, f.Mt103Count);
        Assert.Equal(2, f.MtfCount);
        Assert.Equal("PIRBGRAAXXX", f.Payments[2].CreditorBic);   // no 57A → 59A BIC
        Assert.Equal("USD", f.Payments[2].Currency);
        Assert.Equal(99999.99m, f.Payments[2].Amount);
        Assert.Equal(250.50m, f.Payments[1].Amount);
        Assert.All(f.Payments, p => Assert.Equal("REF0663460", p.SenderRef));
        Assert.All(f.Payments, p => Assert.Equal("GR7201100000000012345678901", p.OrderingAccount));
        Assert.Equal("TXN0000101, TXN0000102, TXN0000103", f.TxnRefs);
        Assert.Equal(1250.50m, f.TotalsByCurrency["EUR"]);
    }

    [Fact]
    public void Multi_message_file_keeps_each_message_context_and_survives_page_breaks()
    {
        ParsedFile f = SwiftParser.ParseFile(Fixtures.Path(Fixtures.MultiMessage));

        Assert.Equal(3, f.PaymentCount);
        Assert.Equal(300010, f.OsnFrom);
        Assert.Equal(300011, f.OsnTo);                      // repeated headers → min / max

        PaymentOrder p1 = f.Payments[0], p2 = f.Payments[1], p3 = f.Payments[2];
        Assert.Equal("REFBETA01", p1.SenderRef);
        Assert.Equal("REFBETA01", p2.SenderRef);
        Assert.Equal("REFBETA02", p3.SenderRef);

        // Payment 2 continues after a page-break header with the same OSN.
        Assert.Equal(750.25m, p2.Amount);
        Assert.Equal("PIRBGRAAXXX", p2.CreditorBic);
        Assert.Equal(PaymentTypes.Mtf, p2.Type);
        Assert.Equal("ONUS CLIENT", p2.BeneficiaryName);

        Assert.Equal(new DateTime(2026, 7, 8), p1.ExecutionDate);
        Assert.Equal(new DateTime(2026, 7, 9), p3.ExecutionDate);
        Assert.Equal("GR1101400000000000000000222", p3.OrderingAccount);
        Assert.Equal("GBP", p3.Currency);
        Assert.Equal(1234567.89m, p3.Amount);
        Assert.Equal("BARCGB22", p3.CreditorBic);
    }

    [Fact]
    public void File_without_field_21_is_a_parse_error()
    {
        ParsedFile f = SwiftParser.ParseFile(Fixtures.Path(Fixtures.NotMt101));
        Assert.True(f.HasError);
        Assert.Empty(f.Payments);
    }

    [Fact]
    public void Missing_file_is_a_parse_error_not_an_exception()
    {
        ParsedFile f = SwiftParser.ParseFile(Path.Combine(Path.GetTempPath(), "does-not-exist.prt"));
        Assert.True(f.HasError);
        Assert.StartsWith("Cannot read file", f.Error);
    }

    [Fact]
    public void Nul_bytes_and_latin1_text_are_tolerated()
    {
        using var tmp = new TempDir();
        byte[] original = File.ReadAllBytes(Fixtures.Path(Fixtures.SingleMt103));
        byte[] withJunk = original.Concat(new byte[] { 0xE9, 0x00, 0x00, 0x00 }).ToArray();   // invalid UTF-8 + NUL padding
        string path = tmp["junk.prt"];
        File.WriteAllBytes(path, withJunk);

        ParsedFile f = SwiftParser.ParseFile(path);
        Assert.Single(f.Payments);
        Assert.Equal(12345.67m, f.Payments[0].Amount);
    }

    [Fact]
    public void Raw_FIN_format_is_accepted()
    {
        const string text = ":20:REF1\r\n:30:260710\r\n:50H:/GR11\r\nCLIENT AE\r\n:21:TX1\r\n:32B:EUR100,50\r\n:57A:PIRBGRAA\r\n:59:/GR22\r\nBENEF\r\n";
        ParsedFile f = SwiftParser.Parse(text, "raw.prt");

        PaymentOrder p = Assert.Single(f.Payments);
        Assert.Equal("REF1", p.SenderRef);
        Assert.Equal("TX1", p.TxnRef);
        Assert.Equal("EUR", p.Currency);
        Assert.Equal(100.50m, p.Amount);
        Assert.Equal(PaymentTypes.Mtf, p.Type);
        Assert.Equal("CLIENT AE", p.OrderingName);
        Assert.Equal("BENEF", p.BeneficiaryName);
        Assert.Equal(new DateTime(2026, 7, 10), p.ExecutionDate);
    }

    [Fact]
    public void Instructing_party_50C_is_not_taken_as_ordering_customer()
    {
        const string text =
            "  20: Sender's Reference\n    R1\n" +
            "  50C: Instructing Party\n    INSTGRAA\n" +
            "  50H: Ordering Customer\n    /GR99\n    REAL CUSTOMER\n" +
            "  21: Transaction Reference\n    T1\n" +
            "  32B: Currency/Amount\n    Currency : EUR\n    Amount : #10,00#\n";
        ParsedFile f = SwiftParser.Parse(text);
        Assert.Equal("GR99", f.OrderingAccount);
        Assert.Equal("REAL CUSTOMER", f.OrderingName);
    }

    [Theory]
    [InlineData("PIRBGRAA", true)]
    [InlineData("PIRBGRAAXXX", true)]
    [InlineData(" pirbgraa ", true)]
    [InlineData("PIRBGRAA123", false)]
    [InlineData("PIRBGRA", false)]
    [InlineData("CHASGB2L", false)]
    [InlineData("", false)]
    public void MTF_iff_creditor_BIC_is_exactly_the_house_BIC(string bic, bool mtf) =>
        Assert.Equal(mtf, PaymentTypes.IsMtf(bic));

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234.567,89", 1234567.89)]
    [InlineData("1.234", 1234)]
    [InlineData("12.5", 12.5)]
    [InlineData("0,5", 0.5)]
    [InlineData("100,", 100)]
    public void Amounts_parse_in_every_notation(string text, double expected) =>
        Assert.Equal((decimal)expected, SwiftParser.ParseAmount(text));

    /// <summary>
    /// Runs against the REAL sample when it is present locally (Fixtures/real/00663459.prt is git-ignored
    /// because real files may contain customer data). Expectations from the handoff notes.
    /// </summary>
    [Fact]
    public void Real_sample_00663459_when_available()
    {
        string path = Fixtures.Path(Path.Combine("real", "00663459.prt"));
        if (!File.Exists(path)) return;

        ParsedFile f = SwiftParser.ParseFile(path);
        Assert.False(f.HasError, f.Error);
        Assert.Single(f.Payments);
        Assert.Equal(190768, f.OsnFrom);
        Assert.Equal("CHASGB2L", f.Payments[0].CreditorBic);
        Assert.Equal(PaymentTypes.Mt103, f.Payments[0].Type);
        Assert.StartsWith("GR72", f.OrderingAccount);
    }
}
