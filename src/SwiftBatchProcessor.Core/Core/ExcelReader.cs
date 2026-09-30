using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SwiftBatchApp.Core;

/// <summary>A worksheet read by its header row. Keys are case-insensitive header texts.</summary>
public sealed class SheetTable
{
    public List<string> Headers { get; } = new();
    public List<Dictionary<string, string>> Rows { get; } = new();

    public bool HasHeader(string header) => Headers.Contains(header, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Lock-free workbook reader: the file is copied into memory with FileShare.ReadWrite|Delete,
/// so a user holding the workbook open in Excel never blocks (or is blocked by) the app.
/// Reads the first worksheet. Handles shared strings, inline strings, numbers and booleans.
/// </summary>
public static class ExcelReader
{
    public static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Header-mapped rows (row 1 = headers). Blank rows are skipped.</summary>
    public static SheetTable ReadTable(string path)
    {
        var table = new SheetTable();
        List<string[]> grid = ReadGrid(path);
        if (grid.Count == 0) return table;

        string[] header = grid[0];
        table.Headers.AddRange(header.Select(h => h.Trim()));
        for (int r = 1; r < grid.Count; r++)
        {
            string[] cells = grid[r];
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < table.Headers.Count; c++)
            {
                string h = table.Headers[c];
                if (h.Length == 0 || row.ContainsKey(h)) continue;
                row[h] = c < cells.Length ? cells[c] : "";
            }
            table.Rows.Add(row);
        }
        return table;
    }

    /// <summary>Positional grid of the first worksheet (row 0 = first row). Sparse cells are filled with "".</summary>
    public static List<string[]> ReadGrid(string path)
    {
        using var ms = new MemoryStream(ReadAllBytesShared(path));
        using var doc = SpreadsheetDocument.Open(ms, false);
        return ReadGrid(doc);
    }

    internal static List<string[]> ReadGrid(SpreadsheetDocument doc)
    {
        var result = new List<string[]>();
        WorksheetPart? wsPart = FirstWorksheet(doc);
        SheetData? sheetData = wsPart?.Worksheet?.GetFirstChild<SheetData>();
        if (sheetData is null) return result;

        SharedStringTable? sst = doc.WorkbookPart?.SharedStringTablePart?.SharedStringTable;
        List<string> shared = sst?.Elements<SharedStringItem>().Select(ItemText).ToList() ?? new List<string>();

        uint expectedRow = 1;
        foreach (Row row in sheetData.Elements<Row>())
        {
            uint rowIndex = row.RowIndex?.Value ?? expectedRow;
            if (rowIndex < 1) rowIndex = expectedRow;
            while (result.Count < rowIndex - 1) result.Add(Array.Empty<string>());   // gaps
            expectedRow = rowIndex + 1;

            var cells = new List<string>();
            int nextCol = 0;
            foreach (Cell cell in row.Elements<Cell>())
            {
                int col = cell.CellReference?.Value is { } cref ? ColumnIndex(cref) : nextCol;
                nextCol = col + 1;
                while (cells.Count <= col) cells.Add("");
                cells[col] = CellText(cell, shared);
            }
            result.Add(cells.ToArray());
        }
        return result;
    }

    internal static WorksheetPart? FirstWorksheet(SpreadsheetDocument doc)
    {
        WorkbookPart? wb = doc.WorkbookPart;
        Sheet? sheet = wb?.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault();
        if (wb is null) return null;
        if (sheet?.Id?.Value is { } id && wb.GetPartById(id) is WorksheetPart ws) return ws;
        return wb.WorksheetParts.FirstOrDefault();
    }

    internal static string CellText(Cell cell, IReadOnlyList<string> shared)
    {
        CellValues? type = cell.DataType?.Value;
        if (type == CellValues.InlineString)
            return cell.InlineString is null ? "" : ItemText(cell.InlineString);

        string raw = cell.CellValue?.Text ?? "";
        if (type == CellValues.SharedString)
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < shared.Count ? shared[i] : "";
        if (type == CellValues.Boolean)
            return raw == "1" ? "TRUE" : "FALSE";
        return raw;
    }

    /// <summary>Text of a shared-string item or inline string (plain text or rich-text runs).</summary>
    internal static string ItemText(OpenXmlElement item)
    {
        Text? t = item.GetFirstChild<Text>();
        if (t is not null) return t.Text;
        return string.Concat(item.Elements<Run>().Select(r => r.Text?.Text ?? ""));
    }

    /// <summary>"C12" → 2 (zero-based).</summary>
    public static int ColumnIndex(string cellReference)
    {
        int col = 0;
        foreach (char ch in cellReference)
        {
            if (ch is >= 'A' and <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
        }
        return col - 1;
    }

    /// <summary>2 → "C" (zero-based).</summary>
    public static string ColumnName(int index)
    {
        string name = "";
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
            name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}
