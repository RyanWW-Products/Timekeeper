using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Timekeeper.Core;

/// <summary>API transport. Authentication is never placed in URLs, returned errors, or saved files.</summary>
public sealed class TimecardApi : IDisposable
{
    private readonly AppSettings settings;
    private readonly Credentials credentials;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim transportGate = new(1, 1);
    private DateTimeOffset lastQuickbaseRequest, lastTogglRequest;
    private const string QbBase = "https://api.quickbase.com/v1/";
    private const string TogglBase = "https://api.track.toggl.com/api/v9/";
    private static readonly int[] AssignmentFields = [3, 8, 11, 13, 15, 16, 23, 73, 75, 77];
    private const int PageSize = 1000;
    private const int RecordCap = 20000;
    public AppSettings Settings => settings;

    public TimecardApi(AppSettings settings, Credentials credentials, HttpClient? http = null)
        : this(settings, credentials, http, allowMissingEmployeeId: false)
    {
    }

    private TimecardApi(AppSettings settings, Credentials credentials, HttpClient? http, bool allowMissingEmployeeId)
    {
        ValidateSettings(settings, allowMissingEmployeeId);
        if (string.IsNullOrWhiteSpace(credentials.TogglToken) || string.IsNullOrWhiteSpace(credentials.QuickbaseToken)
            || credentials.TogglToken.Contains('\r') || credentials.TogglToken.Contains('\n')
            || credentials.QuickbaseToken.Contains('\r') || credentials.QuickbaseToken.Contains('\n'))
            throw new ArgumentException("Both API tokens are required and must be single-line values.");
        this.settings = settings;
        this.credentials = credentials;
        ownsHttp = http is null;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(65) };
    }
    public static void ValidateSettings(AppSettings value) => ValidateSettings(value, allowMissingEmployeeId: false);

    private static void ValidateSettings(AppSettings value, bool allowMissingEmployeeId)
    {
        if (!Regex.IsMatch(value.Realm ?? "", "\\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\.quickbase\\.com\\z"))
            throw new ArgumentException("Enter the Quickbase realm hostname, for example company.quickbase.com.");
        if (!Regex.IsMatch(value.Email ?? "", "\\A[A-Za-z0-9.!#$%&*+/=?^_`|~-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}\\z")
            || (!allowMissingEmployeeId && !Regex.IsMatch(value.EmployeeId ?? "", "\\A[A-Za-z0-9.]{1,80}\\z")))
            throw new ArgumentException("Enter a valid work email and Quickbase employee user ID.");
        foreach (var table in new[] { value.TimecardsTable, value.TasksTable, value.ProjectsTable, value.AssignmentsTable, value.CategoriesTable })
            if (!Regex.IsMatch(table ?? "", "\\A[a-zA-Z0-9]{6,30}\\z")) throw new ArgumentException("A Quickbase table ID is invalid.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(value.TimeZoneId);
        if (value.InternalProjectId < 0 || value.TargetHours < 0 || value.TargetHours > 24 || value.TimecardsHours < 0 || value.TimecardsHours > 24)
            throw new ArgumentException("Time policy values must be between zero and 24 hours, and project ID cannot be negative.");
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        using var me = await TogglGetAsync("me", ct);
        var email = String(me.RootElement, "email");
        if (string.IsNullOrWhiteSpace(email) || !System.Net.Mail.MailAddress.TryCreate(email, out _))
            throw new InvalidOperationException("Toggl did not return a valid account email. Check the Toggl API token.");
        await VerifyQuickbaseIdentityAsync(ct);
        return $"Verified Toggl account: {email}. Quickbase account: {settings.Email}. Confirm both accounts are yours.";
    }

    /// <summary>Resolve only the token's current user during setup. No supplied employee ID is trusted or changed.</summary>
    public static async Task<QuickbaseIdentity> DiscoverQuickbaseIdentityAsync(AppSettings settings, Credentials credentials, HttpClient? http = null, CancellationToken ct = default)
    {
        using var api = new TimecardApi(settings with { EmployeeId = "" }, credentials, http, allowMissingEmployeeId: true);
        return await api.ReadQuickbaseIdentityAsync(ct);
    }

    private async Task VerifyQuickbaseIdentityAsync(CancellationToken ct)
    {
        var identity = await ReadQuickbaseIdentityAsync(ct);
        if (identity.EmployeeId != settings.EmployeeId)
            throw new InvalidOperationException("The Quickbase token, employee user ID, and work email do not identify the same person. Correct settings before continuing.");
    }

    private async Task<QuickbaseIdentity> ReadQuickbaseIdentityAsync(CancellationToken ct)
    {
        // User tokens do not support the XML API_GetUserInfo /db/main endpoint.
        // The REST formula API evaluates User() as the authenticated caller in this table.
        // Keep this formula fixed: a caller-supplied email lookup would not prove token ownership.
        const string formula = "UserToID(User()) & \";\" & UserToEmail(User()) & \";\" & UserToName(User())";
        using var request = QuickbaseRequest("formula/run", new { from = settings.TimecardsTable, formula });
        using var response = await SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new HttpRequestException($"Quickbase could not verify your account (HTTP {(int)response.StatusCode}). Check the realm and user token. In Quickbase, confirm the token is active, assign it to the app containing your timecards, and save that token page before testing again. Also check the Timecards table ID in Advanced settings.");
        EnsureSuccess(response, "Quickbase identity verification");
        using var document = ParseJson(await ReadLimitedAsync(response, ct), "Quickbase identity verification");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count(p => p.NameEquals("result")) != 1
            || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String || result.GetString()!.Length > 2048)
            throw new InvalidOperationException("Quickbase did not return a usable signed-in user identity. Check the Quickbase API token.");
        var parts = result.GetString()!.Split(';', 3);
        if (parts.Length != 3)
            throw new InvalidOperationException("Quickbase did not return a usable signed-in user identity. Check the Quickbase API token.");
        var id = parts[0];
        var email = parts[1].Trim();
        if (!Regex.IsMatch(id, "\\A[0-9]+\\.[A-Za-z0-9]{1,64}\\z") || id == "1.ckbs"
            || !System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)
            throw new InvalidOperationException("Quickbase did not return a usable signed-in user identity. Check the Quickbase API token.");
        if (!string.Equals(email, settings.Email, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Quickbase token belongs to a different work email. Correct the email or token before continuing.");
        var displayName = parts[2].Trim();
        if (string.IsNullOrEmpty(displayName) || displayName.Length > 256 || displayName.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
            || displayName.Contains(credentials.QuickbaseToken, StringComparison.Ordinal) || displayName.Contains(credentials.TogglToken, StringComparison.Ordinal)) displayName = email;
        return new QuickbaseIdentity(id, email, displayName);
    }

    public async Task<ReadSession> ReadAsync(DateOnly start, DateOnly end, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (end < start || end.DayNumber - start.DayNumber >= 31) throw new ArgumentException("Choose a range of one to 31 days.");
        progress?.Report("Verifying your Toggl and Quickbase identity…");
        await TestAsync(ct);
        progress?.Report("Reading Quickbase tasks, categories, and assignments…");
        var reference = await ReadReferenceAsync(ct);
        progress?.Report("Reading Toggl entries…");
        var allEntries = await ReadEntriesRangeAsync(start, end, ct);
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var days = new List<DaySnapshot>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            progress?.Report($"Reading {date:yyyy-MM-dd}…");
            var entries = allEntries.Where(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.Start, timezone).DateTime) == date).ToList();
            var existing = await ReadExistingAsync(date, ct);
            days.Add(new DaySnapshot { Date = date, Entries = entries, Existing = existing });
        }
        return new ReadSession { Settings = settings, Reference = reference, Days = days };
    }

    internal Task<List<TimeEntry>> ReadEntriesAsync(DateOnly date, CancellationToken ct) => ReadEntriesRangeAsync(date, date, ct);

    internal async Task<List<TimeEntry>> ReadEntriesRangeAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var first = DayBoundary(start, timezone);
        var queryStart = DayBoundary(start.AddDays(-1), timezone);
        var last = DayBoundary(end.AddDays(1), timezone);
        // Start-time filtering alone misses a timer spanning midnight. Inspect a lookback day and the
        // running timer independently, so even a timer begun several days earlier cannot disappear.
        using (var current = await TogglGetAsync("me/time_entries/current", ct))
        {
            if (current.RootElement.ValueKind != JsonValueKind.Null)
            {
                if (current.RootElement.ValueKind != JsonValueKind.Object || !DateTimeOffset.TryParse(String(current.RootElement, "start"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var runningStart))
                    throw new InvalidDataException("Toggl returned an invalid running-timer response.");
                if (runningStart < last)
                    throw new InvalidOperationException("A running Toggl timer overlaps this date range. Stop it and split any overnight work by date, then read again.");
            }
        }
        var route = $"me/time_entries?meta=true&start_date={Uri.EscapeDataString(queryStart.ToString("o", CultureInfo.InvariantCulture))}&end_date={Uri.EscapeDataString(last.ToString("o", CultureInfo.InvariantCulture))}";
        using var document = await TogglGetAsync(route, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Toggl returned an unexpected time-entry response.");
        if (root.GetArrayLength() >= 1000) throw new InvalidDataException("Toggl returned 1,000 or more entries. The API may have truncated the result; choose a shorter date range. No session was created.");
        var rows = new List<TimeEntry>();
        foreach (var item in root.EnumerateArray())
        {
            var started = DateTimeOffset.Parse(String(item, "start"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(started, timezone).DateTime);
            var duration = Long(item, "duration");
            var stopped = String(item, "stop");
            DateTimeOffset? stop = string.IsNullOrEmpty(stopped) ? null : DateTimeOffset.Parse(stopped, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (localDay < start && (duration < 0 || (stop ?? started.AddSeconds(Math.Max(0, duration))) > first))
                throw new InvalidOperationException("A Toggl entry starts before this date range and overlaps it. Split overnight work by date in Toggl, then read again.");
            if (localDay < start || localDay > end) continue;
            rows.Add(new TimeEntry(Long(item, "id"), Long(item, "workspace_id"), started,
                stop,
                duration, String(item, "description"), String(item, "project_name"), duration < 0,
                item.TryGetProperty("billable", out var billable) && billable.ValueKind == JsonValueKind.True));
        }
        if (rows.Select(e => e.Id).Distinct().Count() != rows.Count) throw new InvalidDataException("Toggl returned duplicate entry IDs.");
        return rows.OrderBy(e => e.Start).ThenBy(e => e.Id).ToList();
    }

    private static DateTimeOffset DayBoundary(DateOnly date, TimeZoneInfo timezone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (timezone.IsInvalidTime(local) || timezone.IsAmbiguousTime(local))
            throw new InvalidOperationException("The selected time zone has an ambiguous midnight on this date. Choose another date or contact support.");
        return new DateTimeOffset(local, timezone.GetUtcOffset(local));
    }

    public async Task<List<ExistingTimecard>> ReadExistingAsync(DateOnly date, CancellationToken ct = default)
    {
        // Use relation IDs, not display-name lookup fields 12/18/23 from the legacy script.
        var raw = await QueryAllAsync(settings.TimecardsTable, [3, 7, 10, 11, 16, 19, 22, 24, 73], $"{{7.EX.'{date:yyyy-MM-dd}'}}AND{{24.TV.'{settings.Email}'}}", ct);
        return raw.Select(r =>
        {
            if (!DateOnly.TryParse(CellString(r, 7), CultureInfo.InvariantCulture, DateTimeStyles.None, out var recordDate) || recordDate != date)
                throw FieldError(r, settings.TimecardsTable, 7, "Date", $"the selected date {date:yyyy-MM-dd}");
            var employee = Cell(r, 24);
            if (String(employee, "id") != settings.EmployeeId)
                throw FieldError(r, settings.TimecardsTable, 24, "Employee", "the verified Quickbase user; check the employee field mapping");
            return new ExistingTimecard(RecordId(r, settings.TimecardsTable, 3, "Record ID"), recordDate, Hours(r), RecordId(r, settings.TimecardsTable, 11, "Project"), RecordId(r, settings.TimecardsTable, 16, "Task"), RecordId(r, settings.TimecardsTable, 22, "Category"), OptionalRecordId(r, settings.TimecardsTable, 73, "Assignment"), CellString(r, 19));
        }).OrderBy(r => r.RecordId).ToList();
    }

    internal async Task<HashSet<int>> ReadTimecardRecordIdsAsync(IEnumerable<int> recordIds, CancellationToken ct)
    {
        var ids = recordIds.Distinct().ToArray();
        if (ids.Any(id => id <= 0)) throw new InvalidDataException("A saved Quickbase record ID is invalid. Restore the receipt before recovery.");
        var found = new HashSet<int>();
        foreach (var batch in ids.Chunk(100))
        {
            // Do not filter by date or employee: an edited/moved record is still an existing write.
            var where = string.Join("OR", batch.Select(id => $"{{3.EX.'{id}'}}"));
            var rows = await QueryAllAsync(settings.TimecardsTable, [3], where, ct);
            foreach (var row in rows)
            {
                var id = RecordId(row, settings.TimecardsTable, 3, "Record ID");
                if (!batch.Contains(id)) throw new InvalidDataException("Quickbase returned a record outside the deletion check. No entries were released for rewriting.");
                found.Add(id);
            }
        }
        return found;
    }

    public async Task<ReferenceData> ReadReferenceAsync(CancellationToken ct = default)
    {
        var categories = (await QueryAllAsync(settings.CategoriesTable, [3, 6], null, ct)).Select(r => new CategoryRecord(RecordId(r, settings.CategoriesTable, 3, "Record ID"), CellString(r, 6))).ToList();
        var tasks = (await QueryAllAsync(settings.TasksTable, [3, 7, 11, 17], null, ct))
            .Where(r => Truth(Cell(r, 11))).Select(r => new TaskRecord(RecordId(r, settings.TasksTable, 3, "Record ID"), CellString(r, 7), RecordId(r, settings.TasksTable, 17, "Category"))).ToList();
        var assignmentRows = await QueryAllAsync(settings.AssignmentsTable, AssignmentFields,
            "{11.XEX.'Completed'}AND{11.XEX.'Cancelled'}AND{11.XEX.'Canceled'}", ct);
        // Only personal, open assignments belong in this read. A missing relation on
        // somebody else's assignment must not prevent this user's connection test.
        var assignments = assignmentRows.Where(r => AssignmentEmployees(r).Contains(settings.Email, StringComparer.OrdinalIgnoreCase) && !IsClosed(CellString(r, 11)))
            .Select(ParseAssignment).ToList();
        var projectWhere = settings.InternalProjectId > 0 ? $"{{3.EX.'{settings.InternalProjectId}'}}" : $"{{6.CT.'{EscapeQuery(settings.InternalProjectSearch)}'}}";
        var projects = (await QueryAllAsync(settings.ProjectsTable, [3, 6], projectWhere, ct))
            .Select(r => new ProjectRecord(RecordId(r, settings.ProjectsTable, 3, "Record ID"), CellString(r, 6))).ToList();
        // Multiple annual project matches must be selected explicitly in settings, never guessed.
        var internalProject = projects.Count == 1 ? projects[0] : null;
        return new ReferenceData { Tasks = tasks.OrderBy(t => t.Id).ToList(), Categories = categories.OrderBy(c => c.Id).ToList(), Assignments = assignments.OrderBy(a => a.Id).ToList(), InternalProject = internalProject };
    }

    public async Task<List<AssignmentRecord>> FindAssignmentsAsync(string query, CancellationToken ct = default)
    {
        if (query.Length > 150) throw new ArgumentException("Use a search of 150 characters or fewer.");
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 12) throw new ArgumentException("Enter one to twelve search words.");
        var where = string.Join("AND", words.Select(word => $"({{13.CT.'{EscapeQuery(word)}'}}OR{{16.CT.'{EscapeQuery(word)}'}}OR{{23.CT.'{EscapeQuery(word)}'}})"));
        return (await QueryAllAsync(settings.AssignmentsTable, AssignmentFields, where, ct)).Select(ParseAssignment).ToList();
    }

    internal async Task<AssignmentRecord?> ReadAssignmentAsync(int id, CancellationToken ct)
    {
        var rows = await QueryAllAsync(settings.AssignmentsTable, AssignmentFields, $"{{3.EX.'{id}'}}", ct);
        return rows.Count == 1 ? ParseAssignment(rows[0]) : null;
    }
    internal async Task<ProjectRecord?> ReadProjectAsync(int id, CancellationToken ct)
    {
        var rows = await QueryAllAsync(settings.ProjectsTable, [3, 6], $"{{3.EX.'{id}'}}", ct);
        return rows.Count == 1 ? new ProjectRecord(RecordId(rows[0], settings.ProjectsTable, 3, "Record ID"), CellString(rows[0], 6)) : null;
    }
    internal static bool IsClosed(string status) => new[] { "closed", "complete", "completed", "cancelled", "canceled", "inactive", "archived" }.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase);
    private AssignmentRecord ParseAssignment(JsonElement row) => new(RecordId(row, settings.AssignmentsTable, 3, "Record ID"), CellString(row, 13), RecordId(row, settings.AssignmentsTable, 15, "Project"), CellString(row, 16), CellString(row, 23), CellString(row, 11), StripHtml(CellString(row, 8)))
    {
        Employees = AssignmentEmployees(row),
        TaskId = OptionalRecordId(row, settings.AssignmentsTable, 77, "Task"), CategoryId = OptionalRecordId(row, settings.AssignmentsTable, 75, "Category")
    };
    private static List<string> AssignmentEmployees(JsonElement row) => Cell(row, 73).ValueKind == JsonValueKind.Array
        ? Cell(row, 73).EnumerateArray().Select(u => String(u, "email")).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList() : [];

    internal async Task<List<JsonElement>> QueryAllAsync(string table, int[] fields, string? where, CancellationToken ct)
    {
        var output = new List<JsonElement>();
        var seenIds = new HashSet<int>();
        int? declaredTotal = null;
        while (true)
        {
            var body = new Dictionary<string, object> { ["from"] = table, ["select"] = fields, ["options"] = new { top = PageSize, skip = output.Count }, ["sortBy"] = new[] { new { fieldId = 3, order = "ASC" } } };
            if (where is not null) body["where"] = where;
            using var request = QuickbaseRequest("records/query", body);
            using var response = await SendAsync(request, ct);
            EnsureSuccess(response, "Quickbase read");
            using var document = ParseJson(await ReadLimitedAsync(response, ct), "Quickbase");
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Quickbase returned no record array.");
            var count = data.GetArrayLength();
            if (root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("totalRecords", out var total) && total.TryGetInt32(out var totalRecords))
            {
                if (declaredTotal.HasValue && totalRecords != declaredTotal.Value) throw new InvalidDataException("Quickbase records changed during pagination. Read the day again.");
                if (totalRecords < 0 || totalRecords > RecordCap) throw new InvalidDataException("Quickbase exceeds the 20,000-record read limit. Narrow the scope before proceeding.");
                declaredTotal = totalRecords;
            }
            foreach (var row in data.EnumerateArray())
            {
                var id = RecordId(row, table, 3, "Record ID", resultRow: output.Count + 1);
                if (!seenIds.Add(id)) throw FieldError(row, table, 3, "Record ID", "a unique record ID across all pages", output.Count + 1);
                output.Add(row.Clone());
            }
            if (output.Count > RecordCap) throw new InvalidDataException("Quickbase exceeded the read safety limit; partial results were discarded.");
            if (declaredTotal.HasValue)
            {
                if (output.Count == declaredTotal.Value) return output;
                if (count == 0 || output.Count > declaredTotal.Value) throw new InvalidDataException("Quickbase pagination was inconsistent; partial results were discarded.");
            }
            else if (count < PageSize) return output;
            if (output.Count >= RecordCap) throw new InvalidDataException("Quickbase reached the read safety limit without proving completeness.");
        }
    }

    internal async Task<RowOutcome> CreateRowAsync(VerifiedRow row, CancellationToken ct)
    {
        var data = new Dictionary<string, object>
        {
            ["7"] = new { value = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, ["10"] = new { value = row.Hours },
            ["11"] = new { value = row.Project }, ["16"] = new { value = row.Task }, ["19"] = new { value = row.Description },
            ["22"] = new { value = row.Category }, ["24"] = new { value = new { id = settings.EmployeeId } }
        };
        if (row.Assignment.HasValue) data["73"] = new { value = row.Assignment.Value };
        using var request = QuickbaseRequest("records", new { to = settings.TimecardsTable, data = new[] { data }, fieldsToReturn = new[] { 3 } });
        try
        {
            using var response = await SendAsync(request, ct);
            // Only explicit client rejection is known not to have committed; server/transport errors are unknown.
            if (!response.IsSuccessStatusCode)
            {
                var knownFailure = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests;
                return new RowOutcome { Row = row, Status = knownFailure ? "failed" : "unknown", Message = $"Quickbase returned HTTP {(int)response.StatusCode}. {(knownFailure ? "The row was rejected." : "Reconcile before doing any further writes.")}" };
            }
            using var document = ParseJson(await ReadLimitedAsync(response, ct), "Quickbase");
            if (!document.RootElement.TryGetProperty("metadata", out var meta)) return Unknown(row);
            var created = meta.TryGetProperty("createdRecordIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().Select(value => TryRecordId(value, out int id) && id > 0 ? id : throw new InvalidDataException("Quickbase returned an invalid created record ID.")).ToArray() : [];
            var hasErrors = meta.TryGetProperty("lineErrors", out var errors) && errors.ValueKind == JsonValueKind.Object && errors.EnumerateObject().Any();
            var updated = meta.TryGetProperty("updatedRecordIds", out var updates) && updates.ValueKind == JsonValueKind.Array && updates.GetArrayLength() > 0;
            if (created.Length == 1 && created[0] > 0 && !hasErrors && !updated) return new RowOutcome { Row = row, Status = "created", RecordId = created[0], Message = "Created in Quickbase." };
            var thisRowRejected = hasErrors && errors.EnumerateObject().Count() == 1 && errors.TryGetProperty("1", out var rowError)
                && rowError.ValueKind == JsonValueKind.Array && rowError.GetArrayLength() > 0;
            if (created.Length == 0 && thisRowRejected && !updated) return new RowOutcome { Row = row, Status = "failed", Message = "Quickbase rejected this row with a line error. Review the selected fields and permissions." };
            return Unknown(row);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or FormatException)
        { return Unknown(row); }
    }
    private static RowOutcome Unknown(VerifiedRow row) => new() { Row = row, Status = "unknown", Message = "The write outcome could not be confirmed. Do not resubmit; reconcile this receipt first." };

    private HttpRequestMessage QuickbaseRequest(string route, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, QbBase + route);
        request.Headers.Add("QB-Realm-Hostname", settings.Realm);
        request.Headers.Authorization = new AuthenticationHeaderValue("QB-USER-TOKEN", credentials.QuickbaseToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }
    private async Task<JsonDocument> TogglGetAsync(string route, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TogglBase + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.TogglToken + ":api_token")));
        using var response = await SendAsync(request, ct);
        EnsureSuccess(response, "Toggl read");
        return ParseJson(await ReadLimitedAsync(response, ct), "Toggl");
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await transportGate.WaitAsync(timeout.Token);
        try
        {
            // Production transports stay below published burst limits. Injected test transports run without delays.
            if (ownsHttp)
            {
                var isToggl = request.RequestUri!.Host == "api.track.toggl.com";
                var spacing = TimeSpan.FromMilliseconds(isToggl ? 1050 : 250);
                var last = isToggl ? lastTogglRequest : lastQuickbaseRequest;
                var wait = spacing - (DateTimeOffset.UtcNow - last);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, timeout.Token);
                if (isToggl) lastTogglRequest = DateTimeOffset.UtcNow; else lastQuickbaseRequest = DateTimeOffset.UtcNow;
            }
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            try { await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, timeout.Token); return response; }
            catch { response.Dispose(); throw; }
        }
        catch (HttpRequestException) { throw new HttpRequestException("The service could not be reached. Check your connection and try again."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException("The service did not respond within 60 seconds."); }
        finally { transportGate.Release(); }
    }
    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"{operation} reached the service rate limit (HTTP 429). Wait before trying again; no automatic retry was attempted.");
        if (response.StatusCode == HttpStatusCode.PaymentRequired && operation.StartsWith("Toggl", StringComparison.Ordinal))
            throw new HttpRequestException("Toggl's API quota is exhausted (HTTP 402). User-specific reads have an hourly quota; wait for it to reset before trying again.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{operation} failed (HTTP {(int)response.StatusCode}). Check credentials, permissions, or service availability.");
    }
    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }
    private static JsonDocument ParseJson(string value, string service)
    {
        try { return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 40 }); }
        catch (JsonException) { throw new InvalidDataException($"{service} returned an invalid JSON response."); }
    }
    private static string EscapeQuery(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => c is '\'' or '{' or '}' or '\\' || char.IsControl(c)))
            throw new ArgumentException("Search text cannot contain quotes, braces, backslashes, or control characters.");
        return value;
    }
    private static JsonElement Cell(JsonElement row, int id) => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(id.ToString(CultureInfo.InvariantCulture), out var cell) && cell.ValueKind == JsonValueKind.Object && cell.TryGetProperty("value", out var value) ? value : default;
    private static string CellString(JsonElement row, int id) => ValueString(Cell(row, id));
    private static string String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? ValueString(property) : "";
    private static string ValueString(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    private static bool TryRecordId(JsonElement value, out int result)
    {
        result = 0;
        if (value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) return false;
        var text = ValueString(value).Trim();
        // Numeric relationship fields may be serialized as 44.0. Accept only
        // exact whole values; never round a fraction into a different record ID.
        if (!Regex.IsMatch(text, @"\A[+]?[0-9]+(?:\.0+)?\z")) return false;
        int point = text.IndexOf('.');
        return int.TryParse(point < 0 ? text : text[..point], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
    }
    private int RecordId(JsonElement row, string table, int field, string fieldName, int? resultRow = null)
    {
        if (TryRecordId(Cell(row, field), out int result) && result > 0) return result;
        throw FieldError(row, table, field, fieldName, "a positive whole-number record ID", resultRow);
    }
    private int? OptionalRecordId(JsonElement row, string table, int field, string fieldName)
    {
        var value = Cell(row, field);
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) return null;
        if (TryRecordId(value, out int result) && result >= 0) return result == 0 ? null : result;
        throw FieldError(row, table, field, fieldName, "a whole-number record ID, or blank/zero for an optional link");
    }
    private decimal Hours(JsonElement row)
    {
        var value = Cell(row, 10);
        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.String && decimal.TryParse(ValueString(value), NumberStyles.Number, CultureInfo.InvariantCulture, out var result)) return result;
        throw FieldError(row, settings.TimecardsTable, 10, "Hours", "a numeric hours value");
    }
    private QuickbaseDataException FieldError(JsonElement row, string table, int field, string fieldName, string expected, int? resultRow = null)
    {
        var (tableName, nameField) = table == settings.TasksTable ? ("Tasks", 7) : table == settings.AssignmentsTable ? ("Assignments", 13)
            : table == settings.ProjectsTable ? ("Projects", 6) : table == settings.CategoriesTable ? ("Categories", 6) : ("Timecards", 19);
        bool hasId = TryRecordId(Cell(row, 3), out int recordId) && recordId > 0;
        string record = hasId ? $"#{recordId}" : $"unavailable (result row {resultRow?.ToString(CultureInfo.InvariantCulture) ?? "unknown"})";
        string name = DiagnosticText(CellString(row, nameField));
        if (name.Length > 0) record += $" — {name}";
        var value = Cell(row, field);
        string received = value.ValueKind switch
        {
            JsonValueKind.Undefined => "missing field or value",
            JsonValueKind.Null => "null (blank)",
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? "empty text (blank)" : $"text \"{DiagnosticText(value.GetString()!)}\"",
            JsonValueKind.Number => $"number {DiagnosticText(value.GetRawText())}",
            JsonValueKind.Array => "an array (not a single numeric value)",
            JsonValueKind.Object => "an object (not a numeric value)",
            _ => $"a {value.ValueKind.ToString().ToLowerInvariant()} value"
        };
        string url = $"https://{settings.Realm}/db/{table}?a=dr&rid={recordId}";
        return new QuickbaseDataException($"Quickbase could not read {fieldName}.\nTable: {tableName} ({table})\nRecord: {record}\nField: {fieldName} (field {field})\nReceived: {received}\nExpected: {expected}.\nCheck this record and the Quickbase table/field mapping.", hasId ? url : null);
    }
    private string DiagnosticText(string value)
    {
        // Show only the relevant field/name, never a whole record, response or credential.
        foreach (var token in new[] { credentials.TogglToken, credentials.QuickbaseToken })
            if (token.Length > 0) value = value.Replace(token, "[redacted]", StringComparison.Ordinal);
        value = new string(value.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c).ToArray());
        return value.Length > 100 ? value[..100] + "…" : value;
    }
    private static long Long(JsonElement value, string name) => long.TryParse(String(value, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new InvalidDataException("Toggl returned an invalid numeric value.");
    private static bool Truth(JsonElement value) => value.ValueKind == JsonValueKind.True || ValueString(value).Equals("true", StringComparison.OrdinalIgnoreCase) || ValueString(value) == "1";
    private static string StripHtml(string value) => WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")).Trim();
    public void Dispose() { if (ownsHttp) http.Dispose(); transportGate.Dispose(); }
}
