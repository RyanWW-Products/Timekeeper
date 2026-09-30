namespace Timekeeper.Core;

/// <summary>A date choice narrows review and submission without changing the source identity or age.</summary>
public sealed record ProposalScope(ReadSession Session, ProposalEnvelope Proposal)
{
    public static List<DateOnly> ProposedDates(ReadSession session, ProposalEnvelope proposal)
    {
        var linkedIds = proposal.AlreadyRecorded.Select(r => r.SourceEntryId).ToHashSet();
        return proposal.Rows.Select(r => r.Date)
            .Concat(session.Days.Where(d => d.Entries.Any(e => linkedIds.Contains(e.Id))).Select(d => d.Date))
            .Distinct().Order().ToList();
    }

    public static bool NeedsChoice(ReadSession session, ProposalEnvelope proposal)
    {
        if (session.SchemaVersion != proposal.SchemaVersion || session.SessionId != proposal.SessionId || session.Settings.EmployeeId != proposal.EmployeeId) return false;
        var dates = ProposedDates(session, proposal);
        return dates.Count > 0 && dates.Count < session.Days.Count && dates.All(date => session.Days.Any(d => d.Date == date));
    }

    public static ProposalScope Select(ReadSession session, ProposalEnvelope proposal, IReadOnlyCollection<DateOnly>? selectedDates = null)
    {
        if (selectedDates is null) return new(session, proposal);
        if (selectedDates.Count == 0 || selectedDates.Distinct().Count() != selectedDates.Count || selectedDates.Any(date => !session.Days.Any(d => d.Date == date)))
            throw new InvalidOperationException("Choose at least one date from the original read, with no repeated dates.");
        if (proposal.Rows.Any(row => !session.Days.Any(d => d.Date == row.Date)))
            throw new InvalidOperationException("The proposal contains a date outside the original read. Correct the proposal before choosing dates.");
        var excludedIds = session.Days.Where(d => !selectedDates.Contains(d.Date)).SelectMany(d => d.Entries).Select(e => e.Id).ToHashSet();
        // Unknown links stay in the proposal so ordinary validation rejects them. Never infer a match.
        return new(session with { Days = session.Days.Where(d => selectedDates.Contains(d.Date)).ToList() },
            proposal with { Rows = proposal.Rows.Where(r => selectedDates.Contains(r.Date)).ToList(),
                AlreadyRecorded = proposal.AlreadyRecorded.Where(r => !excludedIds.Contains(r.SourceEntryId)).ToList() });
    }
}
