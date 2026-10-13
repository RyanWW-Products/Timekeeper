using System.Xml.Linq;
using Timekeeper.Core;

static class WorkbookValueTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly DateOnly WorkDate = new(2026, 9, 28);
    public static void Register(Action<string, Action> test)
    {
        test("workbooks open for editing import their saved bytes with the matching content hash", () =>
        {
            using var book = new WorkbookFixture();
            using var editor = new FileStream(book.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var savedBytes = new byte[editor.Length]; editor.ReadExactly(savedBytes);
            var result = EmailWorkbookReader.Read(book.Path);
            Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(savedBytes)), result.ContentSha256);
            SameDates(result);
        });
        test("exclusive file locks provide recovery instructions instead of an unhandled error", () =>
        {
            using var book = new WorkbookFixture();
            using var editor = new FileStream(book.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Reject(book, "could not be opened", "close Excel");
        });
        test("final dates accept Excel numeric serials in General cells and metadata", () =>
        {
            using var book = new WorkbookFixture(p => Dates(p, "46293", "n"));
            SameDates(EmailWorkbookReader.Read(book.Path));
        });
        test("serial dates stored as literal text are normalized only in date columns", () =>
        {
            using var book = new WorkbookFixture(p => Dates(p, "46293.00", "inlineStr"));
            SameDates(EmailWorkbookReader.Read(book.Path));
        });
        test("native Open XML date cells support ISO values without Excel serial conversion", () =>
        {
            foreach (var value in new[] { "2026-09-28", "2026-09-28T00:00:00", "2026-09-28T23:59:59.123Z", "2026-09-28T23:59:59-04:00" })
            {
                using var book = new WorkbookFixture(p => Dates(p, value, "d"));
                SameDates(EmailWorkbookReader.Read(book.Path));
            }
        });
        test("1904 numeric date workbooks honor the workbook epoch", () =>
        {
            foreach (var setting in new[] { "1", "true" })
            {
                using var book = new WorkbookFixture(p => { Dates(p, "44831", "n"); Epoch(p, setting); });
                SameDates(EmailWorkbookReader.Read(book.Path));
            }
        });
        test("1900 numeric dates honor both valid false epoch settings", () =>
        {
            foreach (var setting in new[] { "0", "false" })
            {
                using var book = new WorkbookFixture(p => { Dates(p, "46293", "n"); Epoch(p, setting); });
                SameDates(EmailWorkbookReader.Read(book.Path));
            }
        });
        test("invalid epoch settings do not silently select a date system", () =>
        {
            using var book = new WorkbookFixture(p => Epoch(p, "maybe"));
            Reject(book, "date1904");
        });
        test("date time serials use the calendar date without changing minutes", () =>
        {
            using var book = new WorkbookFixture(p => Dates(p, "46293.75", "n"));
            var result = EmailWorkbookReader.Read(book.Path);
            SameDates(result); Equal(5, result.Activities.Single().Minutes);
        });
        test("unambiguous text dates accept English month names and clear day month order", () =>
        {
            foreach (var value in new[] { "09/28/2026", "28/09/2026", "28-09-2026", "2026/9/28", "Sep 28, 2026", "September 28, 2026", "28 September 2026", "28-Sep-2026" })
            {
                using var book = new WorkbookFixture(p => Dates(p, value, "inlineStr"));
                SameDates(EmailWorkbookReader.Read(book.Path));
            }
        });
        test("ambiguous dates identify the sheet and cell with instructions", () =>
        {
            using var book = new WorkbookFixture(p => Cell(p, "sheet2", "B2", "10/12/2026", "inlineStr"));
            Reject(book, "Activities!B2 has an ambiguous date", "YYYY-MM-DD");
        });
        test("invalid date serials and malformed ISO timestamps identify the exact cell", () =>
        {
            foreach (var value in new[] { "-1", "0", "60", "60.5", "2958466", "9999999999999999999999999999", "NaN", "Infinity", "2026-02-30", "2026-09-28T25:00:00Z", "2026-09-28T12:00:00+99:00" })
            {
                using var book = new WorkbookFixture(p => Cell(p, "sheet2", "B2", value, "n"));
                Reject(book, "Activities!B2");
            }
        });
        test("1900 date boundaries skip the fictitious leap day", () =>
        {
            foreach (var pair in new[] { ("1", new DateOnly(1900, 1, 1)), ("59", new DateOnly(1900, 2, 28)), ("61", new DateOnly(1900, 3, 1)), ("2958465", DateOnly.MaxValue) })
            {
                using var book = new WorkbookFixture(p => Dates(p, pair.Item1, "n"));
                Equal(pair.Item2, EmailWorkbookReader.Read(book.Path).Activities.Single().Date);
            }
        });
        test("1904 epoch zero represents January 1 rather than a missing date", () =>
        {
            using var book = new WorkbookFixture(p => { Dates(p, "0", "n"); Epoch(p, "true"); });
            Equal(new DateOnly(1904, 1, 1), EmailWorkbookReader.Read(book.Path).Activities.Single().Date);
        });
        test("review workbook accepts mixed native serial and text metadata dates", () =>
        {
            using var book = new WorkbookFixture(p => { ReviewBook.Convert(p); Cell(p, "sheet2", "A4", "46293", "n"); });
            var result = EmailWorkbookReader.Read(book.Path); SameDates(result);
            Equal(0, EmailWorkbookReader.Validate(EmailWorkbookReader.ConfirmReview(result)).Count);
        });
        test("review row identities are unchanged when a date switches between text and Excel date cells", () =>
        {
            using var original = new WorkbookFixture(ReviewBook.Convert);
            using var numeric = new WorkbookFixture(p => { ReviewBook.Convert(p); Cell(p, "sheet2", "A4", "46293", "n"); });
            using var alternateEpoch = new WorkbookFixture(p => { ReviewBook.Convert(p); Cell(p, "sheet2", "A4", "44831", "n"); Epoch(p, "true"); });
            var fingerprint = EmailWorkbookReader.Read(original.Path).Activities.Single().ActivityId;
            Equal(fingerprint, EmailWorkbookReader.Read(numeric.Path).Activities.Single().ActivityId);
            Equal(fingerprint, EmailWorkbookReader.Read(alternateEpoch.Path).Activities.Single().ActivityId);
        });
        test("date styles do not alter IDs minutes or mapping hints", () =>
        {
            using var book = new WorkbookFixture(p =>
            {
                Dates(p, "46293", "n");
                Cell(p, "sheet2", "A2", "46307", "n", "1");
                Cell(p, "sheet2", "C2", "5", "n", "1");
                Cell(p, "sheet2", "D2", "46307", "n", "1");
                p["xl/styles.xml"] = $"<styleSheet xmlns='{Ns}'><cellXfs count='2'><xf numFmtId='0'/><xf numFmtId='14'/></cellXfs></styleSheet>";
            });
            var result = EmailWorkbookReader.Read(book.Path); SameDates(result);
            Equal("46307", result.Activities.Single().ActivityId); Equal("46307", result.Activities.Single().MatterHint); Equal(5, result.Activities.Single().Minutes);
        });
        test("dates in shared strings are decoded before date normalization", () =>
        {
            using var book = new WorkbookFixture(p =>
            {
                Cell(p, "sheet2", "B2", "0", "s");
                p["xl/sharedStrings.xml"] = $"<sst xmlns='{Ns}'><si><r><t>2026-</t></r><r><t>09-28</t></r></si></sst>";
            });
            SameDates(EmailWorkbookReader.Read(book.Path));
        });
        test("review scientific numeric values and case insensitive booleans keep whole minutes", () =>
        {
            using var book = new WorkbookFixture(p =>
            {
                ReviewBook.Convert(p);
                Cell(p, "sheet2", "F4", "1.5E+1", "n"); Cell(p, "sheet2", "G4", "2.5E-1", "str");
                Cell(p, "sheet2", "E4", "TRUE", "inlineStr");
            });
            var result = EmailWorkbookReader.Read(book.Path); Equal(15, result.Activities.Single().Minutes); Equal<bool?>(true, result.Activities.Single().Billable);
        });
        test("review native boolean numeric and non billable labels preserve classification", () =>
        {
            foreach (var input in new[] { ("1", "b", true), ("0", "b", false), ("1.0", "n", true), ("0E+0", "n", false), ("1", "n", true), ("0", "n", false), ("Non billable", "inlineStr", false) })
            {
                using var book = new WorkbookFixture(p => { ReviewBook.Convert(p); Cell(p, "sheet2", "E4", input.Item1, input.Item2); });
                Equal<bool?>(input.Item3, EmailWorkbookReader.Read(book.Path).Activities.Single().Billable);
            }
        });
        test("final uppercase and numeric boolean cells retain approval and billing meaning", () =>
        {
            foreach (var input in new[] { ("TRUE", "inlineStr"), ("1", "b"), ("1", "n"), ("1.0", "n"), ("1E+0", "inlineStr") })
            {
                using var book = new WorkbookFixture(p =>
                {
                    Cell(p, "sheet1", "B9", input.Item1, input.Item2);
                    Cell(p, "sheet2", "H2", "FALSE", "inlineStr");
                    Cell(p, "sheet1", "B3", "1E+0", "n");
                });
                var result = EmailWorkbookReader.Read(book.Path); Equal(true, result.Metadata.EmployeeConfirmed); Equal<bool?>(false, result.Activities.Single().Billable);
            }
        });
        test("invalid booleans and date formatted nonnumeric durations remain rejected", () =>
        {
            using var boolean = new WorkbookFixture(p => Cell(p, "sheet1", "B9", "2", "n")); Reject(boolean, "employee_confirmed");
            using var duration = new WorkbookFixture(p => { ReviewBook.Convert(p); Cell(p, "sheet2", "F4", "2026-09-28", "d"); }); Reject(duration, "must be numbers");
        });
    }

    private static void Dates(Dictionary<string, string> parts, string value, string type)
    {
        Cell(parts, "sheet1", "B6", value, type); Cell(parts, "sheet1", "B7", value, type); Cell(parts, "sheet2", "B2", value, type);
    }
    private static void Epoch(Dictionary<string, string> parts, string value)
    {
        var doc = XDocument.Parse(parts["xl/workbook.xml"]);
        doc.Root!.AddFirst(new XElement(Ns + "workbookPr", new XAttribute("date1904", value)));
        parts["xl/workbook.xml"] = doc.ToString();
    }
    private static void Cell(Dictionary<string, string> parts, string sheet, string address, string value, string type, string? style = null)
    {
        var path = $"xl/worksheets/{sheet}.xml";
        var doc = XDocument.Parse(parts[path]); var cell = doc.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == address);
        cell.SetAttributeValue("t", type); if (style is not null) cell.SetAttributeValue("s", style);
        cell.ReplaceNodes(type == "inlineStr" ? new XElement(Ns + "is", new XElement(Ns + "t", value)) : new XElement(Ns + "v", value));
        parts[path] = doc.ToString();
    }
    private static void SameDates(EmailWorkbook workbook)
    {
        Equal(WorkDate, workbook.Activities.Single().Date); Equal(WorkDate, workbook.Metadata.PeriodStart); Equal(WorkDate, workbook.Metadata.PeriodEnd);
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, received {actual}.");
    }
    private static void Reject(WorkbookFixture book, params string[] details)
    {
        try { EmailWorkbookReader.Read(book.Path); }
        catch (InvalidDataException error)
        {
            if (details.All(s => error.Message.Contains(s, StringComparison.OrdinalIgnoreCase))) return;
            throw new Exception("Incorrect diagnostic: " + error.Message);
        }
        throw new Exception("Expected workbook rejection.");
    }
}
