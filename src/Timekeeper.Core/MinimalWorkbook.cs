using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Timekeeper.Core;

public static partial class EmailWorkbookReader
{
    public const string MinimalImportLimitations = "This Excel contains timecard inputs without message or event IDs. Duplicate checks use local row fingerprints and existing Quickbase timecards. Reworded or regrouped exports, or work imported using a different format, may not be recognized. Check existing time before submitting. Your approved Word report contains the supporting detail and coverage notes.";
    private static readonly string[] MinimalHeaders = ["date", "minutes", "matter_hint", "assignment_hint", "task_hint", "description", "billable"];
    private static readonly string[] MinimalMetadataKeys = ["format", "employee_email", "time_zone", "employee_confirmed"];

    private static EmailWorkbook ReadMinimal(Dictionary<string, List<Dictionary<string, string>>> sheets, string digest, IReadOnlyList<int> sourceRowNumbers)
    {
        if (!sheets.TryGetValue("Metadata", out var metadataRows) || !sheets.TryGetValue("Timecards", out var rows))
            throw Bad("Minimal imports need exactly Metadata and Timecards worksheets.");
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in metadataRows)
            if (!MinimalMetadataKeys.Contains(row["key"], StringComparer.Ordinal) || !metadata.TryAdd(row["key"], row["value"]))
                throw Bad("Minimal import Metadata contains an unknown or repeated key. Use format, employee_email, time_zone and employee_confirmed only.");
        foreach (var key in MinimalMetadataKeys)
            if (!metadata.ContainsKey(key)) throw Bad($"Metadata is missing '{key}'.");
        if (metadata["format"] != "timekeeper_email_import") throw Bad("The Timecards sheet requires format=timekeeper_email_import.");
        var approved = Boolean(metadata["employee_confirmed"], "employee_confirmed");
        if (!approved) throw Bad("Approve the Word report in M365, then ask the agent to generate Excel with employee_confirmed=true. A draft or example template cannot be imported.");
        if (rows.Count == 0) throw Bad("Timecards must contain at least one approved activity.");

        var activities = new List<EmailActivity>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, index) in rows.Select((row, index) => (row, index)))
        {
            var location = $"Timecards row {sourceRowNumbers[index]}";
            var date = Date(row["date"], location);
            if (!decimal.TryParse(row["minutes"], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                || minutes is < 1 or > 1440 || decimal.Truncate(minutes) != minutes)
                throw Bad($"{location}: minutes must be whole minutes between 1 and 1,440.");
            if (string.IsNullOrWhiteSpace(row["matter_hint"]) || string.IsNullOrWhiteSpace(row["description"]))
                throw Bad($"{location}: matter_hint and description are required. Use the approved project name and work description.");
            bool? billable = row["billable"].ToUpperInvariant() switch
            {
                "" or "QUICKBASE DEFAULT" => null,
                "TRUE" or "BILLABLE" => true,
                "FALSE" or "NON-BILLABLE" or "NONBILLABLE" or "NON BILLABLE" => false,
                _ => Boolean(row["billable"], $"{location} billable")
            };
            // A local duplicate key, not a fabricated source-message ID. Duration,
            // billing and row position are excluded so common corrections stay linked.
            var identity = string.Join("\n", new[] { metadata["employee_email"], date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row["matter_hint"], row["assignment_hint"], row["task_hint"], row["description"] }
                .Select(value => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ")));
            var fingerprint = "import-row:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            if (!fingerprints.Add(fingerprint)) throw Bad($"{location} repeats the same date, matter, assignment, task and work description. Combine duplicate work before exporting; changing minutes or billing does not make it a different activity.");
            activities.Add(new()
            {
                ActivityId = fingerprint, Date = date, Minutes = (int)minutes, MatterHint = row["matter_hint"],
                AssignmentHint = row["assignment_hint"], TaskHint = row["task_hint"], Description = row["description"],
                Billable = billable, TimeBasis = "employee_confirmed_estimate", EvidenceIds = [fingerprint]
            });
        }
        var result = new EmailWorkbook
        {
            ContentSha256 = digest, Activities = activities,
            Metadata = new()
            {
                Format = "timekeeper_email_import", EmployeeEmail = metadata["employee_email"], TimeZone = metadata["time_zone"],
                EmployeeConfirmed = approved, PeriodStart = activities.Min(a => a.Date), PeriodEnd = activities.Max(a => a.Date),
                GeneratedAtUtc = DateTimeOffset.UtcNow
            }
        };
        var errors = Validate(result, allowMinimalAcknowledgement: true);
        if (errors.Count > 0) throw Bad(string.Join("\n", errors));
        return result;
    }

    public static EmailWorkbook ConfirmMinimalImport(EmailWorkbook workbook)
    {
        if (!workbook.IsMinimalImport) throw new InvalidOperationException("Only minimal imports use approved-Word confirmation.");
        var errors = Validate(workbook, allowMinimalAcknowledgement: true);
        if (errors.Count > 0) throw Bad(string.Join("\n", errors));
        // Retain the existing persisted timestamp property for local import confirmation.
        return workbook with { ReviewConfirmedAtUtc = DateTimeOffset.UtcNow };
    }
}
