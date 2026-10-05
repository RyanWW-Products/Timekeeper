using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Timekeeper.Core;

static class SubmissionBillingEmailTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("submission billing permission loss fails preflight before creating a receipt", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadToggl();
            var rows = Rules.Validate(session, proposal).Rows.Select(r => r with { BillableOverride = false }).ToList();
            f.Transport.OverrideVisible = false;
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session, proposal, rows));
            Equal(0, f.Server.WriteCount); Equal(0, f.Store.LoadReceipts().Count);
        });
        await check("created billing mismatch stops remaining rows and blocks replay", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadToggl();
            var rows = Rules.Validate(session, proposal).Rows.Select(r => r with { BillableOverride = false }).ToList();
            f.Transport.ForceBillable = true;
            var receipt = await f.Service.SubmitAsync(session, proposal, rows);
            Equal("attention", receipt.Status); Equal(1, f.Server.WriteCount); Equal("created", receipt.Rows[0].Status);
            Equal<int?>(501, receipt.Rows[0].RecordId); True(receipt.Rows.Skip(1).All(r => r.Status == "not_sent"));
            var (fresh, retry) = await f.ReadToggl();
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(fresh, retry, Rules.Validate(fresh, retry).Rows, warningsAcknowledged: true)); Equal(1, f.Server.WriteCount);
        });
        await check("legacy baseline without billing can submit defaults after billing becomes readable", async () =>
        {
            using var f = new SubmissionEmailFixture();
            f.Server.Existing.Add(new(900, Fixture.Day, 1m, 100, 1, 10, null, "Existing work") { Billable = false });
            var (source, proposal) = await f.ReadToggl();
            var session = source with { Days = source.Days.Select(day => day with { Existing = day.Existing.Select(card => card with { Billable = null }).ToList() }).ToList() };
            var receipt = await f.Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows, warningsAcknowledged: true);
            Equal("complete", receipt.Status); Equal(2, f.Server.WriteCount);
        });
        await check("new billing baseline detects an external billing change before writing", async () =>
        {
            using var f = new SubmissionEmailFixture();
            f.Server.Existing.Add(new(900, Fixture.Day, 1m, 100, 1, 10, null, "Existing work") { Billable = false });
            var (session, proposal) = await f.ReadToggl(); f.Server.Existing[0] = f.Server.Existing[0] with { Billable = true };
            var error = await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows, warningsAcknowledged: true));
            True(error.Message.Contains("Billable")); Equal(0, f.Server.WriteCount);
        });
        await check("email submission creates confirmed time without a Toggl token or any Toggl requests", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadEmail();
            var receipt = await f.Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows, warningsAcknowledged: true);
            Equal("complete", receipt.Status); Equal(1, f.Server.WriteCount); Equal(0, f.Transport.TogglRequests);
            Equal(0.17m, receipt.Rows.Single().Row.Hours); Equal(1, receipt.Rows.Single().Row.EmailEvidenceKeys.Count);
        });
        await check("email history blocks regenerated activity IDs with the same immutable evidence", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadEmail();
            await f.Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows, warningsAcknowledged: true);
            var (fresh, replay) = await f.ReadEmail(activityId: "new-agent-generated-label");
            var error = await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(fresh, replay, Rules.Validate(fresh, replay).Rows, warningsAcknowledged: true));
            True(error.Message.Contains("already written")); Equal(1, f.Server.WriteCount); Equal(0, f.Transport.TogglRequests);
        });
        await check("email history blocks reused evidence after its activity date changes", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadEmail();
            await f.Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows, warningsAcknowledged: true);
            var (fresh, replay) = await f.ReadEmail(activityId: "new-date-label", date: Fixture.Day.AddDays(1));
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(fresh, replay, Rules.Validate(fresh, replay).Rows, warningsAcknowledged: true));
            Equal(1, f.Server.WriteCount); Equal(0, f.Transport.TogglRequests);
        });
        await check("email timeout reconciliation finds exactly one created record without Toggl", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadEmail(); f.Server.WriteMode = "commit-timeout";
            var receipt = await f.Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows, warningsAcknowledged: true);
            Equal("unknown", receipt.Status); Equal(1, f.Server.WriteCount);
            var result = await f.Service.ReconcileAsync(session, receipt);
            Equal("complete", result.Status); Equal("created", result.Rows.Single().Status); Equal<int?>(501, result.Rows.Single().RecordId);
            Equal(1, f.Server.WriteCount); Equal(0, f.Transport.TogglRequests);
        });
        await check("reconciliation retains a uniquely created email record when billing differs", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadEmail();
            f.Server.WriteMode = "commit-timeout"; f.Transport.ForceBillable = true;
            var rows = Rules.Validate(session, proposal).Rows.Select(r => r with { BillableOverride = false }).ToList();
            var receipt = await f.Service.SubmitAsync(session, proposal, rows, warningsAcknowledged: true);
            Equal("unknown", receipt.Status);
            var reconciled = await f.Service.ReconcileAsync(session, receipt);
            Equal("attention", reconciled.Status); Equal("created", reconciled.Rows.Single().Status);
            Equal<int?>(501, reconciled.Rows.Single().RecordId); Equal<bool?>(true, reconciled.Rows.Single().ActualBillable);
            True(reconciled.Message.Contains("do not submit")); Equal(1, f.Server.WriteCount); Equal(0, f.Transport.TogglRequests);
        });
        await check("email deletion recovery preserves history and permits fresh confirmed replacement without Toggl", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (session, proposal) = await f.ReadEmail();
            var receipt = await f.Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows, warningsAcknowledged: true);
            f.Server.Existing.Clear(); var candidates = await f.Service.FindMissingCreatedEntriesAsync(session, receipt);
            Equal(1, candidates.Count); var recovered = await f.Service.ConfirmDeletedEntriesAsync(session, receipt, [candidates[0].Row.RowId], true);
            Equal("created", recovered.Rows.Single().Status); Equal<int?>(501, recovered.Rows.Single().RecordId); True(recovered.Rows.Single().DeletionConfirmedAtUtc.HasValue);
            var (fresh, replacement) = await f.ReadEmail(activityId: "replacement-label");
            var created = await f.Service.SubmitAsync(fresh, replacement, Rules.Validate(fresh, replacement).Rows, warningsAcknowledged: true);
            Equal("complete", created.Status); Equal(2, f.Server.WriteCount); Equal(2, f.Store.LoadReceipts().Count); Equal(0, f.Transport.TogglRequests);
        });
        await check("mutating confirmed email minutes after saving baseline cannot submit", async () =>
        {
            using var f = new SubmissionEmailFixture(); var (original, proposal) = await f.ReadEmail();
            var altered = original with { EmailWorkbook = original.EmailWorkbook! with { Activities = original.EmailWorkbook.Activities.Select(a => a with { Minutes = 100 }).ToList() } };
            await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(altered, proposal, Rules.Validate(altered, proposal).Rows, warningsAcknowledged: true));
            Equal(0, f.Server.WriteCount); Equal(0, f.Store.LoadReceipts().Count);
        });
    }

    static void True(bool value) { if (!value) throw new Exception("Expected true"); }
    static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    static async Task<T> Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T ex) { return ex; } throw new Exception("Expected " + typeof(T).Name); }
}

