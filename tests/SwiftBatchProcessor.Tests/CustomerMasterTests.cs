using SwiftBatchApp.Core;

namespace SwiftBatchApp.Tests;

public class CustomerMasterTests
{
    // Master columns are read by POSITION: A ASC, B CRS, C customer, F instructing, G BIC, H IBANs, J valeur.
    private static readonly string[] MasterHeaders = { "ASC", "CRS", "Customer", "D", "E", "Instructing", "BIC", "IBANs", "I", "Valeur" };

    internal static string CreateMaster(string path, params (string Customer, string Bic, string Ibans, string Valeur)[] rows)
    {
        ExcelWriter.EnsureWorkbook(path, "Customers", MasterHeaders);
        ExcelWriter.AppendRows(path, rows.Select((r, i) => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["ASC"] = $"A{i + 1}", ["CRS"] = $"C{i + 1}", ["Customer"] = r.Customer, ["Instructing"] = "",
            ["BIC"] = r.Bic, ["IBANs"] = r.Ibans, ["Valeur"] = r.Valeur,
        }).ToList());
        return path;
    }

    private static ParsedFile Sample => SwiftParser.ParseFile(Fixtures.Path(Fixtures.SingleMt103));
    // ordering account GR7201100000000012345678901, name "ACME TRADING S.A.", sender BIC ACMEGRA1XXX

    [Fact]
    public void Matches_on_iban_bic8_and_normalised_name()
    {
        using var tmp = new TempDir();
        string master = CreateMaster(tmp["master.xlsx"],
            ("Other Client", "OTHRGRA1", "GR0000000000000000000000001", "T+0"),
            ("Acme Trading SA", "ACMEGRA1", "GR72 0110 0000 0000 1234 5678 901; DE89370400440532013000", "T+1"));

        MatchResult r = CustomerMaster.Match(Sample, master);

        Assert.Equal(MatchOutcome.Match, r.Outcome);
        Assert.Equal("Acme Trading SA", r.Customer!.Customer);
        Assert.Equal("T+1", r.Customer.Valeur);
        Assert.Equal("MATCH: Acme Trading SA", r.RegistryText);
    }

    [Fact]
    public void Unknown_iban_is_no_match()
    {
        using var tmp = new TempDir();
        string master = CreateMaster(tmp["master.xlsx"], ("Acme Trading SA", "ACMEGRA1", "GR0000000000000000000000999", ""));
        MatchResult r = CustomerMaster.Match(Sample, master);
        Assert.True(r.IsNoMatch);
        Assert.Contains("not in master", r.Reason);
    }

    [Fact]
    public void Different_sender_bic_is_no_match()
    {
        using var tmp = new TempDir();
        string master = CreateMaster(tmp["master.xlsx"], ("Acme Trading SA", "ZZZZGRA1", "GR7201100000000012345678901", ""));
        MatchResult r = CustomerMaster.Match(Sample, master);
        Assert.True(r.IsNoMatch);
        Assert.Contains("BIC", r.Reason);
    }

    [Fact]
    public void Different_name_is_no_match()
    {
        using var tmp = new TempDir();
        string master = CreateMaster(tmp["master.xlsx"], ("Completely Different Ltd", "ACMEGRA1", "GR7201100000000012345678901", ""));
        MatchResult r = CustomerMaster.Match(Sample, master);
        Assert.True(r.IsNoMatch);
        Assert.Contains("name", r.Reason);
    }

    [Fact]
    public void No_master_path_means_not_checked()
    {
        MatchResult r = CustomerMaster.Match(Sample, "");
        Assert.Equal(MatchOutcome.NotChecked, r.Outcome);
        Assert.Equal("NOT CHECKED", r.RegistryText);
    }

    [Fact]
    public void Missing_master_file_throws_so_the_engine_retries_later() =>
        Assert.Throws<FileNotFoundException>(() => CustomerMaster.Match(Sample, Path.Combine(Path.GetTempPath(), "no-master.xlsx")));

    [Theory]
    [InlineData("Ακμή Α.Ε.", "ΑΚΜΗ ΑΕ")]
    [InlineData("  acme   trading, s.a. ", "ACME TRADING SA")]
    [InlineData("Société Générale", "SOCIETE GENERALE")]
    public void Names_are_normalised(string input, string expected) =>
        Assert.Equal(expected, CustomerMaster.NormalizeName(input));

    [Theory]
    [InlineData("GR16 0110 1250 0000 0001 2300 695, DE89370400440532013000", "GR1601101250000000012300695|DE89370400440532013000")]
    [InlineData("GR1601101250000000012300695 DE89370400440532013000", "GR1601101250000000012300695|DE89370400440532013000")]
    [InlineData("gr1601101250000000012300695\nDE89370400440532013000;", "GR1601101250000000012300695|DE89370400440532013000")]
    [InlineData("/GR1601101250000000012300695", "GR1601101250000000012300695")]
    public void Iban_cells_are_split_on_any_separator(string cell, string expected) =>
        Assert.Equal(expected.Split('|'), CustomerMaster.SplitIbans(cell));
}
