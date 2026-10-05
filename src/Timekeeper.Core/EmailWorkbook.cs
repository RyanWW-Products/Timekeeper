using System.Globalization;
using System.IO.Compression;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Timekeeper.Core;

public sealed record EmailWorkbook
{
    public EmailWorkbookMetadata Metadata { get; init; } = new();
    public List<EmailActivity> Activities { get; init; } = [];
    public List<EmailEvidence> Evidence { get; init; } = [];
    public string ContentSha256 { get; init; } = "";
    public static EmailWorkbook Load(string path) => EmailWorkbookReader.Read(path);
}

public sealed record EmailWorkbookMetadata
{
    public string Format { get; init; } = "timekeeper_email_activity";
    public int SchemaVersion { get; init; } = 1;
    public string EmployeeEmail { get; init; } = "";
    public string TimeZone { get; init; } = "";
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public bool EmployeeConfirmed { get; init; }
    public string RetrievalLimitations { get; init; } = "";
}

public sealed record EmailActivity
{
    public string ActivityId { get; init; } = "";
    public DateOnly Date { get; init; }
    public int Minutes { get; init; }
    public string MatterHint { get; init; } = "";
    public string AssignmentHint { get; init; } = "";
    public string TaskHint { get; init; } = "";
    public string Description { get; init; } = "";
    public bool? Billable { get; init; }
    public string TimeBasis { get; init; } = "";
    public List<string> EvidenceIds { get; init; } = [];
}

public sealed record EmailEvidence
{
    public string EvidenceId { get; init; } = "";
    public string SourceType { get; init; } = "";
    public DateTimeOffset TimestampUtc { get; init; }
    public string Direction { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Action { get; init; } = "";
}

public sealed record RecordedActivityLink
{
    public string ActivityId { get; init; } = "";
    public int ExistingRecordId { get; init; }
}

/// <summary>Reads only plain cell values from a bounded XLSX package. Excel is never launched.</summary>
public static class EmailWorkbookReader
{
    public const int MaximumActivities = 1000;
    private const long MaximumFileBytes = 10 * 1024 * 1024;
    private const long MaximumPackageBytes = 32 * 1024 * 1024;
    private const long MaximumPartBytes = 8 * 1024 * 1024;
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Relations = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OfficeRelations = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly string[] MetadataKeys = ["format", "schema_version", "employee_email", "time_zone", "period_start", "period_end", "generated_at_utc", "employee_confirmed", "retrieval_limitations"];
    private static readonly string[] ActivityHeaders = ["activity_id", "date", "minutes", "matter_hint", "assignment_hint", "task_hint", "description", "billable", "time_basis", "evidence_ids"];
    private static readonly string[] EvidenceHeaders = ["evidence_id", "source_type", "timestamp_utc", "direction", "subject", "action"];

