using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Timekeeper.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 48
    };
}

public sealed record AppSettings
{
    public const string SharedCopilotUrl = "https://m365.cloud.microsoft/chat/?titleId=T_b313a74c-f0a1-7381-7c08-0d5992e75a3f&source=embedded-builder";
    public string Realm { get; init; } = "trialexhibits.quickbase.com";
    public string Email { get; init; } = "";
    public string EmployeeId { get; init; } = "";
    public string TimeZoneId { get; init; } = "Eastern Standard Time";
    public int InternalProjectId { get; init; }
    public int InternalTaskId { get; init; } = 44;
    public string InternalProjectSearch { get; init; } = "TEI Internal Tampa";
    public decimal TimecardsHours { get; init; } = 0.17m;
    public decimal TargetHours { get; init; } = 8m;
    public bool AddTimecards { get; init; } = true;
    public bool FillWeekdays { get; init; } = true;
    public string CopilotUrl { get; init; } = SharedCopilotUrl;
    public string TimecardsTable { get; init; } = "bd3bsxtbp";
    public string TasksTable { get; init; } = "bd3bsxtbn";
    public string ProjectsTable { get; init; } = "bd3bsxtbj";
    public string AssignmentsTable { get; init; } = "biqs87fvg";
    public string CategoriesTable { get; init; } = "beb9dvs6p";
    [JsonIgnore] public string ProfileKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Realm.ToLowerInvariant()}|{Email.ToLowerInvariant()}|{EmployeeId}|{TimecardsTable}")));
    public DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId)).DateTime);
}

public sealed record Credentials(string TogglToken, string QuickbaseToken);
public sealed record QuickbaseIdentity(string EmployeeId, string Email, string DisplayName);
public sealed record ProjectRecord(int Id, string Name);
public sealed record TaskRecord(int Id, string Name, int CategoryId);
public sealed record CategoryRecord(int Id, string Name);
public sealed record AssignmentRecord(int Id, string Name, int ProjectId, string ProjectName, string Client, string Status, string Notes)
{
    public List<string> Employees { get; init; } = [];
    public int? TaskId { get; init; }
    public int? CategoryId { get; init; }
}
public sealed record TimeEntry(long Id, long WorkspaceId, DateTimeOffset Start, DateTimeOffset? Stop, long DurationSeconds, string Description, string Project, bool Running, bool Billable);
public sealed record ExistingTimecard(int RecordId, DateOnly Date, decimal Hours, int Project, int Task, int Category, int? Assignment, string Description);

public sealed record ReferenceData
{
    public List<TaskRecord> Tasks { get; init; } = [];
    public List<CategoryRecord> Categories { get; init; } = [];
    public List<AssignmentRecord> Assignments { get; init; } = [];
    public ProjectRecord? InternalProject { get; init; }
}
public sealed record DaySnapshot
{
    public DateOnly Date { get; init; }
    public List<TimeEntry> Entries { get; init; } = [];
    public List<ExistingTimecard> Existing { get; init; } = [];
}
public sealed record ReadSession
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public AppSettings Settings { get; init; } = new();
    public ReferenceData Reference { get; init; } = new();
    public List<DaySnapshot> Days { get; init; } = [];
    public bool Demo { get; init; }
}

public sealed record ProposalEnvelope
{
    public int SchemaVersion { get; init; } = 1;
    public string SessionId { get; init; } = "";
    public string EmployeeId { get; init; } = "";
    public List<ProposalRow> Rows { get; init; } = [];
    public List<RecordedEntryLink> AlreadyRecorded { get; init; } = [];
}
public sealed record ProposalRow
{
    public DateOnly Date { get; init; }
    public List<long> SourceEntryIds { get; init; } = [];
    public int? Assignment { get; init; }
    public int Project { get; init; }
    public int Task { get; init; }
    public int Category { get; init; }
    public string Description { get; init; } = "";
    public decimal? Hours { get; init; }
}
public sealed record RecordedEntryLink
{
    public long SourceEntryId { get; init; }
    public int ExistingRecordId { get; init; }
}
public sealed record VerifiedRow
{
    public string RowId { get; init; } = Guid.NewGuid().ToString("N");
    public DateOnly Date { get; init; }
    public decimal Hours { get; init; }
    public int Project { get; init; }
    public string ProjectName { get; init; } = "";
    public int? Assignment { get; init; }
    public string AssignmentName { get; init; } = "";
    public int Task { get; init; }
    public string TaskName { get; init; } = "";
    public int Category { get; init; }
    public string CategoryName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "work";
    public List<long> SourceEntryIds { get; init; } = [];
}
public sealed record ValidationResult
{
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<VerifiedRow> Rows { get; init; } = [];
    [JsonIgnore] public bool IsValid => Errors.Count == 0;
}
public sealed record RowOutcome
{
    public VerifiedRow Row { get; init; } = new();
    public string Status { get; set; } = "pending";
    public int? RecordId { get; set; }
    public string Message { get; set; } = "";
    // Keep the original create and record ID as an audit trail when a user confirms deletion.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? DeletionConfirmedAtUtc { get; set; }
}
public sealed record SubmissionReceipt
{
    public string SubmissionId { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = "";
    public string ProfileKey { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Status { get; set; } = "pending";
    public string Message { get; set; } = "";
    public List<string> ReviewedWarnings { get; init; } = [];
    public DateTimeOffset? WarningsAcknowledgedAtUtc { get; init; }
    public List<RowOutcome> Rows { get; init; } = [];
}
