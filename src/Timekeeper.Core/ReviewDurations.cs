using System.Text.Json;

namespace Timekeeper.Core;

// Local review edits are separate from untrusted agent proposals.
public sealed record ReviewDuration(string RowKey, decimal OriginalHours, decimal Hours);

public static class ReviewDurations
{
    public static string Key(VerifiedRow row) => JsonSerializer.Serialize(new
    {
        row.Date, row.Kind, row.Project, row.Assignment, row.Task, row.Category,
        Entries = row.SourceEntryIds.Order(), Activities = row.SourceActivityIds.Order(StringComparer.Ordinal)
    });

    public static ValidationResult Apply(ReadSession session, ValidationResult calculated, IReadOnlyList<ReviewDuration>? edits)
    {
        if (edits is null || edits.Count == 0 || !calculated.IsValid) return calculated;
        var result = calculated with { Rows = calculated.Rows.ToList(), Errors = calculated.Errors.ToList(), Warnings = calculated.Warnings.ToList() };
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            var matches = result.Rows.Select((row, index) => (row, index)).Where(x => Key(x.row) == edit.RowKey).ToList();
            if (!used.Add(edit.RowKey) || matches.Count != 1 || matches[0].row.Hours != edit.OriginalHours)
            { result.Errors.Add("A duration edit no longer matches its original timecard. Review the hours again."); continue; }
            if (edit.Hours is <= 0 or > 24 || decimal.Round(edit.Hours, 2) != edit.Hours)
            { result.Errors.Add("Edited hours must be greater than zero, at most 24, and have no more than two decimal places."); continue; }
            var (row, index) = matches[0];
            if (edit.Hours == row.Hours) continue;
            result.Rows[index] = row with { Hours = edit.Hours, OriginalHours = row.Hours };
            result.Warnings.Add($"{row.Date:yyyy-MM-dd} · {row.Description}: hours changed in review from {row.Hours:0.00} to {edit.Hours:0.00}. Other rows, including Misc internal, are unchanged. Confirm the daily total.");
            if (session.Days.SelectMany(d => d.Existing).Any(c => c.Date == row.Date && c.Project == row.Project && c.Assignment == row.Assignment && c.Task == row.Task && c.Hours == edit.Hours))
                result.Warnings.Add($"Possible duplicate after editing hours: {row.Date:yyyy-MM-dd}, {row.TaskName}, {edit.Hours:0.00} hours matches an existing Quickbase timecard.");
        }
        foreach (var day in session.Days)
            if (day.Existing.Sum(c => c.Hours) + result.Rows.Where(r => r.Date == day.Date).Sum(r => r.Hours) > 24)
                result.Errors.Add($"{day.Date:yyyy-MM-dd}: edited hours plus existing time exceed 24 hours.");
        return result;
    }
}