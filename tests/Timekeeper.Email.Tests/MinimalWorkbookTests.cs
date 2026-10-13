using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Timekeeper.Core;

static class MinimalWorkbookTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly DateOnly WorkDate = new(2026, 9, 28);

    public static void Register(Action<string, Action> test)
    {
        test("distributed minimal template remains unapproved and its actual sample rows meet the import contract", () =>
        {
            var template = Path.Combine(AppContext.BaseDirectory, "TestData", "Timekeeper Email Import Template.xlsx");
            var originalHash = SHA256.HashData(File.ReadAllBytes(template));
            RejectAction(() => EmailWorkbookReader.Read(template), "employee_confirmed");
            var copy = Path.Combine(Path.GetTempPath(), "timekeeper-minimal-template-test-" + Guid.NewGuid().ToString("N") + ".xlsx");
            File.Copy(template, copy);
            try
            {
                using (var archive = ZipFile.Open(copy, ZipArchiveMode.Update))
                {
                    XDocument Xml(string path)
                    {
                        using var stream = archive.GetEntry(path)!.Open();
                        return XDocument.Load(stream);
                    }
                    XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                    XNamespace package = "http://schemas.openxmlformats.org/package/2006/relationships";
                    var workbook = Xml("xl/workbook.xml");
                    var metadataId = (string)workbook.Descendants(Ns + "sheet").Single(s => (string?)s.Attribute("name") == "Metadata").Attribute(rel + "id")!;
                    var target = (string)Xml("xl/_rels/workbook.xml.rels").Descendants(package + "Relationship").Single(r => (string?)r.Attribute("Id") == metadataId).Attribute("Target")!;
                    var path = target.StartsWith('/') ? target[1..] : "xl/" + target;
                    var sheet = Xml(path);
                    var sharedStrings = archive.GetEntry("xl/sharedStrings.xml") is null ? [] : Xml("xl/sharedStrings.xml").Descendants(Ns + "si").Select(s => string.Concat(s.Descendants(Ns + "t").Select(t => t.Value))).ToList();
                    string Text(XElement cell) => (string?)cell.Attribute("t") switch
                    {
                        "s" => sharedStrings[int.Parse(cell.Element(Ns + "v")!.Value, System.Globalization.CultureInfo.InvariantCulture)],
                        "inlineStr" => string.Concat(cell.Descendants(Ns + "t").Select(t => t.Value)),
                        _ => cell.Element(Ns + "v")?.Value ?? ""
                    };
                    var row = sheet.Descendants(Ns + "row").Single(r => r.Elements(Ns + "c").Any(c => ((string?)c.Attribute("r"))?.StartsWith('A') == true && Text(c) == "employee_confirmed"));
                    var approval = row.Elements(Ns + "c").Single(c => ((string?)c.Attribute("r"))?.StartsWith('B') == true);
                    // Only the disposable copy's approval cell changes. The sample data
                    // stays untouched, so this verifies the shipped artifact itself.
                    approval.SetAttributeValue("t", "b"); approval.ReplaceNodes(new XElement(Ns + "v", "1"));
                    archive.GetEntry(path)!.Delete();
                    using var output = archive.CreateEntry(path, CompressionLevel.Optimal).Open(); sheet.Save(output);
                }
                var imported = EmailWorkbookReader.Read(copy);
                Require(imported.IsMinimalImport && !imported.ReviewConfirmedAtUtc.HasValue);
                var confirmed = EmailWorkbookReader.ConfirmMinimalImport(imported);
                Equal(0, EmailWorkbookReader.Validate(confirmed).Count);
                Equal(3, confirmed.Activities.Count); Equal(30, confirmed.Activities.Sum(a => a.Minutes));
                Equal(1, confirmed.Activities.Count(a => a.Billable == true));
                Equal(1, confirmed.Activities.Count(a => a.Billable == false));
                Equal(1, confirmed.Activities.Count(a => a.Billable is null));
            }
            finally
            {
                File.Delete(copy);
                Require(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(template))), "The distributed workbook template was changed by its import test.");
            }
        });
        test("minimal workbook has only machine data and still requires local acknowledgement", () =>
        {
            using var book = new WorkbookFixture(MiniBook.Convert);
            var before = DateTimeOffset.UtcNow;
            var imported = EmailWorkbookReader.Read(book.Path);
            Require(imported.IsMinimalImport && imported.UsesLocalFingerprints && !imported.IsReviewWorkbook);
            Require(imported.Metadata.EmployeeConfirmed && !imported.ReviewConfirmedAtUtc.HasValue);
            Require(imported.Metadata.GeneratedAtUtc >= before && imported.Metadata.GeneratedAtUtc <= DateTimeOffset.UtcNow);
            Equal(WorkDate, imported.Metadata.PeriodStart); Equal(WorkDate, imported.Metadata.PeriodEnd);
            Require(EmailWorkbookReader.Validate(imported).Count > 0);
            var confirmed = EmailWorkbookReader.ConfirmMinimalImport(imported);
            Require(confirmed.ReviewConfirmedAtUtc.HasValue && !imported.ReviewConfirmedAtUtc.HasValue);
            Equal(0, EmailWorkbookReader.Validate(confirmed).Count);
            Equal(imported.ContentSha256, confirmed.ContentSha256);
            Equal(0, confirmed.Evidence.Count); Equal(0, confirmed.ReviewNotes.Count);
            Equal("employee_confirmed_estimate", confirmed.Activities.Single().TimeBasis);
            Require(confirmed.Activities.Single().ActivityId.StartsWith("import-row:", StringComparison.Ordinal));
            Equal(confirmed.Activities.Single().ActivityId, confirmed.Activities.Single().EvidenceIds.Single());
        });
        test("minimal exports cannot bypass Word approval with false missing or invalid confirmation", () =>
        {
            foreach (var approval in new[] { "false", "0", "", "yes", "2" })
            {
                using var book = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 1, "B5", approval); });
                Reject(book);
            }
            using var missing = new WorkbookFixture(p =>
            {
                MiniBook.Convert(p); var sheet = XDocument.Parse(p["xl/worksheets/sheet1.xml"]);
                sheet.Descendants(Ns + "row").Single(r => (string?)r.Attribute("r") == "5").Remove();
                p["xl/worksheets/sheet1.xml"] = sheet.ToString();
            });
            Reject(missing);
            using var valid = new WorkbookFixture(MiniBook.Convert);
            var approved = EmailWorkbookReader.Read(valid.Path);
            RejectAction(() => EmailWorkbookReader.ConfirmMinimalImport(approved with { Metadata = approved.Metadata with { EmployeeConfirmed = false } }));
        });
        test("minimal imports retain account and timezone guards after acknowledgement", () =>
        {
            using var book = new WorkbookFixture(MiniBook.Convert);
            var confirmed = EmailWorkbookReader.ConfirmMinimalImport(EmailWorkbookReader.Read(book.Path));
            var settings = DemoData.CreateSession().Settings;
            Equal(0, EmailWorkbookReader.Validate(confirmed, settings).Count);
            Require(EmailWorkbookReader.Validate(confirmed, settings with { Email = "someone.else@example.com" }).Any(e => e.Contains("employee email", StringComparison.OrdinalIgnoreCase)));
            Require(EmailWorkbookReader.Validate(confirmed, settings with { TimeZoneId = "Pacific Standard Time" }).Any(e => e.Contains("time zone", StringComparison.OrdinalIgnoreCase)));
            foreach (var (address, value) in new[] { ("B3", "invalid-address"), ("B4", "Not/AZone") })
            {
                using var invalid = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 1, address, value); });
                Reject(invalid);
            }
        });
        test("minimal reporting period follows actual row dates and cannot exceed 31 days", () =>
        {
            using var multi = new WorkbookFixture(p => MiniBook.Convert(p, MiniBook.Row("2026-09-30"), MiniBook.Row("2026-09-28")));
            var imported = EmailWorkbookReader.Read(multi.Path);
            Equal(WorkDate, imported.Metadata.PeriodStart); Equal(WorkDate.AddDays(2), imported.Metadata.PeriodEnd);
            using var oversized = new WorkbookFixture(p => MiniBook.Convert(p, MiniBook.Row("2026-09-28"), MiniBook.Row("2026-10-29")));
            Reject(oversized);
        });
        test("minimal workbooks require actual activities project hints and descriptions", () =>
        {
            using var empty = new WorkbookFixture(p => MiniBook.Convert(p, Array.Empty<string[]>())); Reject(empty);
            foreach (var address in new[] { "C2", "F2" })
            {
                using var blank = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 2, address, "   "); });
                Reject(blank);
            }
            using var hints = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 2, "D2", ""); MiniBook.Cell(p, 2, "E2", ""); });
            var activity = EmailWorkbookReader.Read(hints.Path).Activities.Single();
            Equal("", activity.AssignmentHint); Equal("", activity.TaskHint);
        });
        test("minimal workbook rejects malformed headers extra data and duplicate metadata", () =>
        {
            foreach (var edit in new Action<Dictionary<string, string>>[]
            {
                p => MiniBook.Cell(p, 2, "B1", "hours"),
                p => MiniBook.Cell(p, 2, "H2", "unexpected"),
                p => MiniBook.Cell(p, 1, "A5", "employee_email"),
                p => MiniBook.Cell(p, 1, "B2", "unsupported_format")
            })
            {
                using var book = new WorkbookFixture(p => { MiniBook.Convert(p); edit(p); }); Reject(book);
            }
        });
        test("minimal minutes preserve numeric values but reject invalid or excessive time", () =>
        {
            foreach (var input in new[] { ("15", "n"), ("15.0", "inlineStr"), ("1.5E1", "n") })
            {
                using var book = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 2, "B2", input.Item1, input.Item2); });
                Equal(15, EmailWorkbookReader.Read(book.Path).Activities.Single().Minutes);
            }
            foreach (var value in new[] { "0", "-1", "1.5", "1441", "NaN", "Infinity", "2026-09-28", "9999999999999999999999999999999" })
            {
                using var book = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 2, "B2", value); }); Reject(book);
            }
            using var tooLong = new WorkbookFixture(p => MiniBook.Convert(p, MiniBook.Row(minutes: "1000", description: "First work"), MiniBook.Row(minutes: "500", description: "Second work")));
            Reject(tooLong);
        });
        test("minimal validation identifies the actual worksheet row after blank spacers", () =>
        {
            using var book = new WorkbookFixture(p => MiniBook.Convert(p, Array.Empty<string>(), MiniBook.Row(minutes: "1.5")));
            Reject(book, "Timecards row 3", "minutes");
        });
        test("minimal billing accepts Excel booleans numeric text and human labels", () =>
        {
            foreach (var input in new (string Value, string Type, bool? Expected)[]
            {
                ("1", "b", true), ("0", "b", false), ("1.0", "n", true), ("0E0", "n", false),
                ("TRUE", "inlineStr", true), ("false", "inlineStr", false), ("Billable", "inlineStr", true),
                ("NON-BILLABLE", "inlineStr", false), ("NONBILLABLE", "inlineStr", false),
                ("QUICKBASE DEFAULT", "inlineStr", null), ("", "inlineStr", null)
            })
            {
                using var book = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 2, "G2", input.Value, input.Type); });
                Equal(input.Expected, EmailWorkbookReader.Read(book.Path).Activities.Single().Billable);
            }
            using var invalid = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 2, "G2", "2", "n"); }); Reject(invalid);
        });
        test("minimal approval accepts supported Excel boolean representations without granting local acknowledgement", () =>
        {
            foreach (var input in new[] { ("TRUE", "inlineStr"), ("1", "b"), ("1.0", "n"), ("1E0", "inlineStr") })
            {
                using var book = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 1, "B5", input.Item1, input.Item2); });
                var result = EmailWorkbookReader.Read(book.Path);
                Require(result.Metadata.EmployeeConfirmed && !result.ReviewConfirmedAtUtc.HasValue);
            }
        });
        test("minimal row identities survive reordered rows duration billing and cosmetic text edits", () =>
        {
            using var first = new WorkbookFixture(p => MiniBook.Convert(p, MiniBook.Row(description: "Reviewed client correspondence"), MiniBook.Row(description: "Prepared case notes")));
            using var edited = new WorkbookFixture(p =>
            {
                MiniBook.Convert(p, MiniBook.Row(minutes: "30", description: "prepared   CASE notes", billing: "true"), MiniBook.Row(minutes: "10", description: " REVIEWED CLIENT correspondence ", billing: "false"));
                MiniBook.Cell(p, 1, "B3", "ALEX@EXAMPLE.COM"); MiniBook.Cell(p, 2, "C2", " EXAMPLE   case ");
                MiniBook.Cell(p, 2, "D3", " PRESENTATION design "); MiniBook.Cell(p, 2, "E3", " DESIGN ");
            });
            var a = EmailWorkbookReader.Read(first.Path); var b = EmailWorkbookReader.Read(edited.Path);
            Equal(string.Join("|", a.Activities.Select(r => r.ActivityId).Order()), string.Join("|", b.Activities.Select(r => r.ActivityId).Order()));
            Require(a.ContentSha256 != b.ContentSha256);
        });
        test("minimal duplicate fingerprints are rejected with the actual row location", () =>
        {
            using var duplicate = new WorkbookFixture(p => MiniBook.Convert(p, MiniBook.Row(), MiniBook.Row(minutes: "30", billing: "true")));
            Reject(duplicate, "Timecards", "3");
        });
        test("minimal fingerprints distinguish separate dates accounts matters tasks and descriptions", () =>
        {
            using var baseline = new WorkbookFixture(MiniBook.Convert);
            var original = EmailWorkbookReader.Read(baseline.Path).Activities.Single().ActivityId;
            foreach (var edit in new[] { (2, "A2", "2026-09-29"), (1, "B3", "another@example.com"), (2, "C2", "Other case"), (2, "D2", "Research assignment"), (2, "E2", "Research"), (2, "F2", "Prepared a different document") })
            {
                using var changed = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, edit.Item1, edit.Item2, edit.Item3); });
                Require(original != EmailWorkbookReader.Read(changed.Path).Activities.Single().ActivityId);
            }
        });
        test("minimal calendar dates accept text native serial and shared string cells consistently", () =>
        {
            using var baseline = new WorkbookFixture(MiniBook.Convert);
            var original = EmailWorkbookReader.Read(baseline.Path).Activities.Single().ActivityId;
            foreach (var input in new[] { ("46293.75", "n"), ("46293", "inlineStr"), ("2026-09-28T23:30:00-04:00", "d"), ("September 28, 2026", "inlineStr"), ("28/09/2026", "inlineStr") })
            {
                using var book = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 2, "A2", input.Item1, input.Item2); });
                var row = EmailWorkbookReader.Read(book.Path).Activities.Single(); Equal(WorkDate, row.Date); Equal(original, row.ActivityId); Equal(5, row.Minutes);
            }
            using var shared = new WorkbookFixture(p =>
            {
                MiniBook.Convert(p); MiniBook.Cell(p, 2, "A2", "0", "s");
                p["xl/sharedStrings.xml"] = new XElement(Ns + "sst", new XElement(Ns + "si", new XElement(Ns + "t", "2026-09-28"))).ToString();
            });
            Equal(original, EmailWorkbookReader.Read(shared.Path).Activities.Single().ActivityId);
        });
        test("minimal 1904 date epoch preserves the same work date and fingerprint", () =>
        {
            using var baseline = new WorkbookFixture(MiniBook.Convert);
            using var oldEpoch = new WorkbookFixture(p =>
            {
                MiniBook.Convert(p); MiniBook.Cell(p, 2, "A2", "44831", "n");
                var workbook = XDocument.Parse(p["xl/workbook.xml"]); workbook.Root!.AddFirst(new XElement(Ns + "workbookPr", new XAttribute("date1904", true)));
                p["xl/workbook.xml"] = workbook.ToString();
            });
            Equal(EmailWorkbookReader.Read(baseline.Path).Activities.Single().ActivityId, EmailWorkbookReader.Read(oldEpoch.Path).Activities.Single().ActivityId);
        });
        test("minimal ambiguous or fictitious dates identify the Timecards cell", () =>
        {
            foreach (var value in new[] { "10/12/2026", "60", "-1", "2026-02-30" })
            {
                using var book = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 2, "A2", value); }); Reject(book, "Timecards!A2");
            }
        });
        test("minimal Timecards filter names allow quoted or unquoted bounded ranges", () =>
        {
            foreach (var name in new[] { "Timecards!$A$1:$G$2", "'Timecards'!$A$1:$G$2" })
            {
                using var book = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Filter(p, name); });
                Equal(5, EmailWorkbookReader.Read(book.Path).Activities.Single().Minutes);
            }
        });
        test("minimal named formulas and formulas with cached values cannot drive imported time", () =>
        {
            foreach (var reference in new[] { "SUM(Timecards!$B$2:$B$2)", "'[external.xlsx]Timecards'!$A$1:$G$2", "Timecards!$A$1:$H$2", "Metadata!$A$1:$B$5" })
            {
                using var named = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Filter(p, reference); }); Reject(named);
            }
            using var formula = new WorkbookFixture(p =>
            {
                MiniBook.Convert(p); var sheet = XDocument.Parse(p["xl/worksheets/sheet2.xml"]);
                var cell = sheet.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "B2");
                cell.Attribute("t")?.Remove(); cell.ReplaceNodes(new XElement(Ns + "f", "1+4"), new XElement(Ns + "v", 5));
                p["xl/worksheets/sheet2.xml"] = sheet.ToString();
            });
            Reject(formula);
        });
        test("minimal workbooks cannot contain external data or merged activity cells", () =>
        {
            using var external = new WorkbookFixture(p => { MiniBook.Convert(p); p["xl/connections.xml"] = "<connections/>"; }); Reject(external);
            using var merged = new WorkbookFixture(p =>
            {
                MiniBook.Convert(p); var sheet = XDocument.Parse(p["xl/worksheets/sheet2.xml"]);
                sheet.Root!.Add(new XElement(Ns + "mergeCells", new XElement(Ns + "mergeCell", new XAttribute("ref", "C2:D2"))));
                p["xl/worksheets/sheet2.xml"] = sheet.ToString();
            });
            Reject(merged);
        });
        test("minimal format does not relax legacy final evidence or review confirmation", () =>
        {
            using var final = new WorkbookFixture(); var full = EmailWorkbookReader.Read(final.Path);
            Require(!full.IsMinimalImport && !full.UsesLocalFingerprints); Equal(0, EmailWorkbookReader.Validate(full).Count);
            using var noEvidence = new WorkbookFixture(p => MiniBook.Cell(p, 2, "J2", "")); Reject(noEvidence);
            using var review = new WorkbookFixture(ReviewBook.Convert); var draft = EmailWorkbookReader.Read(review.Path);
            Require(!draft.IsMinimalImport && draft.IsReviewWorkbook && draft.UsesLocalFingerprints);
            Require(!draft.Metadata.EmployeeConfirmed && EmailWorkbookReader.Validate(draft).Count > 0);
            Equal(0, EmailWorkbookReader.Validate(EmailWorkbookReader.ConfirmReview(draft)).Count);
        });
        test("minimal activities use normal assignment validation and combine before rounding", () =>
        {
            using var book = new WorkbookFixture(p => MiniBook.Convert(p, MiniBook.Row(minutes: "1", description: "Reviewed message"), MiniBook.Row(minutes: "1", description: "Prepared response")));
            var session = Session(EmailWorkbookReader.ConfirmMinimalImport(EmailWorkbookReader.Read(book.Path)));
            var proposal = Proposal(session); var result = EmailRules.Validate(session, proposal); Valid(result);
            Equal(1, result.Rows.Count); Equal(0.08m, result.Rows.Single().Hours); Equal(2, result.Rows.Single().EmailEvidenceKeys.Count);
            Require(result.Warnings.Any(w => w.Contains("duplicate", StringComparison.OrdinalIgnoreCase)));
            var incomplete = proposal with { Rows = [proposal.Rows[0]] };
            Require(!EmailRules.Validate(session, incomplete).IsValid);
            var wrongAssignment = proposal with { Rows = proposal.Rows.Select(r => r with { Assignment = 99999 }).ToList() };
            Require(!EmailRules.Validate(session, wrongAssignment).IsValid);
        });
        test("minimal billing classifications remain separate after assignment mapping", () =>
        {
            using var book = new WorkbookFixture(p => MiniBook.Convert(p, MiniBook.Row(description: "Lead intake", billing: "true"), MiniBook.Row(description: "Observe intake", billing: "false")));
            var session = Session(EmailWorkbookReader.ConfirmMinimalImport(EmailWorkbookReader.Read(book.Path)));
            var result = EmailRules.Validate(session, Proposal(session)); Valid(result);
            Equal(2, result.Rows.Count); Require(result.Rows.Any(r => r.BillableOverride == true) && result.Rows.Any(r => r.BillableOverride == false));
            Require(result.Rows.All(r => r.Assignment == 501 && r.Task == 60));
        });
        test("minimal saved confirmation and duplicate keys survive session and receipt persistence", () =>
        {
            using var original = new WorkbookFixture(MiniBook.Convert);
            using var edited = new WorkbookFixture(p => { MiniBook.Convert(p); MiniBook.Cell(p, 2, "B2", "10"); });
            var first = Session(EmailWorkbookReader.ConfirmMinimalImport(EmailWorkbookReader.Read(original.Path)));
            var restored = JsonSerializer.Deserialize<ReadSession>(JsonSerializer.Serialize(first, JsonDefaults.Options), JsonDefaults.Options)!;
            Require(restored.EmailWorkbook!.IsMinimalImport && restored.EmailWorkbook.UsesLocalFingerprints && restored.EmailWorkbook.ReviewConfirmedAtUtc.HasValue);
            var result = EmailRules.Validate(restored, Proposal(restored)); Valid(result);
            var receipt = new SubmissionReceipt { Rows = [new RowOutcome { Row = result.Rows.Single(), Status = "created", RecordId = 8001 }] };
            var saved = JsonSerializer.Deserialize<SubmissionReceipt>(JsonSerializer.Serialize(receipt, JsonDefaults.Options), JsonDefaults.Options)!;
            var retry = Session(EmailWorkbookReader.ConfirmMinimalImport(EmailWorkbookReader.Read(edited.Path)));
            var retryResult = EmailRules.Validate(retry, Proposal(retry)); Valid(retryResult);
            Equal(saved.Rows.Single().Row.EmailEvidenceKeys.Single(), retryResult.Rows.Single().EmailEvidenceKeys.Single());
        });
        test("minimal resubmission of a saved row is blocked before any network request", () =>
        {
            using var book = new WorkbookFixture(MiniBook.Convert);
            var approved = EmailWorkbookReader.ConfirmMinimalImport(EmailWorkbookReader.Read(book.Path));
            var session = Session(approved);
            session = session with { Demo = false, Settings = session.Settings with { EmployeeId = "1234.test" } };
            var proposal = Proposal(session); var rows = EmailRules.Validate(session, proposal); Valid(rows);
            var root = Path.Combine(Path.GetTempPath(), "timekeeper-minimal-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new SessionStore(root); store.SaveSession(session);
                store.SaveReceipt(new SubmissionReceipt { SessionId = "prior-import", ProfileKey = session.Settings.ProfileKey, Rows = [new RowOutcome { Row = rows.Rows.Single(), Status = "created", RecordId = 8001 }] });
                using var transport = new NoNetworkHandler(); using var client = new HttpClient(transport);
                using var api = new TimecardApi(session.Settings, new Credentials("", "fake-unit-test-token"), client);
                var submission = new SubmissionService(api, store);
                try { submission.SubmitAsync(session, proposal, rows.Rows, warningsAcknowledged: true).GetAwaiter().GetResult(); throw new Exception("Repeated import was not rejected."); }
                catch (InvalidOperationException error) { Require(error.Message.Contains("already written", StringComparison.OrdinalIgnoreCase), error.Message); }
                Equal(0, transport.Requests); Equal(1, store.LoadReceipts().Count);
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    private static ReadSession Session(EmailWorkbook workbook)
    {
        var seed = DemoData.CreateSession();
        return seed with { EmailWorkbook = workbook, Days = workbook.Activities.Select(a => a.Date).Distinct().Select(date => new DaySnapshot { Date = date }).ToList() };
    }

    private static ProposalEnvelope Proposal(ReadSession session) => new()
    {
        SessionId = session.SessionId, EmployeeId = session.Settings.EmployeeId,
        Rows = session.EmailWorkbook!.Activities.Select(a => new ProposalRow { Date = a.Date, SourceActivityIds = [a.ActivityId], Assignment = 501, Project = 100, Task = 60, Category = 20, Description = a.Description, BillableOverride = a.Billable }).ToList()
    };

    private static void Valid(ValidationResult result) => Require(result.IsValid, string.Join(" | ", result.Errors));
    private static void Equal<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
    private static void Require(bool condition, string message = "Minimal import contract assertion failed.") { if (!condition) throw new Exception(message); }
    private static void Reject(WorkbookFixture book, params string[] messages) => RejectAction(() => EmailWorkbookReader.Read(book.Path), messages);
    private static void RejectAction(Action action, params string[] messages)
    {
        try { action(); }
        catch (InvalidDataException error)
        {
            foreach (var message in messages) Require(error.Message.Contains(message, StringComparison.OrdinalIgnoreCase), error.Message);
            return;
        }
        throw new Exception("Expected invalid minimal workbook data to be rejected.");
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; throw new InvalidOperationException("No network request is permitted in the duplicate-import regression test.");
        }
    }
}

static class MiniBook
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly string[] Headers = ["date", "minutes", "matter_hint", "assignment_hint", "task_hint", "description", "billable"];

    public static string[] Row(string date = "2026-09-28", string minutes = "5", string description = "Reviewed client correspondence", string billing = "") =>
        [date, minutes, "Example case", "Presentation design", "Design", description, billing];

    public static void Convert(Dictionary<string, string> parts) => Convert(parts, Row());
    public static void Convert(Dictionary<string, string> parts, params string[][] rows)
    {
        parts["xl/workbook.xml"] = parts["xl/workbook.xml"].Replace("Activities", "Timecards", StringComparison.Ordinal);
        parts["xl/worksheets/sheet1.xml"] = Sheet([["key", "value"], ["format", "timekeeper_email_import"], ["employee_email", "alex@example.com"], ["time_zone", "America/New_York"], ["employee_confirmed", "true"]]);
        parts["xl/worksheets/sheet2.xml"] = Sheet(new[] { Headers }.Concat(rows).ToArray());
    }

    public static void Cell(Dictionary<string, string> parts, int sheetIndex, string address, string value, string type = "inlineStr")
    {
        var path = $"xl/worksheets/sheet{sheetIndex}.xml"; var sheet = XDocument.Parse(parts[path]);
        var rowNumber = new string(address.SkipWhile(char.IsLetter).ToArray());
        var row = sheet.Descendants(Ns + "row").Single(r => (string?)r.Attribute("r") == rowNumber);
        var cell = row.Elements(Ns + "c").SingleOrDefault(c => (string?)c.Attribute("r") == address);
        if (cell is null) { cell = new XElement(Ns + "c", new XAttribute("r", address)); row.Add(cell); }
        cell.SetAttributeValue("t", type);
        cell.ReplaceNodes(type == "inlineStr" ? new XElement(Ns + "is", new XElement(Ns + "t", value)) : new XElement(Ns + "v", value));
        parts[path] = sheet.ToString();
    }

    public static void Filter(Dictionary<string, string> parts, string reference)
    {
        var workbook = XDocument.Parse(parts["xl/workbook.xml"]);
        workbook.Root!.Add(new XElement(Ns + "definedNames", new XElement(Ns + "definedName", new XAttribute("name", "_xlnm._FilterDatabase"), new XAttribute("localSheetId", 1), new XAttribute("hidden", 1), reference)));
        parts["xl/workbook.xml"] = workbook.ToString();
    }

    private static string Sheet(string[][] rows) => new XElement(Ns + "worksheet", new XElement(Ns + "sheetData", rows.Select((row, r) =>
        new XElement(Ns + "row", new XAttribute("r", r + 1), row.Select((value, c) => new XElement(Ns + "c", new XAttribute("r", $"{(char)('A' + c)}{r + 1}"), new XAttribute("t", "inlineStr"), new XElement(Ns + "is", new XElement(Ns + "t", value)))))))).ToString(SaveOptions.DisableFormatting);
}