    public static EmailWorkbook Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw Bad("Choose a .xlsx workbook. PDF, CSV and macro-enabled workbooks cannot be imported.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is 0 or > MaximumFileBytes) throw Bad("The workbook must be nonempty and no larger than 10 MB.");
        var digest = Convert.ToHexString(SHA256.HashData(file));
        file.Position = 0;
        try
        {
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is 0 or > 256) throw Bad("The workbook contains too many package parts.");
            var parts = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var part in archive.Entries)
            {
                var name = part.FullName;
                if (name.EndsWith('/')) continue;
                if (name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(p => p is "." or "..") || !parts.TryAdd(name, part))
                    throw Bad("The workbook has an invalid or duplicate package path.");
                expanded += part.Length;
                if (part.Length > MaximumPartBytes || expanded > MaximumPackageBytes)
                    throw Bad("The workbook expands beyond the supported size.");
                if (new[] { "vbaproject", "externallinks/", "activex/", "embeddings/", "macrosheets/", "dialogsheets/", "connections.xml", "querytables/" }.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                    throw Bad("The workbook contains macros, external data or embedded objects. Export a plain .xlsx file.");
            }
            var documents = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts.Values.Where(p => p.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || p.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = part.Open();
                using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumPartBytes, MaxCharactersFromEntities = 0 });
                var doc = XDocument.Load(xml, LoadOptions.None);
                if (doc.Descendants(Main + "f").Any() || doc.Descendants(Main + "definedName").Any())
                    throw Bad("Formula cells and defined formulas are not supported. Export plain values.");
                if (doc.Descendants(Relations + "Relationship").Any(r => string.Equals((string?)r.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)))
                    throw Bad("External links are not supported. Export plain text values without hyperlinks.");
                if (doc.Descendants().Attributes("ContentType").Any(a => a.Value.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase)))
                    throw Bad("Macro-enabled content is not supported.");
                documents.Add(part.FullName, doc);
            }
            XDocument Required(string name) => documents.GetValueOrDefault(name) ?? throw Bad($"The workbook is missing {name}.");
            _ = Required("[Content_Types].xml");
            var workbook = Required("xl/workbook.xml");
            var rels = Required("xl/_rels/workbook.xml.rels").Root?.Elements(Relations + "Relationship").ToList() ?? throw Bad("Workbook relationships are missing.");
            if (rels.Any(r => string.IsNullOrWhiteSpace((string?)r.Attribute("Id"))) || rels.Select(r => (string?)r.Attribute("Id")).Distinct(StringComparer.Ordinal).Count() != rels.Count)
                throw Bad("Workbook relationships are invalid or duplicated.");
            var shared = documents.TryGetValue("xl/sharedStrings.xml", out var sharedDoc)
                ? sharedDoc.Root!.Elements(Main + "si").Select(PlainText).ToList() : [];
            if (shared.Count > 50_000) throw Bad("The workbook contains too many shared strings.");
            var sheets = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
            var sheetParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sheet in workbook.Root?.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
            {
                var name = (string?)sheet.Attribute("name") ?? "";
                if (name is not ("Metadata" or "Activities" or "Evidence")) throw Bad($"Unsupported worksheet '{name}'. Use Metadata, Activities and optional Evidence only.");
                var relation = rels.SingleOrDefault(r => (string?)r.Attribute("Id") == (string?)sheet.Attribute(OfficeRelations + "id"));
                if (relation is null || !((string?)relation.Attribute("Type") ?? "").EndsWith("/worksheet", StringComparison.Ordinal)) throw Bad($"Worksheet '{name}' has an invalid relationship.");
                var target = (string?)relation.Attribute("Target") ?? "";
                if (target.StartsWith("/xl/", StringComparison.Ordinal)) target = target[1..];
                else target = "xl/" + target;
                if (target.Contains('\\') || target.Contains('%') || target.Split('/').Any(p => p is "." or "..") || !target.StartsWith("xl/worksheets/", StringComparison.Ordinal))
                    throw Bad("A worksheet points outside the workbook.");
                if (!sheetParts.Add(target) || sheets.ContainsKey(name)) throw Bad("Worksheet names or package references are duplicated.");
                sheets.Add(name, ReadSheet(Required(target), name, shared));
            }
            if (!sheets.TryGetValue("Metadata", out var metadataRows) || !sheets.TryGetValue("Activities", out var activityRows))
                throw Bad("The workbook needs Metadata and Activities worksheets.");
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in metadataRows)
            {
                if (!metadata.TryAdd(row["key"], row["value"]) || !MetadataKeys.Contains(row["key"], StringComparer.Ordinal)) throw Bad("Metadata contains a duplicate or unknown key.");
            }
            foreach (var key in MetadataKeys) if (!metadata.ContainsKey(key)) throw Bad($"Metadata is missing '{key}'.");
            if (!int.TryParse(metadata["schema_version"], NumberStyles.None, CultureInfo.InvariantCulture, out var version)) throw Bad("Metadata schema_version must be 1.");
            var result = new EmailWorkbook
            {
                ContentSha256 = digest,
                Metadata = new EmailWorkbookMetadata
                {
                    Format = metadata["format"], SchemaVersion = version, EmployeeEmail = metadata["employee_email"], TimeZone = metadata["time_zone"],
                    PeriodStart = Date(metadata["period_start"], "period_start"), PeriodEnd = Date(metadata["period_end"], "period_end"),
                    GeneratedAtUtc = Timestamp(metadata["generated_at_utc"], "generated_at_utc"), EmployeeConfirmed = Boolean(metadata["employee_confirmed"], "employee_confirmed"), RetrievalLimitations = metadata["retrieval_limitations"]
                },
                Activities = activityRows.Select((row, index) =>
                {
                    if (!decimal.TryParse(row["minutes"], NumberStyles.Float, CultureInfo.InvariantCulture, out var minuteValue) || minuteValue != decimal.Truncate(minuteValue) || minuteValue is < 1 or > 1440) throw Bad($"Activities row {index + 2}: minutes must be a positive whole number no greater than 1,440.");
                    var minutes = (int)minuteValue;
                    return new EmailActivity { ActivityId = row["activity_id"], Date = Date(row["date"], $"Activities row {index + 2} date"), Minutes = minutes, MatterHint = row["matter_hint"], AssignmentHint = row["assignment_hint"], TaskHint = row["task_hint"], Description = row["description"], Billable = row["billable"] == "" ? null : Boolean(row["billable"], $"Activities row {index + 2} billable"), TimeBasis = row["time_basis"], EvidenceIds = row["evidence_ids"].Split(';', StringSplitOptions.TrimEntries).ToList() };
                }).ToList(),
                Evidence = sheets.GetValueOrDefault("Evidence", []).Select(row => new EmailEvidence { EvidenceId = row["evidence_id"], SourceType = row["source_type"], TimestampUtc = Timestamp(row["timestamp_utc"], "Evidence timestamp_utc"), Direction = row["direction"], Subject = row["subject"], Action = row["action"] }).ToList()
            };
            var errors = Validate(result);
            if (errors.Count > 0) throw Bad(string.Join("\n", errors));
            return result;
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or OverflowException)
        { throw Bad("The workbook structure is invalid. Ask the email agent for a new plain-value export.", ex); }
    }

    public static List<string> Validate(EmailWorkbook? workbook, AppSettings? settings = null, DateTimeOffset? now = null)
    {
        var errors = new List<string>();
        if (workbook?.Metadata is null || workbook.Activities is null || workbook.Evidence is null) return ["Email workbook metadata or activities are missing."];
        var meta = workbook.Metadata;
        if (meta.Format != "timekeeper_email_activity" || meta.SchemaVersion != 1) errors.Add("Unsupported email workbook format or schema version.");
        if (!MailAddress.TryCreate(meta.EmployeeEmail, out var address) || address.Address != meta.EmployeeEmail) errors.Add("The workbook employee_email must be a valid email address.");
        if (settings is not null && !string.Equals(meta.EmployeeEmail, settings.Email, StringComparison.OrdinalIgnoreCase)) errors.Add("The workbook employee email does not match the verified Quickbase account.");
        if (!meta.EmployeeConfirmed) errors.Add("Confirm all activity and minutes with the email agent before exporting. employee_confirmed must be true.");
        TimeZoneInfo? zone = null;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(meta.TimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { errors.Add("The workbook time_zone must be a valid Windows or IANA time zone."); }
        if (zone is not null && settings is not null)
        {
            try { if (!zone.HasSameRules(TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId))) errors.Add("The workbook time zone does not match your Timekeeper settings."); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { errors.Add("The configured Timekeeper time zone is invalid."); }
        }
        if (meta.PeriodStart == default || meta.PeriodEnd < meta.PeriodStart || meta.PeriodEnd.DayNumber - meta.PeriodStart.DayNumber >= 31) errors.Add("The workbook reporting period must contain one to 31 dates.");
        if (meta.GeneratedAtUtc == default || meta.GeneratedAtUtc.Offset != TimeSpan.Zero || meta.GeneratedAtUtc > (now ?? DateTimeOffset.UtcNow).AddMinutes(5)) errors.Add("The workbook generated_at_utc must be a valid UTC timestamp that is not in the future.");
        if (meta.RetrievalLimitations is null || meta.RetrievalLimitations.Length > 4000 || HasControls(meta.RetrievalLimitations)) errors.Add("Retrieval limitations must be plain text of at most 4,000 characters.");
        if (workbook.Activities.Count is 0 or > MaximumActivities) errors.Add($"The workbook must contain one to {MaximumActivities} activities.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in workbook.Activities)
        {
            if (row is null) { errors.Add("An email activity is missing."); continue; }
            if (!Identifier(row.ActivityId, 256) || !ids.Add(row.ActivityId)) errors.Add("Activity IDs must be nonempty, unique text of at most 256 characters.");
            var label = $"Activity '{row.ActivityId}'";
            if (row.Date < meta.PeriodStart || row.Date > meta.PeriodEnd || row.Date == default) errors.Add($"{label} falls outside the workbook reporting period.");
            if (row.Minutes is < 1 or > 1440) errors.Add($"{label} must have between 1 and 1,440 confirmed whole minutes.");
            if (row.TimeBasis is not ("employee_confirmed_estimate" or "measured")) errors.Add($"{label} has an unsupported time_basis.");
            if (string.IsNullOrWhiteSpace(row.Description) || row.Description.Length > 4000 || HasControls(row.Description)) errors.Add($"{label} needs a plain-text description of at most 4,000 characters.");
            if (new[] { row.MatterHint, row.AssignmentHint, row.TaskHint }.Any(s => s is null || s.Length > 1000 || HasControls(s))) errors.Add($"{label} has an invalid mapping hint.");
            if (row.EvidenceIds is null || row.EvidenceIds.Count is 0 or > 1000) { errors.Add($"{label} needs immutable evidence IDs for its messages or events."); continue; }
            foreach (var id in row.EvidenceIds)
                if (!Identifier(id, 2048) || id.Contains(';') || !evidenceIds.Add(id)) errors.Add($"{label}: evidence IDs must be nonempty and used only once in the workbook, including across different dates.");
        }
        foreach (var day in workbook.Activities.Where(a => a is not null).GroupBy(a => a.Date))
            if (day.Sum(a => (long)a.Minutes) > 1440) errors.Add($"{day.Key:yyyy-MM-dd}: confirmed activity exceeds 24 hours.");
        var corroborating = new HashSet<string>(StringComparer.Ordinal);
        if (workbook.Evidence.Count > 10_000) errors.Add("The Evidence sheet contains too many entries.");
        foreach (var item in workbook.Evidence)
        {
            if (item is null || !Identifier(item.EvidenceId, 2048) || !corroborating.Add(item.EvidenceId) || !evidenceIds.Contains(item.EvidenceId)) { errors.Add("Evidence rows need unique IDs referenced by an activity."); continue; }
            if (item.TimestampUtc == default || item.TimestampUtc.Offset != TimeSpan.Zero || new[] { item.SourceType, item.Direction, item.Subject, item.Action }.Any(s => s is null || s.Length > 4000 || HasControls(s))) errors.Add("An Evidence row contains an invalid UTC timestamp or text value.");
        }
        return errors.Distinct().ToList();
    }

    private static List<Dictionary<string, string>> ReadSheet(XDocument document, string name, List<string> shared)
    {
        if (document.Root?.Name != Main + "worksheet" || document.Descendants(Main + "mergeCell").Any()) throw Bad($"Worksheet '{name}' must contain a plain unmerged table.");
        var cells = new SortedDictionary<int, Dictionary<int, string>>();
        var totalCells = 0;
        foreach (var row in document.Root.Element(Main + "sheetData")?.Elements(Main + "row") ?? [])
        {
            if (!int.TryParse((string?)row.Attribute("r"), out var rowNumber) || rowNumber is < 1 or > 10_001 || cells.ContainsKey(rowNumber)) throw Bad($"Worksheet '{name}' has invalid or repeated row positions.");
            var values = new Dictionary<int, string>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                if (++totalCells > 110_000) throw Bad("The worksheet contains too many cells.");
                var match = Regex.Match((string?)cell.Attribute("r") ?? "", @"\A([A-Z]{1,2})([1-9][0-9]{0,4})\z", RegexOptions.CultureInvariant);
                if (!match.Success || match.Groups[2].Value != rowNumber.ToString(CultureInfo.InvariantCulture)) throw Bad($"Worksheet '{name}' has an invalid cell address.");
                var column = match.Groups[1].Value.Aggregate(0, (n, c) => n * 26 + c - 'A' + 1);
                if (column > 32 || values.ContainsKey(column)) throw Bad($"Worksheet '{name}' has too many columns or a repeated cell.");
                var type = (string?)cell.Attribute("t") ?? "n";
                var value = (string?)cell.Element(Main + "v") ?? "";
                value = type switch
                {
                    "inlineStr" => PlainText(cell.Element(Main + "is") ?? new XElement(Main + "is")),
                    "s" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count ? shared[index] : throw Bad("A shared string index is invalid."),
                    "b" => value is "1" or "0" ? value == "1" ? "true" : "false" : throw Bad("A Boolean cell is invalid."),
                    "n" or "str" => value,
                    _ => throw Bad($"Worksheet '{name}' contains an unsupported cell type. Use plain text and numbers.")
                };
                if (value.Length > 16_000 || HasControls(value)) throw Bad("A cell contains too much text or unsupported control characters.");
                values.Add(column, value.Trim());
            }
            cells.Add(rowNumber, values);
        }
        if (!cells.TryGetValue(1, out var headerCells)) throw Bad($"Worksheet '{name}' must have its headers in row 1.");
        var expected = name switch { "Metadata" => ["key", "value"], "Activities" => ActivityHeaders, _ => EvidenceHeaders };
        if (headerCells.Any(c => c.Key > expected.Length && c.Value.Length > 0) || !Enumerable.Range(1, expected.Length).All(i => headerCells.TryGetValue(i, out var h) && h == expected[i - 1])) throw Bad($"Worksheet '{name}' headers must be: {string.Join(", ", expected)}.");
        var result = new List<Dictionary<string, string>>();
        foreach (var (rowNumber, values) in cells.Where(p => p.Key != 1))
        {
            if (values.Values.All(string.IsNullOrEmpty)) continue;
            if (values.Any(c => c.Key > expected.Length && c.Value.Length > 0)) throw Bad($"Worksheet '{name}' row {rowNumber} contains unexpected columns.");
            result.Add(Enumerable.Range(1, expected.Length).ToDictionary(i => expected[i - 1], i => values.GetValueOrDefault(i, ""), StringComparer.Ordinal));
        }
        if (name == "Activities" && result.Count > MaximumActivities) throw Bad($"The workbook supports at most {MaximumActivities} activities.");
        return result;
    }

    private static string PlainText(XElement element) => string.Concat(element.Descendants(Main + "t").Select(t => t.Value));
    private static bool HasControls(string text) => text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t');
    private static bool Identifier(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text == text.Trim() && text.Length <= max && !text.Any(char.IsControl);
    private static DateOnly Date(string value, string field) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw Bad($"{field} must be ISO date text: YYYY-MM-DD.");
    private static DateTimeOffset Timestamp(string value, string field) => DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : throw Bad($"{field} must be an ISO UTC timestamp ending in Z.");
    private static bool Boolean(string value, string field) => value is "true" or "false" ? value == "true" : throw Bad($"{field} must be true or false.");
    private static InvalidDataException Bad(string message, Exception? inner = null) => new("Email workbook: " + message, inner);
}
