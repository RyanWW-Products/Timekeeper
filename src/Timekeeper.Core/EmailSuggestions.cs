using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Timekeeper.Core;

public sealed record EmailMappingSuggestion(int? AssignmentId, bool Internal, int? TaskId, string Reason);

/// <summary>Proposes editable mappings from the loaded reference data. Never links existing timecards.</summary>
public static class EmailSuggestions
{
    private static readonly HashSet<string> IgnoredWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "at", "by", "for", "from", "in", "of", "on", "or", "the", "to", "with",
        "v", "vs", "versus", "case", "matter", "project", "work", "worked", "client", "company",
        "inc", "incorporated", "llc", "ltd", "corp", "corporation", "unknown", "unspecified", "unassigned", "tbd"
    };
    private static readonly HashSet<string> InternalLabels = new(StringComparer.Ordinal)
    {
        "internal", "internal work", "general internal", "internal admin", "internal administration",
        "administrative", "administration", "admin", "misc internal", "non client work"
    };

    public static EmailMappingSuggestion Suggest(EmailActivity activity, ReadSession session)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(session);
        var reference = session.Reference;
        var matter = Normalize(activity.MatterHint);
        var internalWork = reference.InternalProject is { Id: > 0 } project
            && (Exact(matter, Normalize(project.Name)) || InternalLabels.Contains(matter));
        int? assignmentId = null;
        string assignmentReason;
        if (internalWork)
        {
            assignmentReason = "Internal work matched your configured internal project.";
        }
        else
        {
            var candidates = ValidAssignments(reference).Select(a => RankAssignment(activity, a, session.Settings.Email))
                .Where(a => a.Score > 0).OrderByDescending(a => a.Score).ThenBy(a => a.Assignment.Id).ToList();
            if (candidates.Count == 0)
                assignmentReason = "No matching assignment found. Choose an assignment or use Find assignment.";
            else
            {
                var best = candidates[0]; assignmentId = best.Assignment.Id;
                var ambiguous = candidates.Skip(1).Any(candidate => best.Score - candidate.Score <= 40);
                assignmentReason = ambiguous
                    ? "Suggested assignment: several matches are similar. Review this choice."
                    : best.ExactMatch ? "Exact project and assignment match." : "Suggested assignment from the project and activity hints.";
                if (Inactive(best.Assignment)) assignmentReason += " This assignment is closed or inactive.";
            }
        }
        var task = PickTask(activity, reference, assignmentId, internalWork, session.Settings.InternalTaskId);
        return new(assignmentId, internalWork, task.Id, assignmentReason + " " + task.Reason);
    }

    /// <summary>Recomputes a proposed task after a user changes the assignment.</summary>
    public static int? SuggestTask(EmailActivity activity, ReferenceData reference, int? assignmentId, bool internalWork, int internalTaskId) =>
        PickTask(activity, reference, assignmentId, internalWork, internalTaskId).Id;

    private sealed record AssignmentScore(AssignmentRecord Assignment, int Score, bool ExactMatch);
    private static AssignmentScore RankAssignment(EmailActivity activity, AssignmentRecord assignment, string email)
    {
        var matter = Normalize(activity.MatterHint);
        var project = Normalize(assignment.ProjectName);
        var hint = Normalize(activity.AssignmentHint);
        var name = Normalize(assignment.Name);
        var matterScore = Match(matter, project, strict: true);
        // A generic assignment such as "Presentation design" cannot override a conflicting case name.
        if (Tokens(matter).Count > 0 && matterScore == 0) return new(assignment, 0, false);
        var assignmentScore = Match(hint, name);
        var description = Normalize(activity.Description);
        var descriptionScore = Math.Max(DescriptionMatch(description, project), DescriptionMatch(description, name));
        if (matterScore == 0 && assignmentScore == 0 && descriptionScore == 0) return new(assignment, 0, false);
        var score = matterScore * 10 + assignmentScore * 6 + descriptionScore / 4;
        if (Inactive(assignment)) score -= 100;
        if (assignment.Employees?.Any(e => string.Equals(e?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)) == true) score += 10;
        // Even a plausible inactive-only match stays available for explicit review.
        return new(assignment, Math.Max(1, score), Exact(matter, project) && Exact(hint, name));
    }

    private static (int? Id, string Reason) PickTask(EmailActivity activity, ReferenceData reference, int? assignmentId, bool internalWork, int internalTaskId)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(reference);
        var categoryIds = reference.Categories.Where(c => c is not null && c.Id > 0).Select(c => c.Id).ToHashSet();
        var tasks = reference.Tasks.Where(t => t is not null && t.Id > 0 && categoryIds.Contains(t.CategoryId) && !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Id).Where(group => group.Count() == 1).Select(group => group.Single()).ToList();
        var assignment = internalWork ? null : ValidAssignments(reference).SingleOrDefault(a => a.Id == assignmentId);
        var hint = Normalize(activity.TaskHint);
        var exact = tasks.Where(t => Exact(hint, Normalize(t.Name)))
            .OrderByDescending(t => t.CategoryId == assignment?.CategoryId).ThenBy(t => t.Id).ToList();
        if (exact.Count > 0)
        {
            var preferredCategory = exact[0].CategoryId == assignment?.CategoryId;
            var ambiguous = exact.Skip(1).Any(t => (t.CategoryId == assignment?.CategoryId) == preferredCategory);
            return (exact[0].Id, ambiguous ? "Suggested task: multiple tasks share that name. Review this choice." : "Task name matches Quickbase.");
        }
        if (assignment?.TaskId is int linkedId)
        {
            var linked = tasks.SingleOrDefault(t => t.Id == linkedId && (!assignment.CategoryId.HasValue || t.CategoryId == assignment.CategoryId));
            if (linked is not null) return (linked.Id, "Task comes from the selected Quickbase assignment.");
        }
        if (internalWork && tasks.Any(t => t.Id == internalTaskId))
            return (internalTaskId, "Using your configured internal task.");
        var description = Normalize(activity.Description);
        var ranked = tasks.Select(t => new { Task = t, Hint = Match(hint, Normalize(t.Name)), Description = DescriptionMatch(description, Normalize(t.Name), allowSingleWord: true) })
            .Where(t => t.Hint > 0 || t.Description > 0)
            .Select(t => new { t.Task, Score = t.Hint * 5 + t.Description / 3 + (t.Task.CategoryId == assignment?.CategoryId ? 10 : 0) })
            .OrderByDescending(t => t.Score).ThenBy(t => t.Task.Id).ToList();
        if (ranked.Count == 0) return (null, "Choose a task; no reliable task match was found.");
        var close = ranked.Skip(1).Any(t => ranked[0].Score - t.Score <= 20);
        return (ranked[0].Task.Id, close ? "Suggested task: several matches are similar. Review this choice." : "Suggested task from the work description or task hint.");
    }

    private static IEnumerable<AssignmentRecord> ValidAssignments(ReferenceData reference) => reference.Assignments
        .Where(a => a is not null && a.Id > 0 && a.ProjectId > 0 && !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.ProjectName))
        .GroupBy(a => a.Id).Where(group => group.Count() == 1).Select(group => group.Single());

    private static int DescriptionMatch(string description, string candidate, bool allowSingleWord = false)
    {
        var tokens = Tokens(candidate);
        if (tokens.Count < (allowSingleWord ? 1 : 2) || tokens.All(t => t is "other" or "general" or "misc")) return 0;
        return Match(description, candidate, strict: true);
    }

    private static int Match(string query, string candidate, bool strict = false)
    {
        if (query.Length == 0 || candidate.Length == 0) return 0;
        if (query == candidate) return 100;
        var queryTokens = Tokens(query); var candidateTokens = Tokens(candidate);
        if (queryTokens.Count == 0 || candidateTokens.Count == 0) return 0;
        var common = queryTokens.Intersect(candidateTokens, StringComparer.Ordinal).Count();
        if (common == 0) return 0;
        // Whole phrases allow abbreviated case names while avoiding substring matches such as Lee / Fleet.
        if ((" " + candidate + " ").Contains(" " + query + " ", StringComparison.Ordinal)
            || (" " + query + " ").Contains(" " + candidate + " ", StringComparison.Ordinal)) return 85;
        var queryCoverage = (double)common / queryTokens.Count;
        var candidateCoverage = (double)common / candidateTokens.Count;
        if (strict && queryCoverage < .75 && candidateCoverage < 1) return 0;
        if (!strict && queryCoverage < .5 && candidateCoverage < 1) return 0;
        return 50 + (int)(30 * queryCoverage + 15 * candidateCoverage);
    }

    private static bool Exact(string left, string right) => left.Length > 0 && left == right;
    private static bool Inactive(AssignmentRecord assignment) => Regex.IsMatch(assignment.Status ?? "", @"\b(closed|complete|completed|cancelled|canceled|inactive|archived)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static HashSet<string> Tokens(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token.Length > 1 && !IgnoredWords.Contains(token)).Select(Stem).ToHashSet(StringComparer.Ordinal);
    private static string Stem(string token) => token.Length > 4 && token.EndsWith("s", StringComparison.Ordinal) && !token.EndsWith("ss", StringComparison.Ordinal) ? token[..^1] : token;
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var letters = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(letters.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    }
}
