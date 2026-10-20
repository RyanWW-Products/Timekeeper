using System.Net;
using System.Text;
using System.Text.Json;
using Timekeeper.Core;

static class BillingApiTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("live recognized billing rule resolves task defaults and internal exceptions", async () =>
        {
            foreach (var projectType in new[] { "Internal", "Client" })
            {
                using var f = new BillingFixture();
                f.Server.ProjectType = projectType;
                f.Server.Metadata = new object[] { BillingFake.OverrideField(),
                    new { id = 172, label = "Bill This Task", fieldType = "checkbox", mode = "formula", properties = new { formula = "If([Billable Override]=\"\",If([Project Type]=\"Internal\",false,[Bill Task]=true,true,false), [Billable Override]=\"NonBillable\",false,[Billable Override]=\"Billable\",true)" } },
                    new { id = 90, label = "Project Type", fieldType = "text", mode = "lookup", properties = new { lookupReferenceFieldId = 11, lookupTargetFieldId = 9 } },
                    new { id = 91, label = "Bill Task", fieldType = "checkbox", mode = "lookup", properties = new { lookupReferenceFieldId = 16, lookupTargetFieldId = 8 } }
                };
                var reference = await f.Api.ReadReferenceAsync();
                Equal<bool?>(projectType != "Internal", reference.Billing!.DefaultFor(100, 44));
                Equal<bool?>(null, reference.Billing.DefaultFor(999, 44)); Equal(0, f.Server.WriteCount);
            }
        });
        await check("task billing failures do not hide a readable internal project default and can retry", async () =>
        {
            using var f = new BillingFixture(); f.Server.Metadata = BillingFake.DefaultFields();
            f.Server.TaskDefaultsStatus = HttpStatusCode.Forbidden;
            var billing = await f.Api.ReadBillingDefaultsAsync([100]);
            Equal<bool?>(false, billing.DefaultFor(100, 44)); Equal(1, f.Server.ProjectDefaultQueries);
            f.Server.ProjectType = "Client";
            billing = await f.Api.ReadBillingDefaultsAsync([100], refresh: true);
            Equal<bool?>(null, billing.DefaultFor(100, 44)); True(billing.DefaultUnavailableReason(100, 44).Contains("task billing values could not be read"));
            True(!billing.DefaultUnavailableReason(100, 44).Contains("QB-secret"));
            f.Server.TaskDefaultsStatus = HttpStatusCode.OK;
            var retry = await f.Api.ReadBillingDefaultsAsync([100]);
            True(ReferenceEquals(billing, retry)); Equal<bool?>(true, retry.DefaultFor(100, 44)); Equal("", retry.DefaultUnavailableReason(100, 44)); Equal(0, f.Server.WriteCount);
        });
        await check("a nonbillable task resolves without readable project types but a billable task stays unknown", async () =>
        {
            using var f = new BillingFixture(); f.Server.Metadata = BillingFake.DefaultFields();
            f.Server.ProjectDefaultsStatus = HttpStatusCode.Forbidden;
            f.Server.TaskDefaultRows = [FakeApi.Row((3, 44), (8, false)), FakeApi.Row((3, 45), (8, true))];
            var billing = await f.Api.ReadBillingDefaultsAsync([100]);
            Equal<bool?>(false, billing.DefaultFor(100, 44)); Equal<bool?>(null, billing.DefaultFor(100, 45));
            True(billing.DefaultUnavailableReason(100, 45).Contains("project types could not be read"));
            f.Server.ProjectDefaultsStatus = HttpStatusCode.OK; f.Server.ProjectType = "Client";
            Equal<bool?>(true, (await f.Api.ReadBillingDefaultsAsync([100])).DefaultFor(100, 45)); Equal(0, f.Server.WriteCount);
        });
        await check("visible formula branches resolve independently when the other lookup metadata is hidden", async () =>
        {
            using var internalOnly = new BillingFixture(); internalOnly.Server.Metadata = BillingFake.DefaultFields(includeTask: false);
            var billing = await internalOnly.Api.ReadBillingDefaultsAsync([100]);
            Equal<bool?>(false, billing.DefaultFor(100, 44)); Equal(0, internalOnly.Server.TaskDefaultQueries);
            using var taskOnly = new BillingFixture(); taskOnly.Server.Metadata = BillingFake.DefaultFields(includeProject: false);
            taskOnly.Server.TaskDefaultRows = [FakeApi.Row((3, 44), (8, false)), FakeApi.Row((3, 45), (8, true))];
            billing = await taskOnly.Api.ReadBillingDefaultsAsync([100]);
            Equal<bool?>(false, billing.DefaultFor(100, 44)); Equal<bool?>(null, billing.DefaultFor(100, 45)); Equal(0, taskOnly.Server.ProjectDefaultQueries);
            True(billing.DefaultUnavailableReason(100, 45).Contains("Project Type relationship"));
        });
        await check("partial task defaults are retried instead of caching missing values indefinitely", async () =>
        {
            using var f = new BillingFixture(); f.Server.Metadata = BillingFake.DefaultFields(); f.Server.ProjectType = "Client";
            f.Server.TaskDefaultRows = [FakeApi.Row((3, 44), (8, true)), FakeApi.Row((3, 45))];
            var billing = await f.Api.ReadBillingDefaultsAsync([100]);
            Equal<bool?>(true, billing.DefaultFor(100, 44)); Equal<bool?>(null, billing.DefaultFor(100, 45));
            f.Server.TaskDefaultRows = [FakeApi.Row((3, 44), (8, true)), FakeApi.Row((3, 45), (8, false))];
            billing = await f.Api.ReadBillingDefaultsAsync([100]);
            Equal<bool?>(false, billing.DefaultFor(100, 45)); Equal(2, f.Server.TaskDefaultQueries);
            await f.Api.ReadBillingDefaultsAsync([100]); Equal(2, f.Server.TaskDefaultQueries);
        });
        await check("failed billing metadata can recover without rebuilding the API client", async () =>
        {
            using var f = new BillingFixture(); f.Server.Metadata = BillingFake.DefaultFields(); f.Server.MetadataStatus = HttpStatusCode.Forbidden;
            var billing = await f.Api.ReadBillingDefaultsAsync([100]);
            Equal<bool?>(null, billing.DefaultFor(100, 44)); True(billing.DefaultUnavailableReason(100, 44).Contains("metadata could not be read"));
            f.Server.MetadataStatus = HttpStatusCode.OK;
            Equal<bool?>(false, (await f.Api.ReadBillingDefaultsAsync([100])).DefaultFor(100, 44)); Equal(2, f.Server.MetadataQueries); Equal(0, f.Server.WriteCount);
        });
        await check("billing refresh reads changed inputs and does not retain previously readable permissions after failure", async () =>
        {
            using var f = new BillingFixture(); f.Server.Metadata = BillingFake.DefaultFields();
            var first = await f.Api.ReadBillingDefaultsAsync([100]); Equal<bool?>(false, first.DefaultFor(100, 44));
            f.Server.ProjectType = "Client";
            var updated = await f.Api.ReadBillingDefaultsAsync([100], refresh: true);
            Equal<bool?>(true, updated.DefaultFor(100, 44)); True(!ReferenceEquals(first, updated)); Equal(2, f.Server.TaskDefaultQueries); Equal(2, f.Server.ProjectDefaultQueries);
            f.Server.MetadataStatus = HttpStatusCode.Forbidden;
            var failed = await f.Api.ReadBillingDefaultsAsync([100], refresh: true);
            True(!failed.CanOverride); Equal<bool?>(null, failed.DefaultFor(100, 44));
            True(!(await f.Api.ReadBillingCapabilitiesAsync()).CanOverride); Equal(4, f.Server.MetadataQueries); Equal(0, f.Server.WriteCount);
        });
        await check("billing formula formatting is tolerated without changing literals or field names", async () =>
        {
            using var formatted = new BillingFixture(); formatted.Server.Metadata = BillingFake.DefaultFields(formula: BillingFake.DefaultFormula.Replace("If(", "IF (\n ").Replace(",", ", \n"));
            Equal<bool?>(false, (await formatted.Api.ReadBillingDefaultsAsync([100])).DefaultFor(100, 44));
            foreach (var changed in new[] { BillingFake.DefaultFormula.Replace("Internal", "Inter nal"), BillingFake.DefaultFormula.Replace("[Project Type]", "[ProjectType]"), BillingFake.DefaultFormula.Replace("[Bill Task]", "[Bill  Task]") })
            {
                using var f = new BillingFixture(); f.Server.Metadata = BillingFake.DefaultFields(formula: changed);
                var billing = await f.Api.ReadBillingDefaultsAsync([100]); Equal<bool?>(null, billing.DefaultFor(100, 44)); Equal(0, f.Server.ProjectDefaultQueries);
                True(billing.DefaultUnavailableReason(100, 44).Contains("supported Bill This Task formula"));
            }
        });
        await check("billing metadata handles case labels numeric string references and serialized booleans", async () =>
        {
            using var f = new BillingFixture(); f.Server.ProjectType = "Client";
            f.Server.Metadata = BillingFake.DefaultFields(includeProject: false, includeTask: false).Concat(new object[] {
                new { id = 90, label = "project type", fieldType = "text", mode = "lookup", properties = new { lookupReferenceFieldId = "11", lookupTargetFieldId = "9" } },
                new { id = 91, label = "BILL TASK", fieldType = "checkbox", mode = "lookup", properties = new { lookupReferenceFieldId = "16", lookupTargetFieldId = "8" } }
            }).ToArray();
            f.Server.TaskDefaultRows = [FakeApi.Row((3, 44), (8, "TRUE")), FakeApi.Row((3, 45), (8, "False")), FakeApi.Row((3, 46), (8, "unknown"))];
            var billing = await f.Api.ReadBillingDefaultsAsync([100]);
            Equal<bool?>(true, billing.DefaultFor(100, 44)); Equal<bool?>(false, billing.DefaultFor(100, 45)); Equal<bool?>(null, billing.DefaultFor(100, 46)); Equal(0, f.Server.WriteCount);
        });
        await check("malformed lookup metadata does not crash optional billing discovery", async () =>
        {
            using var f = new BillingFixture();
            f.Server.Metadata = BillingFake.DefaultFields(includeProject: false).Concat(new object[] { new { id = 90, label = "Project Type", fieldType = "text", mode = "lookup", properties = "invalid" } }).ToArray();
            var billing = await f.Api.ReadBillingDefaultsAsync([100]);
            Equal<bool?>(null, billing.DefaultFor(100, 44)); True(billing.DefaultUnavailableReason(100, 44).Contains("Project Type relationship")); Equal(0, f.Server.WriteCount);
        });
        await check("unknown billing formulas never guess a default", async () =>
        {
            using var f = new BillingFixture(); var reference = await f.Api.ReadReferenceAsync();
            Equal<bool?>(null, reference.Billing!.DefaultFor(100, 44));
        });        await check("Quickbase-only identity test and discovery require no Toggl token", async () =>
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
        await check("local email confirmation is deferred to final review without changing source approval", async () =>
        {
            foreach (var format in new[] { "timekeeper_email_review", "timekeeper_email_import" })
            {
                using var f = new BillingFixture(""); var source = Workbook();
                var workbook = source with { Metadata = source.Metadata with { Format = format, EmployeeConfirmed = format != "timekeeper_email_review" }, ReviewConfirmedAtUtc = null };
                var session = await f.Api.ReadEmailBaselineAsync(workbook);
                True(ReferenceEquals(workbook, session.EmailWorkbook)); True(!session.EmailWorkbook!.ReviewConfirmedAtUtc.HasValue);
                Equal(format != "timekeeper_email_review", session.EmailWorkbook.Metadata.EmployeeConfirmed);
                Equal(0, f.Server.TogglRequests); Equal(0, f.Server.WriteCount);
            }
        });
        await check("minimal imports still require Word approval before network access", async () =>
        {
            using var f = new BillingFixture(""); var source = Workbook();
            var workbook = source with { Metadata = source.Metadata with { Format = "timekeeper_email_import", EmployeeConfirmed = false } };
            await Throws<InvalidDataException>(() => f.Api.ReadEmailBaselineAsync(workbook));
            Equal(0, f.Server.Requests); Equal(0, f.Server.WriteCount);
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
    public int MetadataQueries, WriteCount, TogglRequests, RecordQueries, Requests, RoleQueries, TaskDefaultQueries, ProjectDefaultQueries;
    public HttpStatusCode TaskDefaultsStatus = HttpStatusCode.OK, ProjectDefaultsStatus = HttpStatusCode.OK;
    public object[]? TaskDefaultRows;
    public string RoleIds = "12", RoleFormula = "";
    public string MetadataUrl = "", WriteJson = "";
    public HttpMethod? MetadataMethod;
    public bool? ForceBillable;
    public bool OmitWriteResult;
    public string ProjectType = "Internal";
    private bool actualBillable;
    public static object OverrideField(int id = 99, string mode = "", string type = "text", object? permissions = null, object? properties = null) =>
        new { id, label = "Billable Override", fieldType = type, mode, permissions = permissions ?? Array.Empty<object>(), properties = properties ?? new { formula = "" } };
    public static object ResultField() => new { id = 172, label = "Bill This Task", fieldType = "checkbox", mode = "formula", properties = new { formula = "[Billable Override] = \"Billable\"" } };
    public const string DefaultFormula = "If([Billable Override]=\"\",If([Project Type]=\"Internal\",false,[Bill Task]=true,true,false), [Billable Override]=\"NonBillable\",false,[Billable Override]=\"Billable\",true)";
    public static object[] DefaultFields(bool includeProject = true, bool includeTask = true, string? formula = null)
    {
        var fields = new List<object> { OverrideField(), new { id = 172, label = "Bill This Task", fieldType = "checkbox", mode = "formula", properties = new { formula = formula ?? DefaultFormula } } };
        if (includeProject) fields.Add(new { id = 90, label = "Project Type", fieldType = "text", mode = "lookup", properties = new { lookupReferenceFieldId = 11, lookupTargetFieldId = 9 } });
        if (includeTask) fields.Add(new { id = 91, label = "Bill Task", fieldType = "checkbox", mode = "lookup", properties = new { lookupReferenceFieldId = 16, lookupTargetFieldId = 8 } });
        return fields.ToArray();
    }
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
            var select = body.RootElement.GetProperty("select").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            if (table == "bd3bsxtbn" && select.Contains(8))
            {
                TaskDefaultQueries++;
                if (TaskDefaultsStatus != HttpStatusCode.OK) return new(TaskDefaultsStatus) { Content = new StringContent("QB-secret private tasks") };
                if (TaskDefaultRows is not null) return Json(new { data = TaskDefaultRows, metadata = new { totalRecords = TaskDefaultRows.Length } });
            }
            if (table == "bd3bsxtbj" && select.Contains(9))
            {
                ProjectDefaultQueries++;
                if (ProjectDefaultsStatus != HttpStatusCode.OK) return new(ProjectDefaultsStatus) { Content = new StringContent("QB-secret private projects") };
            }
            object[]? references = table switch
            {
                "beb9dvs6p" => [FakeApi.Row((3, 11), (6, "Internal"))],
                "bd3bsxtbn" => [FakeApi.Row((3, 44), (7, "Internal work"), (8, true), (11, true), (17, 11))],
                "bd3bsxtbj" => [FakeApi.Row((3, 100), (6, "Internal"), (9, ProjectType))],
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
