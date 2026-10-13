using System.Xml.Linq;
using System.Text.Json;
using Timekeeper.Core;

static class WorkbookNameTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static void Register(Action<string, Action> test)
    {
        test("quoted and unquoted Review filter names preserve activities and local confirmation", () =>
        {
            using var original = new WorkbookFixture(ReviewBook.Convert);
            var baseline = EmailWorkbookReader.Read(original.Path);
            foreach (var reference in new[] { "Review!$A$3:$I$5", "'Review'!$A$3:$I$5" })
            {
                using var book = new WorkbookFixture(p => { ReviewBook.Convert(p); Name(p, "_xlnm._FilterDatabase", reference); });
                var workbook = EmailWorkbookReader.Read(book.Path);
                Require(JsonSerializer.Serialize(workbook.Activities) == JsonSerializer.Serialize(baseline.Activities));
                Require(!workbook.Metadata.EmployeeConfirmed && !workbook.ReviewConfirmedAtUtc.HasValue);
                Require(EmailWorkbookReader.Validate(EmailWorkbookReader.ConfirmReview(workbook)).Count == 0);
            }
        });
        test("quoted Communication Accounting filter names remain accepted", () =>
        {
            using var book = new WorkbookFixture(p =>
            {
                ReviewBook.Convert(p);
                var workbook = XDocument.Parse(p["xl/workbook.xml"]);
                XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                workbook.Root!.Element(Ns + "sheets")!.Add(new XElement(Ns + "sheet", new XAttribute("name", "Communication Accounting"), new XAttribute("sheetId", 3), new XAttribute(rel + "id", "r3")));
                p["xl/workbook.xml"] = workbook.ToString();
                var relationships = XDocument.Parse(p["xl/_rels/workbook.xml.rels"]);
                XNamespace package = "http://schemas.openxmlformats.org/package/2006/relationships";
                relationships.Root!.Add(new XElement(package + "Relationship", new XAttribute("Id", "r3"), new XAttribute("Type", rel.NamespaceName + "/worksheet"), new XAttribute("Target", "worksheets/sheet3.xml")));
                p["xl/_rels/workbook.xml.rels"] = relationships.ToString();
                p["xl/worksheets/sheet3.xml"] = new XElement(Ns + "worksheet", new XElement(Ns + "sheetData")).ToString();
                Name(p, "_xlnm._FilterDatabase", "'Communication Accounting'!$A$1:$I$5", 2);
            });
            Require(EmailWorkbookReader.Read(book.Path).Activities.Single().Minutes == 15);
        });
        test("review names cannot hide arbitrary formulas external references or malformed filters", () =>
        {
            foreach (var reference in new[] { "SUM(Review!$A$3:$I$5)", "'[external.xlsx]Review'!$A$3:$I$5", "https://example.com/book.xlsx!$A$3:$I$5", "'Review!$A$3:$I$5", "Review'!$A$3:$I$5", "Review!$A$0:$I$5", "Review!$A$3:$J$5", "Metadata!$A$1:$B$5" })
            {
                using var book = new WorkbookFixture(p => { ReviewBook.Convert(p); Name(p, "_xlnm._FilterDatabase", reference); });
                Reject(book, "Workbook defined name '_xlnm._FilterDatabase'", "Formulas > Name Manager");
            }
            using var namedFormula = new WorkbookFixture(p => { ReviewBook.Convert(p); Name(p, "CustomTotal", "SUM(Review!F4:F5)"); });
            Reject(namedFormula, "Workbook defined name 'CustomTotal'");
        });
        test("final activity workbooks still reject defined names", () =>
        {
            using var book = new WorkbookFixture(p => Name(p, "_xlnm._FilterDatabase", "'Activities'!$A$1:$J$2"));
            Reject(book, "Formula cells and defined formulas");
        });
        test("unsupported review cell formulas identify their exact location", () =>
        {
            using var book = new WorkbookFixture(p =>
            {
                ReviewBook.Convert(p);
                var sheet = XDocument.Parse(p["xl/worksheets/sheet2.xml"]);
                var cell = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "E4");
                cell.Attribute("t")?.Remove();
                cell.ReplaceNodes(new XElement(Ns + "f", "1+1"), new XElement(Ns + "v", "2"));
                p["xl/worksheets/sheet2.xml"] = sheet.ToString();
            });
            Reject(book, "Formula in Review!E4", "plain values");
        });
    }

    private static void Name(Dictionary<string, string> parts, string name, string reference, int sheetIndex = 1)
    {
        var workbook = XDocument.Parse(parts["xl/workbook.xml"]);
        workbook.Root!.Add(new XElement(Ns + "definedNames", new XElement(Ns + "definedName", new XAttribute("name", name), new XAttribute("localSheetId", sheetIndex), new XAttribute("hidden", 1), reference)));
        parts["xl/workbook.xml"] = workbook.ToString();
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Workbook filter metadata changed import or confirmation behavior.");
    }

    private static void Reject(WorkbookFixture book, params string[] messages)
    {
        try { EmailWorkbookReader.Read(book.Path); }
        catch (InvalidDataException error)
        {
            foreach (var message in messages)
                if (!error.Message.Contains(message, StringComparison.Ordinal)) throw new Exception(error.Message);
            return;
        }
        throw new Exception("Expected unsupported workbook content to be rejected.");
    }
}