sealed class SubmissionEmailFixture : IDisposable
{
    public AppSettings Settings = new() { Email = "person@example.com", EmployeeId = "123.test", InternalProjectId = 100 };
    public FakeApi Server = new() { FilterExistingDates = true };
    public SubmissionTestTransport Transport;
    public HttpClient Http;
    public TimecardApi Api;
    public SessionStore Store = new(Path.Combine(Path.GetTempPath(), "timekeeper-submission-email-tests", Guid.NewGuid().ToString("N")));
    public SubmissionService Service;
    public SubmissionEmailFixture()
    {
        Transport = new(Server); Http = new(Transport); Api = new(Settings, new("", "QB-secret"), Http); Service = new(Api, Store);
    }
    public async Task<(ReadSession, ProposalEnvelope)> ReadToggl()
    {
        // This fixture enables Toggl only for tests explicitly exercising the existing workflow.
        Api.Dispose(); Api = new(Settings, new("toggl-secret", "QB-secret"), Http); Service = new(Api, Store);
        var session = await Api.ReadAsync(Fixture.Day, Fixture.Day);
        return (session, new() { SessionId = session.SessionId, EmployeeId = Settings.EmployeeId,
            Rows = [new() { Date = Fixture.Day, SourceEntryIds = [123456789012], Project = 100, Task = 1, Category = 10, Description = "Work completed" }] });
    }
    public async Task<(ReadSession, ProposalEnvelope)> ReadEmail(string activityId = "activity-1", DateOnly? date = null)
    {
        var day = date ?? Fixture.Day;
        var workbook = new EmailWorkbook
        {
            Metadata = new() { EmployeeEmail = Settings.Email, TimeZone = Settings.TimeZoneId, EmployeeConfirmed = true, PeriodStart = day, PeriodEnd = day, GeneratedAtUtc = DateTimeOffset.UtcNow },
            Activities = [new() { ActivityId = activityId, Date = day, Minutes = 10, Description = "Email follow-up", TimeBasis = "employee_confirmed_estimate", EvidenceIds = ["immutable-email-1"] }]
        };
        var session = await Api.ReadEmailBaselineAsync(workbook); Store.SaveSession(session);
        return (session, new() { SessionId = session.SessionId, EmployeeId = Settings.EmployeeId,
            Rows = [new() { Date = day, SourceActivityIds = [activityId], Project = 100, Task = 1, Category = 10, Description = "Email follow-up" }] });
    }
    public void Dispose() { Api.Dispose(); Http.Dispose(); Directory.Delete(Store.RootPath, true); }
}

