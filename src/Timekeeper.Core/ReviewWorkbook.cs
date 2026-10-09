using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Timekeeper.Core;

public static partial class EmailWorkbookReader
{
    public const string ReviewLimitations = "This review workbook has no stable message or event IDs. Duplicate checks use row fingerprints and existing Quickbase timecards; changed or regrouped exports may not be recognized. Check the entire day for overlapping timers, meetings and earlier imports. Estimates and policy allocations need your confirmation.";
    private static readonly string[] ReviewHeaders = ["Date", "Matter / Project", "Assignment / Category", "Detailed work summary", "Billing", "Minutes", "Decimal Hours", "Evidence Reviewed", "Needs Attention"];

    private static SortedDictionary<int, Dictionary<int, string>> ReadReviewCells(System.Xml.Linq.XDocument doc, string name, List<string> shared)
    {
        var cells = ReadCells(doc, name, shared, allowMerged: true);
        foreach (var merge in doc.Descendants(Main + "mergeCell"))
        {
            var range = (string?)merge.Attribute("ref") ?? "";
            if (Regex.IsMatch(range, @"\AA1:[A-I]1\z")) continue;
            var total = Regex.Match(range, @"\AA([1-9][0-9]{0,4}):E\1\z");
            if (name == "Review" && total.Success && cells.TryGetValue(int.Parse(total.Groups[1].Value, CultureInfo.InvariantCulture), out var row)
                && row.GetValueOrDefault(1) == "TOTAL" && row.All(c => c.Key is < 2 or > 5 || c.Value.Length == 0)) continue;
            throw Bad($"Worksheet {name}: only a merged title or TOTAL label is supported.");
        }
        var formulas = doc.Descendants(Main + "f").ToList();
        if (formulas.Count == 0) return cells;
        if (name != "Review") throw Bad("Formula cells are supported only for Review hours and totals.");
        var headers = cells.Where(r => Enumerable.Range(1, ReviewHeaders.Length).All(i => r.Value.GetValueOrDefault(i) == ReviewHeaders[i - 1])).ToList();
        if (headers.Count != 1) throw Bad("Formula cells need the standard Review column headers.");
        foreach (var formula in formulas)
        {
            var address = (string?)formula.Parent?.Attribute("r") ?? "";
            var match = Regex.Match(address, @"\A([FG])([1-9][0-9]{0,4})\z");
            if (!match.Success || formula.HasAttributes || formula.Parent?.Name != Main + "c") throw Bad("Formula cells are supported only for Review hours and totals.");
            var column = match.Groups[1].Value; var number = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (!cells.TryGetValue(number, out var row) || number <= headers[0].Key) throw Bad("Formula location is invalid.");
            var expression = formula.Value.Trim();
            if (column == "G" && row.GetValueOrDefault(1) != "TOTAL" && expression == $"F{number}/60")
            {
                // Calculate locally from Minutes, ignoring Excel's potentially stale cached result.
                row[7] = decimal.Round(Number(row.GetValueOrDefault(6, ""), number) / 60m, 2, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
                continue;
            }
            if (row.GetValueOrDefault(1) == "TOTAL" && expression == $"SUM({column}{headers[0].Key + 1}:{column}{number - 1})")
            {
                var minutes = cells.Where(r => r.Key > headers[0].Key && r.Key < number && r.Value.Values.Any(v => v.Length > 0)).Sum(r => Number(r.Value.GetValueOrDefault(6, ""), r.Key));
                row[column == "F" ? 6 : 7] = (column == "F" ? minutes : decimal.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
                continue;
            }
            throw Bad($"Formula in Review!{address} is unsupported. Only same-row Minutes / 60 and whole-table SUM totals are allowed.");
        }
        return cells;
    }
    private static EmailWorkbook ReadReview(Dictionary<string, SortedDictionary<int, Dictionary<int, string>>> sheets, string digest)
    {
        if (!sheets.TryGetValue("Metadata", out var metaRows) || !sheets.TryGetValue("Review", out var rows)) throw Bad("Review workbooks need Metadata and Review sheets.");
        if (!metaRows.TryGetValue(1, out var headers) || headers.GetValueOrDefault(1) != "key" || headers.GetValueOrDefault(2) != "value") throw Bad("Metadata headers must be key, value.");
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in metaRows.Where(r => r.Key > 1).Select(r => r.Value).Where(r => r.Values.Any(v => v.Length > 0)))
            if (row.Any(c => c.Key > 2 && c.Value.Length > 0) || !meta.TryAdd(row.GetValueOrDefault(1, ""), row.GetValueOrDefault(2, ""))) throw Bad("Review metadata contains extra cells or repeated keys.");
        string Required(string key) => meta.TryGetValue(key, out var value) ? value : throw Bad($"Review metadata is missing {key}.");
        if (Required("format") != "timekeeper_email_review") throw Bad("The Review sheet requires format=timekeeper_email_review.");
        _ = Boolean(Required("employee_confirmed"), "employee_confirmed");
        var candidates = rows.Where(r => Enumerable.Range(1, ReviewHeaders.Length).All(i => r.Value.GetValueOrDefault(i) == ReviewHeaders[i - 1])).ToList();
        if (candidates.Count != 1) throw Bad("Review headers must be: " + string.Join(", ", ReviewHeaders) + ".");
        var header = candidates[0].Key;
        var activities = new List<EmailActivity>();
        decimal? totalMinutes = null, totalHours = null;
        foreach (var (number, row) in rows.Where(r => r.Key > header))
        {
            if (row.Values.All(string.IsNullOrEmpty)) continue;
            if (row.Any(c => c.Key > ReviewHeaders.Length && c.Value.Length > 0)) throw Bad($"Review row {number} contains extra columns.");
            string Cell(int column) => row.GetValueOrDefault(column, "");
            if (Cell(1).Equals("TOTAL", StringComparison.OrdinalIgnoreCase))
            {
                if (totalMinutes.HasValue || row.Any(c => c.Key is not (1 or 6 or 7) && c.Value.Length > 0)) throw Bad("The Review total row is duplicated or contains activity data.");
                totalMinutes = Number(Cell(6), number); totalHours = Number(Cell(7), number); continue;
            }
            if (totalMinutes.HasValue) throw Bad("Review activities cannot follow the TOTAL row.");
            var date = Date(Cell(1), $"Review row {number}");
            var minutes = Number(Cell(6), number);
            if (minutes is < 1 or > 1440 || decimal.Truncate(minutes) != minutes) throw Bad($"Review row {number}: Minutes must be whole minutes between 1 and 1,440.");
            if (Number(Cell(7), number) != decimal.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero)) throw Bad($"Review row {number}: Minutes and Decimal Hours disagree. Correct the workbook before importing.");
            bool? billable = Cell(5).ToUpperInvariant() switch
            {
                "BILLABLE" => true, "NON-BILLABLE" or "NONBILLABLE" => false, "QUICKBASE DEFAULT" or "" => null,
                _ => throw Bad($"Review row {number}: Billing must be BILLABLE, NON-BILLABLE or QUICKBASE DEFAULT.")
            };
            // These are local row fingerprints, explicitly not fabricated email/message IDs.
            // Exclude durations, notes and row positions so straightforward edits/reordering still match.
            var identity = string.Join("\n", new[] { Required("employee_email"), date.ToString("yyyy-MM-dd"), Cell(2), Cell(3), Cell(8) }
                .Select(value => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ")));
            var fingerprint = "review-row:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            activities.Add(new() { ActivityId = fingerprint, Date = date, Minutes = (int)minutes, MatterHint = Cell(2), AssignmentHint = Cell(3),
                Description = Cell(4), Billable = billable, TimeBasis = "employee_confirmed_estimate", EvidenceIds = [fingerprint], EvidenceSummary = Cell(8), ReviewNotes = Cell(9) });
        }
        if (totalMinutes.HasValue && (totalMinutes != activities.Sum(a => a.Minutes) || totalHours != decimal.Round(activities.Sum(a => a.Minutes) / 60m, 2, MidpointRounding.AwayFromZero))) throw Bad("The Review total does not match its activity rows.");
        var notes = sheets.GetValueOrDefault("Needs attention", []).Where(r => r.Key > 1).Select(r => string.Join(" · ", r.Value.OrderBy(c => c.Key).Select(c => c.Value).Where(v => v.Length > 0))).Where(v => v.Length > 0).ToList();
        var result = new EmailWorkbook
        {
            ContentSha256 = digest, Activities = activities, ReviewNotes = notes,
            Metadata = new() { Format = "timekeeper_email_review", EmployeeEmail = Required("employee_email"), TimeZone = Required("time_zone"),
                PeriodStart = Date(Required("period_start"), "period_start"), PeriodEnd = Date(Required("period_end"), "period_end"),
                GeneratedAtUtc = DateTimeOffset.UtcNow, EmployeeConfirmed = false, RetrievalLimitations = Required("retrieval_limitations") }
        };
        var errors = Validate(result, allowReviewDraft: true);
        if (errors.Count > 0) throw Bad(string.Join("\n", errors));
        return result;
    }

    private static decimal Number(string value, int row) => decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
        ? number : throw Bad($"Review row {row}: Minutes and Decimal Hours must be numbers.");

    public static EmailWorkbook ConfirmReview(EmailWorkbook workbook)
    {
        if (!workbook.IsReviewWorkbook) throw new InvalidOperationException("Only review workbooks use local confirmation.");
        var errors = Validate(workbook, allowReviewDraft: true);
        if (errors.Count > 0) throw Bad(string.Join("\n", errors));
        return workbook with { Metadata = workbook.Metadata with { EmployeeConfirmed = true }, ReviewConfirmedAtUtc = DateTimeOffset.UtcNow };
    }
}