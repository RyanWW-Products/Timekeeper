using System.Net;
using System.Text;
using System.Text.Json;
using Timekeeper.Core;

static class BillingApiTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("Quickbase-only identity test and discovery require no Toggl token", async () =>
        {
            using var f = new BillingFixture("");
            True((await f.Api.TestQuickbaseAsync()).Contains("person@example.com"));
            var discovered = await TimecardApi.DiscoverQuickbaseIdentityAsync(f.Settings, new("", "QB-secret"), f.Http);
            Equal("Pat Example", discovered.DisplayName); Equal(0, f.Server.TogglRequests);
            await Throws<InvalidOperationException>(() => f.Api.TestAsync()); Equal(0, f.Server.TogglRequests);
        });
        await check("email baseline reads only Quickbase and retains confirmed source workbook", async () =>
        {
            using var f = new BillingFixture(""); var workbook = Workbook();
            var session = await f.Api.ReadEmailBaselineAsync(workbook);
            True(ReferenceEquals(workbook, session.EmailWorkbook)); Equal(0, session.Days.Single().Entries.Count);
            Equal(1, session.Days.Single().Existing.Count); Equal(0, f.Server.TogglRequests); True(session.Reference.Billing!.CanOverride);
        });
        await check("unconfirmed or wrong-account email workbook fails before network access", async () =>
        {
            foreach (var metadata in new[] { Workbook().Metadata with { EmployeeConfirmed = false }, Workbook().Metadata with { EmployeeEmail = "someone.else@example.com" } })
            {
                using var f = new BillingFixture("");
                await Throws<InvalidDataException>(() => f.Api.ReadEmailBaselineAsync(Workbook() with { Metadata = metadata }));
                Equal(0, f.Server.Requests); Equal(0, f.Server.WriteCount);
            }
        });
        await check("billing discovery uses exact labels and returned IDs", async () =>
        {
            using var f = new BillingFixture(); var capability = await f.Api.ReadBillingCapabilitiesAsync();
            True(capability.CanOverride); Equal<int?>(99, capability.OverrideFieldId); Equal<int?>(172, capability.BillableFieldId);
            True(f.Server.MetadataUrl.Contains("tableId=bd3bsxtbp")); True(f.Server.MetadataUrl.Contains("includeFieldPerms=true")); Equal(HttpMethod.Get, f.Server.MetadataMethod);
        });
        await check("hidden override permissions preserve ordinary Quickbase defaults", async () =>
        {
            using var f = new BillingFixture(); f.Server.Metadata = new[] { BillingFake.ResultField() };
            var capability = await f.Api.ReadBillingCapabilitiesAsync(); True(!capability.CanOverride); Equal<int?>(172, capability.BillableFieldId);
            await Throws<InvalidOperationException>(() => f.Api.ValidateBillingAsync([Row(false)])); Equal(0, f.Server.WriteCount);
            var result = await f.Api.CreateRowAsync(Row(null), default); Equal("created", result.Status);
            using var body = JsonDocument.Parse(f.Server.WriteJson); True(!body.RootElement.GetProperty("data")[0].TryGetProperty("99", out _));
        });
        await check("billing rejects formula lookup restricted and malformed override metadata", async () =>
        {
            var invalidFields = new object[]
            {
                BillingFake.OverrideField(mode: "formula"), BillingFake.OverrideField(mode: "lookup"),
                BillingFake.OverrideField(type: "checkbox"), BillingFake.OverrideField(id: 11),
                BillingFake.OverrideField(permissions: new[] { new { permissionType = "View" } }),
                BillingFake.OverrideField(permissions: new[] { new { permissionType = "None" } }),
                BillingFake.OverrideField(properties: new { formula = "true" }),
                BillingFake.OverrideField(properties: new { lookupReferenceFieldId = 11 }),
                BillingFake.OverrideField(type: "text-multiple-choice", properties: new { choices = new[] { "Billable", "Non-Billable" } })
            };
            foreach (var field in invalidFields)
            {
                using var f = new BillingFixture(); f.Server.Metadata = new[] { field, BillingFake.ResultField() };
                True(!(await f.Api.ReadBillingCapabilitiesAsync()).CanOverride);
                await Throws<InvalidOperationException>(() => f.Api.ValidateBillingAsync([Row(true)])); Equal(0, f.Server.WriteCount);
            }
        });
        await check("duplicate billing labels and unavailable result cannot authorize overrides", async () =>
        {
            foreach (var fields in new object[][] { [BillingFake.OverrideField(), BillingFake.OverrideField(id: 100), BillingFake.ResultField()], [BillingFake.OverrideField()] })
            {
                using var f = new BillingFixture(); f.Server.Metadata = fields;
                True(!(await f.Api.ReadBillingCapabilitiesAsync()).CanOverride); Equal(0, f.Server.WriteCount);
            }
        });
        await check("matching text choice metadata authorizes exact Quickbase values", async () =>
        {
            using var f = new BillingFixture(); f.Server.Metadata = new[] { BillingFake.OverrideField(type: "text-multiple-choice", properties: new { choices = new[] { "Billable", "NonBillable" } }), BillingFake.ResultField() };
            True((await f.Api.ReadBillingCapabilitiesAsync()).CanOverride);
        });
        await check("billing permissions use only the caller's roles and combine their grants", async () =>
        {
            foreach (var scenario in new[] { (OwnRoles: "12", Allowed: true), (OwnRoles: "13", Allowed: false), (OwnRoles: "12 ; 13", Allowed: true), (OwnRoles: "99", Allowed: false) })
            {
                using var f = new BillingFixture(); f.Server.RoleIds = scenario.OwnRoles;
                f.Server.Metadata = new[] { BillingFake.OverrideField(permissions: new[] { new { roleId = 12, role = "Case Manager", permissionType = "Modify" }, new { roleId = 13, role = "Viewer", permissionType = "View" } }), BillingFake.ResultField() };
                Equal(scenario.Allowed, (await f.Api.ReadBillingCapabilitiesAsync()).CanOverride); Equal(1, f.Server.RoleQueries);
                Equal("ToText(UserRoles(\"ID\"))", f.Server.RoleFormula); Equal(0, f.Server.WriteCount);
            }
        });
        await check("unavailable or malformed caller roles disable overrides without losing readable billing", async () =>
        {
            foreach (var result in new[] { "", "QB-secret", "12;", "12;12", "-1", "1.5" })
            {
                using var f = new BillingFixture(); f.Server.RoleIds = result;
                f.Server.Metadata = new[] { BillingFake.OverrideField(permissions: new[] { new { roleId = 12, permissionType = "Modify" } }), BillingFake.ResultField() };
                var capability = await f.Api.ReadBillingCapabilitiesAsync(); True(!capability.CanOverride); Equal<int?>(172, capability.BillableFieldId); True(!capability.Message.Contains("QB-secret"));
            }
            using var denied = new BillingFixture(); denied.Server.RoleStatus = HttpStatusCode.Forbidden;
            denied.Server.Metadata = new[] { BillingFake.OverrideField(permissions: new[] { new { roleId = 12, permissionType = "Modify" } }), BillingFake.ResultField() };
            True(!(await denied.Api.ReadBillingCapabilitiesAsync()).CanOverride); Equal(0, denied.Server.WriteCount);
        });
        await check("billing preflight refreshes cached permissions before writes", async () =>
        {
            using var f = new BillingFixture(); True((await f.Api.ReadBillingCapabilitiesAsync()).CanOverride);
            f.Server.Metadata = Array.Empty<object>(); await Throws<InvalidOperationException>(() => f.Api.ValidateBillingAsync([Row(false)]));
            Equal(2, f.Server.MetadataQueries); Equal(0, f.Server.WriteCount);
        });
        await check("metadata failures disable billing without exposing service content", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError })
            {
                using var f = new BillingFixture(); f.Server.MetadataStatus = status;
                var capability = await f.Api.ReadBillingCapabilitiesAsync(); True(!capability.CanOverride); True(!capability.Message.Contains("QB-secret"));
                await Throws<InvalidOperationException>(() => f.Api.ValidateBillingAsync([Row(false)])); Equal(0, f.Server.WriteCount);
            }
        });
        await check("both billing choices preserve assignment project task and category", async () =>
        {
            foreach (bool choice in new[] { true, false })
            {
                using var f = new BillingFixture(); await f.Api.ValidateBillingAsync([Row(choice)]);
                var outcome = await f.Api.CreateRowAsync(Row(choice), default); Equal("created", outcome.Status); Equal<bool?>(choice, outcome.ActualBillable);
                using var body = JsonDocument.Parse(f.Server.WriteJson); var data = body.RootElement.GetProperty("data")[0];
                Equal(choice ? "Billable" : "NonBillable", data.GetProperty("99").GetProperty("value").GetString());
                Equal(100, data.GetProperty("11").GetProperty("value").GetInt32()); Equal(44, data.GetProperty("16").GetProperty("value").GetInt32());
                Equal(11, data.GetProperty("22").GetProperty("value").GetInt32()); Equal(555, data.GetProperty("73").GetProperty("value").GetInt32());
                True(!data.TryGetProperty("172", out _)); Equal(1, f.Server.WriteCount);
            }
        });
        await check("billing mismatch preserves confirmed created ID and marks attention", async () =>
        {
            using var f = new BillingFixture(); f.Server.ForceBillable = true;
            var result = await f.Api.CreateRowAsync(Row(false), default);
            Equal("created", result.Status); Equal<int?>(901, result.RecordId); Equal<bool?>(true, result.ActualBillable); True(result.Message.Contains("Do not resubmit")); Equal(1, f.Server.WriteCount);
        });
        await check("missing billing return is confirmed through read-only record lookup", async () =>
        {
            using var f = new BillingFixture(); f.Server.OmitWriteResult = true;
            var result = await f.Api.CreateRowAsync(Row(false), default);
            Equal("created", result.Status); Equal<bool?>(false, result.ActualBillable); Equal(1, f.Server.RecordQueries); Equal(1, f.Server.WriteCount);
        });
        await check("billing verification failure never loses the created record", async () =>
        {
            using var f = new BillingFixture(); f.Server.OmitWriteResult = true; f.Server.QueryStatus = HttpStatusCode.Forbidden;
            var result = await f.Api.CreateRowAsync(Row(false), default);
            Equal("created", result.Status); Equal<int?>(901, result.RecordId); Equal<bool?>(null, result.ActualBillable);
            True(result.Message.Contains("could not be confirmed")); Equal(1, f.Server.WriteCount);
        });
        await check("existing timecards include discovered billing status", async () =>
        {
            using var f = new BillingFixture(); f.Server.ForceBillable = false;
            var existing = await f.Api.ReadExistingAsync(new(2026, 10, 5)); Equal<bool?>(false, existing.Single().Billable);
        });
    }

    static VerifiedRow Row(bool? billable) => new() { Date = new(2026, 10, 5), Hours = 1m, Project = 100, Task = 44, Category = 11, Assignment = 555, Description = "Case meeting", BillableOverride = billable };
    static EmailWorkbook Workbook() => new()
    {
        Metadata = new() { EmployeeEmail = "person@example.com", TimeZone = "Eastern Standard Time", EmployeeConfirmed = true, PeriodStart = new(2026, 10, 5), PeriodEnd = new(2026, 10, 5), GeneratedAtUtc = DateTimeOffset.UtcNow },
        Activities = [new() { ActivityId = "activity-1", Date = new(2026, 10, 5), Minutes = 10, Description = "Reviewed case meeting notes", TimeBasis = "employee_confirmed_estimate", EvidenceIds = ["immutable-message-1"] }]
    };
    static void True(bool value) { if (!value) throw new Exception("Expected true"); }
    static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}

