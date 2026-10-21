namespace Timekeeper.Core;

// Local review edits do not rewrite the original source or the agent's proposal.
public sealed record ReviewDescription(string RowKey, string OriginalDescription, string Description);

public static class ReviewDescriptions
{
    public static ValidationResult Apply(ReadSession session, ValidationResult calculated, IReadOnlyList<ReviewDescription>? edits)
    {
        if (edits is null || edits.Count == 0 || !calculated.IsValid) return calculated;
        var result = calculated with { Rows = calculated.Rows.ToList(), Errors = calculated.Errors.ToList(), Warnings = calculated.Warnings.ToList() };
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (edit is null)
            {
                result.Errors.Add("A description edit no longer matches its original timecard. Review the activity again.");
                continue;
            }
            var matches = result.Rows.Select((row, index) => (row, index)).Where(x => ReviewDurations.Key(x.row) == edit.RowKey).ToList();
            if (!used.Add(edit.RowKey) || matches.Count != 1 || !string.Equals(matches[0].row.Description, edit.OriginalDescription, StringComparison.Ordinal))
            {
                result.Errors.Add("A description edit no longer matches its original timecard. Review the activity again.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(edit.Description) || edit.Description.Length > 4000 || edit.Description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            {
                result.Errors.Add("Activity descriptions must contain 1 to 4,000 characters and no unsupported control characters.");
                continue;
            }
            var (row, index) = matches[0];
            var description = edit.Description.Trim();
            if (description == row.Description) continue;
            result.Rows[index] = row with { Description = description };
            result.Warnings.Add($"{row.Date:yyyy-MM-dd}: activity description changed in review. Confirm the updated text before submitting.");
        }
        return result;
    }
}
