using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SwiftBatchApp.Core;

/// <summary>
/// Open XML workbook writer (no Excel process). Every write:
///  * opens the file with FileShare.None — fails fast while a user has it open in Excel,
///  * edits an in-memory copy and writes it back only when something changed,
///  * is retried <see cref="RetryCount"/> times with linear back-off, then throws IOException
///    (the engine turns that into a queued PendingRows entry).
/// Columns are always addressed by header text, never by position, so old workbooks stay aligned
/// when new columns are added (missing headers are appended at the end).
/// </summary>
public static class ExcelWriter
{
    internal static int RetryCount = 5;
    internal static int RetryDelayMs = 400;

    private const uint HeaderStyle = 1;
    private const uint AmountStyle = 2;

    // ---------------------------------------------------------------- retries

    public static void WithRetries(Action action) => WithRetries(() => { action(); return true; });

    public static T WithRetries<T>(Func<T> func)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                return func();
            }
            catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }
            if (attempt < RetryCount - 1) Thread.Sleep(RetryDelayMs * (attempt + 1));
        }
        throw last as IOException ?? new IOException("The workbook is locked by another process.", last);
    }

    // ---------------------------------------------------------------- public API

    /// <summary>Creates the workbook with a styled, frozen header row; if it exists, adds missing headers. True if created.</summary>
    public static bool EnsureWorkbook(string path, string sheetName, IReadOnlyList<string> headers)
    {
        if (!File.Exists(path))
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            byte[] bytes = CreateWorkbookBytes(sheetName, headers);
            try
            {
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(bytes, 0, bytes.Length);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Someone else created it a moment ago — fall through to the header check.
            }
        }
        EnsureHeaders(path, headers);
        return false;
    }

    /// <summary>Appends any missing headers at the end of row 1. Returns the full header list.</summary>
    public static List<string> EnsureHeaders(string path, IReadOnlyList<string> required) =>
        Mutate(path, doc =>
        {
            var sheet = OpenSheet(doc);
            bool dirty = sheet.AddMissingHeaders(required);
            return (sheet.Headers.ToList(), dirty);
        });

    /// <summary>Appends rows; values are matched to columns by header (unknown headers are added).</summary>
    public static void AppendRows(string path, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return;
        Mutate(path, doc =>
        {
            var sheet = OpenSheet(doc);
            sheet.AddMissingHeaders(rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            foreach (var row in rows) sheet.AppendRow(row);
            sheet.UpdateDimension();
            return (true, true);
        });
    }

    public static void AppendRow(string path, IReadOnlyDictionary<string, object?> row) => AppendRows(path, new[] { row });

    /// <summary>
    /// Calls <paramref name="updater"/> for every data row (header → text). A non-empty result is
    /// written into that row (header → new value). Returns the number of rows changed.
    /// </summary>
    public static int UpdateRows(string path, Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, object?>?> updater) =>
        Mutate(path, doc =>
        {
            var sheet = OpenSheet(doc);
            int changed = sheet.UpdateRows(updater);
            return (changed, changed > 0);
        });

    // ---------------------------------------------------------------- core

    private static T Mutate<T>(string path, Func<SpreadsheetDocument, (T Result, bool Dirty)> change) =>
        WithRetries(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;

            (T result, bool dirty) outcome;
            using (var doc = SpreadsheetDocument.Open(ms, true))
            {
                outcome = change(doc);
            }
            if (outcome.dirty)
            {
                fs.Position = 0;
                fs.SetLength(0);
                ms.Position = 0;
                ms.CopyTo(fs);
                fs.Flush(true);
            }
            return outcome.result;
        });

    private static SheetEditor OpenSheet(SpreadsheetDocument doc)
    {
        WorksheetPart ws = ExcelReader.FirstWorksheet(doc) ?? throw new InvalidDataException("The workbook has no worksheet.");
        return new SheetEditor(doc, ws);
    }

    internal static byte[] CreateWorkbookBytes(string sheetName, IReadOnlyList<string> headers)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            WorkbookPart wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook();
            wb.AddNewPart<WorkbookStylesPart>().Stylesheet = BuildStylesheet();

            WorksheetPart ws = wb.AddNewPart<WorksheetPart>();
            var columns = new Columns();
            for (int i = 0; i < headers.Count; i++)
            {
                double width = Math.Clamp(headers[i].Length + 6, 12, 42);
                columns.Append(new Column { Min = (uint)(i + 1), Max = (uint)(i + 1), Width = width, CustomWidth = true });
            }
            var headerRow = new Row { RowIndex = 1 };
            for (int i = 0; i < headers.Count; i++)
                headerRow.Append(TextCell(ExcelReader.ColumnName(i) + "1", headers[i], HeaderStyle));

            ws.Worksheet = new Worksheet(
                new SheetViews(new SheetView(
                    new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen },
                    new Selection { Pane = PaneValues.BottomLeft, ActiveCell = "A2", SequenceOfReferences = new ListValue<StringValue> { InnerText = "A2" } })
                { TabSelected = true, WorkbookViewId = 0 }),
                new SheetFormatProperties { DefaultRowHeight = 15 },
                columns,
                new SheetData(headerRow));

            wb.Workbook.Append(new Sheets(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = sheetName }));
        }
        return ms.ToArray();
    }

    private static Stylesheet BuildStylesheet() => new(
        new Fonts(
            new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
            new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" })),
        new Fills(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFFFB81C" }, new BackgroundColor { Indexed = 64 }) { PatternType = PatternValues.Solid })),
        new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())),
        new CellFormats(
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 0 },
            new CellFormat { FontId = 1, FillId = 2, BorderId = 0, NumberFormatId = 0, ApplyFont = true, ApplyFill = true },
            new CellFormat { FontId = 0, FillId = 0, BorderId = 0, NumberFormatId = 4, ApplyNumberFormat = true }));

    internal static Cell TextCell(string reference, string text, uint? style = null)
    {
        var cell = new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }),
        };
        if (style.HasValue) cell.StyleIndex = style.Value;
        return cell;
    }

    internal static Cell ValueCell(string reference, object value, uint? style)
    {
        string? number = value switch
        {
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double db => db.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            _ => null,
        };
        if (number is null) return TextCell(reference, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", style);
        var cell = new Cell { CellReference = reference, DataType = CellValues.Number, CellValue = new CellValue(number) };
        if (style.HasValue) cell.StyleIndex = style.Value;
        return cell;
    }

    // ---------------------------------------------------------------- sheet editor

    /// <summary>Header-aware editing of one worksheet held in memory.</summary>
    private sealed class SheetEditor
    {
        private readonly WorksheetPart _ws;
        private readonly SheetData _data;
        private readonly Row _headerRow;
        private readonly List<string> _shared;
        private readonly bool _ourStyles;

        public List<string> Headers { get; } = new();

        public SheetEditor(SpreadsheetDocument doc, WorksheetPart ws)
        {
            _ws = ws;
            _data = ws.Worksheet.GetFirstChild<SheetData>() ?? ws.Worksheet.AppendChild(new SheetData());
            _shared = doc.WorkbookPart?.SharedStringTablePart?.SharedStringTable?
                          .Elements<SharedStringItem>().Select(ExcelReader.ItemText).ToList() ?? new List<string>();
            _ourStyles = IsOurStylesheet(doc);

            Row? first = _data.Elements<Row>().FirstOrDefault();
            if (first is null || (first.RowIndex?.Value ?? 1) != 1)
            {
                first = new Row { RowIndex = 1 };
                _data.InsertAt(first, 0);
            }
            _headerRow = first;
            foreach (var (col, text) in CellsWithIndex(_headerRow))
            {
                while (Headers.Count <= col) Headers.Add("");
                Headers[col] = text.Trim();
            }
            while (Headers.Count > 0 && Headers[^1].Length == 0) Headers.RemoveAt(Headers.Count - 1);
        }

        public int IndexOf(string header) => Headers.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));

        public bool AddMissingHeaders(IEnumerable<string> required)
        {
            bool dirty = false;
            uint? style = _headerRow.Elements<Cell>().FirstOrDefault()?.StyleIndex?.Value;
            foreach (string h in required)
            {
                if (string.IsNullOrWhiteSpace(h) || IndexOf(h) >= 0) continue;
                int col = Headers.Count;
                Headers.Add(h);
                SetCell(_headerRow, col, h, style);
                dirty = true;
            }
            return dirty;
        }

        public void AppendRow(IReadOnlyDictionary<string, object?> values)
        {
            uint next = 1;
            foreach (Row r in _data.Elements<Row>())
                next = Math.Max(next, (r.RowIndex?.Value ?? 0) + 1);
            var row = new Row { RowIndex = next };
            foreach (var (col, value) in values
                         .Where(kv => kv.Value is not null && !(kv.Value is string s && s.Length == 0))
                         .Select(kv => (IndexOf(kv.Key), kv.Value!))
                         .Where(x => x.Item1 >= 0)
                         .OrderBy(x => x.Item1))
            {
                string reference = ExcelReader.ColumnName(col) + next;
                uint? style = value is decimal && _ourStyles ? AmountStyle : null;
                row.Append(ValueCell(reference, value, style));
            }
            _data.Append(row);
        }

        public int UpdateRows(Func<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, object?>?> updater)
        {
            int changed = 0;
            foreach (Row row in _data.Elements<Row>().ToList())
            {
                if (ReferenceEquals(row, _headerRow)) continue;
                var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string h in Headers) if (h.Length > 0) current.TryAdd(h, "");
                foreach (var (col, text) in CellsWithIndex(row))
                    if (col < Headers.Count && Headers[col].Length > 0) current[Headers[col]] = text;
                if (current.Values.All(string.IsNullOrWhiteSpace)) continue;

                var updates = updater(current);
                if (updates is null || updates.Count == 0) continue;
                AddMissingHeaders(updates.Keys);
                foreach (var (header, value) in updates)
                {
                    int col = IndexOf(header);
                    SetCell(row, col, value, null);
                }
                changed++;
            }
            if (changed > 0) UpdateDimension();
            return changed;
        }

        public void UpdateDimension()
        {
            SheetDimension? dim = _ws.Worksheet.GetFirstChild<SheetDimension>();
            if (dim is null) return;
            uint lastRow = 1;
            foreach (Row r in _data.Elements<Row>()) lastRow = Math.Max(lastRow, r.RowIndex?.Value ?? 1);
            dim.Reference = $"A1:{ExcelReader.ColumnName(Math.Max(0, Headers.Count - 1))}{lastRow}";
        }

        private void SetCell(Row row, int col, object? value, uint? styleOverride)
        {
            uint rowIndex = row.RowIndex?.Value ?? 1;
            string reference = ExcelReader.ColumnName(col) + rowIndex;
            Cell? existing = null;
            Cell? after = null;
            foreach (var (c, cell) in CellElementsWithIndex(row))
            {
                if (c == col) { existing = cell; break; }
                if (c > col) { after = cell; break; }
            }
            uint? style = styleOverride ?? existing?.StyleIndex?.Value;
            if (value is decimal && _ourStyles && styleOverride is null && existing?.StyleIndex is null) style = AmountStyle;
            Cell replacement = value is null ? TextCell(reference, "", style) : ValueCell(reference, value, style);

            if (existing is not null) row.ReplaceChild(replacement, existing);
            else if (after is not null) row.InsertBefore(replacement, after);
            else row.Append(replacement);
        }

        private IEnumerable<(int Col, string Text)> CellsWithIndex(Row row) =>
            CellElementsWithIndex(row).Select(x => (x.Col, ExcelReader.CellText(x.Cell, _shared)));

        private static IEnumerable<(int Col, Cell Cell)> CellElementsWithIndex(Row row)
        {
            int next = 0;
            foreach (Cell cell in row.Elements<Cell>())
            {
                int col = cell.CellReference?.Value is { } r ? ExcelReader.ColumnIndex(r) : next;
                next = col + 1;
                yield return (col, cell);
            }
        }

        private static bool IsOurStylesheet(SpreadsheetDocument doc)
        {
            CellFormats? xfs = doc.WorkbookPart?.WorkbookStylesPart?.Stylesheet?.CellFormats;
            return xfs?.Elements<CellFormat>().ElementAtOrDefault((int)AmountStyle)?.NumberFormatId?.Value == 4;
        }
    }
}
