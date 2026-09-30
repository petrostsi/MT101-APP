using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SwiftBatchApp.Core;

namespace SwiftBatchApp.Tests;

public class ExcelTests
{
    public ExcelTests()
    {
        ExcelWriter.RetryCount = 3;
        ExcelWriter.RetryDelayMs = 5;
    }

    [Fact]
    public void Create_append_and_read_back_by_header()
    {
        using var tmp = new TempDir();
        string path = tmp["book.xlsx"];

        Assert.True(ExcelWriter.EnsureWorkbook(path, "Data", new[] { "Name", "Count", "Amount" }));
        Assert.False(ExcelWriter.EnsureWorkbook(path, "Data", new[] { "Name", "Count", "Amount" }));
        ExcelWriter.AppendRows(path, new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["Name"] = "first", ["Count"] = 3, ["Amount"] = 1234.56m },
            new Dictionary<string, object?> { ["Amount"] = 0.5m, ["Name"] = "second" },          // any key order, gaps
        });

        SheetTable t = ExcelReader.ReadTable(path);
        Assert.Equal(new[] { "Name", "Count", "Amount" }, t.Headers);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("first", t.Rows[0]["Name"]);
        Assert.Equal("3", t.Rows[0]["count"]);                                                   // case-insensitive
        Assert.Equal("1234.56", t.Rows[0]["Amount"]);
        Assert.Equal("", t.Rows[1]["Count"]);
        Assert.Equal("0.5", t.Rows[1]["Amount"]);
    }

    [Fact]
    public void Missing_headers_are_appended_and_old_columns_stay_aligned()
    {
        using var tmp = new TempDir();
        string path = tmp["registry.xlsx"];
        ExcelWriter.EnsureWorkbook(path, "Registry", new[] { "File Name", "Payment Count", "Status" });   // an old layout
        ExcelWriter.AppendRow(path, new Dictionary<string, object?> { ["File Name"] = "old.prt", ["Payment Count"] = 2, ["Status"] = "PROCESSED" });

        List<string> headers = ExcelWriter.EnsureHeaders(path, WorkbookLayout.RegistryHeaders);
        ExcelWriter.AppendRow(path, new Dictionary<string, object?> { ["File Name"] = "new.prt", ["Orders"] = 1, ["Customer Match"] = "MATCH: X" });

        Assert.Equal(new[] { "File Name", "Payment Count", "Status" }, headers.Take(3));
        Assert.Contains("Customer Match", headers);
        SheetTable t = ExcelReader.ReadTable(path);
        Assert.Equal("old.prt", t.Rows[0]["File Name"]);
        Assert.Equal("2", t.Rows[0]["Payment Count"]);
        Assert.Equal("", t.Rows[0]["Customer Match"]);
        Assert.Equal("new.prt", t.Rows[1]["File Name"]);
        Assert.Equal("MATCH: X", t.Rows[1]["Customer Match"]);
        Assert.Equal("1", t.Rows[1]["Orders"]);
    }

    [Fact]
    public void UpdateRows_changes_only_matching_rows_and_skips_the_write_when_nothing_changes()
    {
        using var tmp = new TempDir();
        string path = tmp["book.xlsx"];
        ExcelWriter.EnsureWorkbook(path, "Data", new[] { "Key", "Status" });
        ExcelWriter.AppendRows(path, new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["Key"] = "a", ["Status"] = "Pending" },
            new Dictionary<string, object?> { ["Key"] = "b", ["Status"] = "Pending" },
        });

        int changed = ExcelWriter.UpdateRows(path, r => r["Key"] == "b" ? new Dictionary<string, object?> { ["Status"] = "Completed", ["Note"] = "x" } : null);
        Assert.Equal(1, changed);
        SheetTable t = ExcelReader.ReadTable(path);
        Assert.Equal("Pending", t.Rows[0]["Status"]);
        Assert.Equal("Completed", t.Rows[1]["Status"]);
        Assert.Equal("x", t.Rows[1]["Note"]);

        DateTime stamp = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(20);
        Assert.Equal(0, ExcelWriter.UpdateRows(path, _ => null));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Workbooks_saved_by_Excel_with_shared_strings_are_read_and_updated()
    {
        using var tmp = new TempDir();
        string path = tmp["excel-saved.xlsx"];
        CreateSharedStringWorkbook(path);

        SheetTable t = ExcelReader.ReadTable(path);
        Assert.Equal(new[] { "File Name", "Status" }, t.Headers);
        Assert.Equal("f1.prt", t.Rows[0]["File Name"]);
        Assert.Equal("Pending", t.Rows[1]["Status"]);

        ExcelWriter.UpdateRows(path, r => r["File Name"] == "f2.prt" ? new Dictionary<string, object?> { ["Status"] = "Completed" } : null);
        t = ExcelReader.ReadTable(path);
        Assert.Equal("Pending", t.Rows[0]["Status"]);
        Assert.Equal("Completed", t.Rows[1]["Status"]);
        Assert.Equal("f2.prt", t.Rows[1]["File Name"]);
    }

    [Fact]
    public void Locked_workbook_fails_with_IOException_after_retries()
    {
        using var tmp = new TempDir();
        string path = tmp["locked.xlsx"];
        ExcelWriter.EnsureWorkbook(path, "Data", new[] { "A" });

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => ExcelWriter.AppendRow(path, new Dictionary<string, object?> { ["A"] = "x" }));
        }
        ExcelWriter.AppendRow(path, new Dictionary<string, object?> { ["A"] = "x" });
        Assert.Single(ExcelReader.ReadTable(path).Rows);
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("Z9", 25)]
    [InlineData("AA10", 26)]
    [InlineData("AZ1", 51)]
    [InlineData("BA1", 52)]
    public void Column_references_round_trip(string reference, int index)
    {
        Assert.Equal(index, ExcelReader.ColumnIndex(reference));
        Assert.Equal(new string(reference.TakeWhile(char.IsLetter).ToArray()), ExcelReader.ColumnName(index));
    }

    [Fact]
    public void Row_json_round_trip_keeps_numbers()
    {
        var row = new Dictionary<string, object?> { ["s"] = "text", ["i"] = 3, ["d"] = 12.5m, ["n"] = null, ["l"] = 190768L };
        var back = RowCodec.Deserialize(RowCodec.Serialize(row));
        Assert.Equal("text", back["s"]);
        Assert.Equal(3L, back["i"]);
        Assert.Equal(12.5m, back["d"]);
        Assert.Null(back["n"]);
        Assert.Equal(190768L, back["l"]);
    }

    /// <summary>Mimics a workbook re-saved by Excel: shared strings, a dimension element, no inline strings.</summary>
    private static void CreateSharedStringWorkbook(string path)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        WorkbookPart wb = doc.AddWorkbookPart();
        wb.Workbook = new Workbook();
        var sst = wb.AddNewPart<SharedStringTablePart>();
        string[] strings = { "File Name", "Status", "f1.prt", "Pending", "f2.prt" };
        sst.SharedStringTable = new SharedStringTable(strings.Select(s => new SharedStringItem(new Text(s))));

        Cell S(string r, int i) => new() { CellReference = r, DataType = CellValues.SharedString, CellValue = new CellValue(i.ToString()) };
        WorksheetPart ws = wb.AddNewPart<WorksheetPart>();
        ws.Worksheet = new Worksheet(
            new SheetDimension { Reference = "A1:B3" },
            new SheetData(
                new Row(S("A1", 0), S("B1", 1)) { RowIndex = 1 },
                new Row(S("A2", 2), S("B2", 3)) { RowIndex = 2 },
                new Row(S("A3", 4), S("B3", 3)) { RowIndex = 3 }));
        wb.Workbook.AppendChild(new Sheets(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Sheet1" }));
    }
}