sealed class SubmissionTestTransport(FakeApi server) : DelegatingHandler(server)
{
    public bool OverrideVisible = true;
    public bool? ForceBillable;
    public int TogglRequests;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        if (url.Host.Contains("toggl")) TogglRequests++;
        if (url.AbsolutePath == "/v1/fields") return Json(OverrideVisible ? new[] { BillingFake.OverrideField(), BillingFake.ResultField() } : new[] { BillingFake.ResultField() });
        if (url.AbsolutePath == "/v1/records")
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var row = body.RootElement.GetProperty("data")[0];
            var billing = ForceBillable ?? (row.TryGetProperty("99", out var field) && field.GetProperty("value").GetString() == "Billable");
            var beforeCount = server.Existing.Count;
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, ct); }
            finally
            {
                if (server.Existing.Count > beforeCount) server.Existing[^1] = server.Existing[^1] with { Billable = billing };
            }
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            if (server.Existing.Count > beforeCount) node["data"] = JsonSerializer.SerializeToNode(new[] { FakeApi.Row((3, server.Existing[^1].RecordId), (172, billing)) });
            response.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }
        var result = await base.SendAsync(request, ct);
        if (url.AbsolutePath == "/v1/records/query" && result.IsSuccessStatusCode)
        {
            var node = JsonNode.Parse(await result.Content.ReadAsStringAsync(ct))!;
            foreach (var row in node["data"]!.AsArray())
            {
                var id = row?["3"]?["value"]?.GetValue<int>();
                var card = server.Existing.SingleOrDefault(r => r.RecordId == id);
                if (card is not null) row!["172"] = JsonSerializer.SerializeToNode(new { value = card.Billable });
            }
            result.Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
        }
        return result;
    }
    static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
}