sealed class BillingFixture : IDisposable
{
    public AppSettings Settings = new() { Email = "person@example.com", EmployeeId = "123.test", InternalProjectId = 100 };
    public BillingFake Server = new();
    public HttpClient Http;
    public TimecardApi Api;
    public BillingFixture(string toggl = "toggl-secret") { Http = new(Server); Api = new(Settings, new(toggl, "QB-secret"), Http); }
    public void Dispose() { Api.Dispose(); Http.Dispose(); }
}

sealed class BillingFake : HttpMessageHandler
{
    public object Metadata = new[] { OverrideField(), ResultField() };
    public HttpStatusCode MetadataStatus = HttpStatusCode.OK, QueryStatus = HttpStatusCode.OK, RoleStatus = HttpStatusCode.OK;
    public int MetadataQueries, WriteCount, TogglRequests, RecordQueries, Requests, RoleQueries;
    public string RoleIds = "12", RoleFormula = "";
    public string MetadataUrl = "", WriteJson = "";
    public HttpMethod? MetadataMethod;
    public bool? ForceBillable;
    public bool OmitWriteResult;
    private bool actualBillable;
    public static object OverrideField(int id = 99, string mode = "", string type = "text", object? permissions = null, object? properties = null) =>
        new { id, label = "Billable Override", fieldType = type, mode, permissions = permissions ?? Array.Empty<object>(), properties = properties ?? new { formula = "" } };
    public static object ResultField() => new { id = 172, label = "Bill This Task", fieldType = "checkbox", mode = "formula", properties = new { formula = "[Billable Override] = \"Billable\"" } };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        Requests++;
        if (url.Host.Contains("toggl")) { TogglRequests++; return Json(new { email = "person@example.com" }); }
        if (url.AbsolutePath == "/v1/formula/run")
        {
            using var formulaBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var formula = formulaBody.RootElement.GetProperty("formula").GetString()!;
            if (formula.Contains("UserRoles", StringComparison.Ordinal))
            {
                RoleQueries++; RoleFormula = formula;
                return RoleStatus == HttpStatusCode.OK ? Json(new { result = RoleIds }) : new(RoleStatus) { Content = new StringContent("QB-secret private roles") };
            }
            return Json(new { result = "123.test;person@example.com;Pat Example" });
        }
        if (url.AbsolutePath == "/v1/fields")
        {
            MetadataQueries++; MetadataUrl = url.AbsoluteUri; MetadataMethod = request.Method;
            return MetadataStatus == HttpStatusCode.OK ? Json(Metadata) : new(MetadataStatus) { Content = new StringContent("QB-secret private metadata") };
        }
        if (url.AbsolutePath == "/v1/records")
        {
            WriteCount++; WriteJson = await request.Content!.ReadAsStringAsync(ct); using var body = JsonDocument.Parse(WriteJson);
            var row = body.RootElement.GetProperty("data")[0];
            actualBillable = ForceBillable ?? (row.TryGetProperty("99", out var choice) && choice.GetProperty("value").GetString() == "Billable");
            return Json(new { metadata = new { createdRecordIds = new[] { 901 } }, data = OmitWriteResult ? Array.Empty<object>() : new[] { FakeApi.Row((3, 901), (172, actualBillable)) } });
        }
        if (url.AbsolutePath == "/v1/records/query")
        {
            RecordQueries++;
            if (QueryStatus != HttpStatusCode.OK) return new(QueryStatus) { Content = new StringContent("QB-secret") };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var table = body.RootElement.GetProperty("from").GetString();
            object[]? references = table switch
            {
                "beb9dvs6p" => [FakeApi.Row((3, 11), (6, "Internal"))],
                "bd3bsxtbn" => [FakeApi.Row((3, 44), (7, "Internal work"), (11, true), (17, 11))],
                "bd3bsxtbj" => [FakeApi.Row((3, 100), (6, "Internal"))],
                "biqs87fvg" => [],
                _ => null
            };
            if (references is not null) return Json(new { data = references, metadata = new { totalRecords = references.Length } });
            return Json(new { data = new[] { FakeApi.Row((3, 901), (7, "2026-10-05"), (10, 1m), (11, 100), (16, 44), (22, 11), (24, new { id = "123.test" }), (73, 555), (19, "Case meeting"), (172, ForceBillable ?? actualBillable)) }, metadata = new { totalRecords = 1 } });
        }
        throw new Exception("Unexpected billing test endpoint: " + url.AbsolutePath);
    }
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
