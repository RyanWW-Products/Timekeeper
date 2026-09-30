using System.Net;
using System.Text;
using System.Text.Json;
using Timekeeper.Core;

var failures = new List<string>();
var passed = 0;
await Check("bad task category identifies table record field and value", async () =>
{
    using var f = new Fixture();
    f.Server.RowsByTable[f.Settings.TasksTable] = [FakeApi.Row((3, 44), (7, "Internal work"), (11, true), (17, null))];
    var error = await DataError(() => f.Api.ReadReferenceAsync());
    True(error.Message.Contains("Tasks (bd3bsxtbn)")); True(error.Message.Contains("#44 — Internal work"));
    True(error.Message.Contains("Category (field 17)")); True(error.Message.Contains("null (blank)"));
    Equal("https://trialexhibits.quickbase.com/db/bd3bsxtbn?a=dr&rid=44", error.RecordUrl); Equal(0, f.Server.WriteCount);
});
await Check("missing record ID identifies result position without inventing a link", async () =>
{
    using var f = new Fixture(); f.Server.RowsByTable[f.Settings.CategoriesTable] = [FakeApi.Row((3, 10), (6, "Good")), FakeApi.Row((6, "Missing ID"))];
    var error = await DataError(() => f.Api.ReadReferenceAsync());
    True(error.Message.Contains("Categories (beb9dvs6p)")); True(error.Message.Contains("result row 2"));
    True(error.Message.Contains("Record ID (field 3)")); True(error.Message.Contains("missing field or value")); Equal<string?>(null, error.RecordUrl);
});
await Check("malformed field wrapper receives the same contextual error", async () =>
{
    using var f = new Fixture(); f.Server.RowsByTable[f.Settings.TasksTable] = [new Dictionary<string, object?> { ["3"] = new { value = 7 }, ["7"] = new { value = "Broken task" }, ["11"] = new { value = true }, ["17"] = "invalid wrapper" }];
    var error = await DataError(() => f.Api.ReadReferenceAsync());
    True(error.Message.Contains("#7 — Broken task")); True(error.Message.Contains("Category (field 17)"));
});
await Check("exact whole decimal IDs are accepted across references", async () =>
{
    using var f = new Fixture();
    f.Server.RowsByTable[f.Settings.CategoriesTable] = [FakeApi.Row((3, "10.0"), (6, "Internal"))];
    f.Server.RowsByTable[f.Settings.TasksTable] = [FakeApi.Row((3, 44.0m), (7, "Internal work"), (11, true), (17, "10.000"))];
    f.Server.RowsByTable[f.Settings.ProjectsTable] = [FakeApi.Row((3, "100.0"), (6, "Internal"))];
    f.Server.RowsByTable[f.Settings.AssignmentsTable] = [FakeApi.Row((3, 201.0m), (13, "Assigned work"), (15, "100.00"), (11, "Active"), (73, new[] { new { email = f.Settings.Email } }), (77, "44.0"), (75, 10.0m))];
    var reference = await f.Api.ReadReferenceAsync();
    Equal(10, reference.Categories.Single().Id); Equal(44, reference.Tasks.Single().Id); Equal(100, reference.InternalProject!.Id);
    Equal(201, reference.Assignments.Single().Id); Equal<int?>(44, reference.Assignments.Single().TaskId); Equal<int?>(10, reference.Assignments.Single().CategoryId);
});
await Check("fractions overflow and nonpositive required IDs are never rounded or accepted", async () =>
{
    foreach (var value in new object[] { "10.5", "10.000000000000000000000000000001", "2147483648", -7, "0.0", true })
    {
        using var f = new Fixture(); f.Server.RowsByTable[f.Settings.TasksTable] = [FakeApi.Row((3, 44), (7, "Internal work"), (11, true), (17, value))];
        var error = await DataError(() => f.Api.ReadReferenceAsync()); True(error.Message.Contains("Category (field 17)")); Equal(0, f.Server.WriteCount);
    }
});
await Check("optional assignment relations accept blank and whole zero", async () =>
{
    foreach (var value in new object?[] { null, "", "  ", "0.00", 0.0m })
    {
        using var f = new Fixture();
        f.Server.RowsByTable[f.Settings.AssignmentsTable] = [FakeApi.Row((3, 201), (15, 100), (11, "Active"), (73, new[] { new { email = f.Settings.Email } }), (77, value), (75, value))];
        var reference = await f.Api.ReadReferenceAsync(); Equal<int?>(null, reference.Assignments.Single().TaskId); Equal<int?>(null, reference.Assignments.Single().CategoryId);
    }
});
await Check("irrelevant assignments are filtered before parsing their relations", async () =>
{
    using var f = new Fixture();
    f.Server.RowsByTable[f.Settings.AssignmentsTable] = [
        FakeApi.Row((3, 201), (15, null), (11, "Active"), (73, new[] { new { email = "someone.else@example.com" } })),
        FakeApi.Row((3, 202), (15, null), (11, "Closed"), (73, new[] { new { email = f.Settings.Email } })),
        FakeApi.Row((3, 203), (15, 100), (11, "Active"), (73, new[] { new { email = f.Settings.Email } }))];
    var reference = await f.Api.ReadReferenceAsync(); Equal(203, reference.Assignments.Single().Id);
});
await Check("a personal broken assignment still blocks with its project field identified", async () =>
{
    using var f = new Fixture();
    f.Server.RowsByTable[f.Settings.AssignmentsTable] = [FakeApi.Row((3, 201), (13, "Assigned work"), (15, null), (11, "Active"), (73, new[] { new { email = f.Settings.Email } }))];
    var error = await DataError(() => f.Api.ReadReferenceAsync());
    True(error.Message.Contains("Assignments (biqs87fvg)")); True(error.Message.Contains("#201 — Assigned work")); True(error.Message.Contains("Project (field 15)"));
});
await Check("assignment lookup identifies bad optional relation without suppressing other employees", async () =>
{
    foreach (var field in new[] { 75, 77 })
    {
        using var f = new Fixture();
        f.Server.RowsByTable[f.Settings.AssignmentsTable] = [FakeApi.Row((3, 201), (13, "Other work"), (15, 100), (field, "not a number"), (73, new[] { new { email = "someone.else@example.com" } }))];
        var error = await DataError(() => f.Api.FindAssignmentsAsync("Other"));
        True(error.Message.Contains($"field {field}")); True(error.Message.Contains("#201 — Other work")); True(error.Message.Contains("not a number"));
    }
});
await Check("field diagnostics bound text and redact credentials without dumping records", async () =>
{
    using var f = new Fixture();
    f.Server.RowsByTable[f.Settings.TasksTable] = [FakeApi.Row((3, 44), (7, "TOP-SECRET-TOGGL\n\u202e" + new string('x', 400)), (11, true), (17, "TOP-SECRET-QB"), (8, "unrelated private notes"))];
    var error = await DataError(() => f.Api.ReadReferenceAsync());
    True(!error.Message.Contains("TOP-SECRET")); True(!error.Message.Contains("\u202e")); True(!error.Message.Contains("private notes"));
    True(error.Message.Contains("[redacted]")); True(error.Message.Length < 800);
});
await Check("invalid existing hours identify the timecard", async () =>
{
    using var f = new Fixture();
    f.Server.RowsByTable[f.Settings.TimecardsTable] = [FakeApi.Row((3, 901), (7, "2026-09-28"), (10, "bad hours"), (19, "Existing work"), (24, new { id = f.Settings.EmployeeId }))];
    var error = await DataError(() => f.Api.ReadExistingAsync(Fixture.Day));
    True(error.Message.Contains("Timecards (bd3bsxtbp)")); True(error.Message.Contains("#901 — Existing work")); True(error.Message.Contains("Hours (field 10)"));
});
await Check("read uses local RFC3339 bounds and exact Toggl fields", async () =>
{
    using var f = new Fixture();
    var s = await f.Read();
    Equal(1, s.Days[0].Entries.Count);
    Equal(25200L, s.Days[0].Entries[0].DurationSeconds);
    Equal(123456789012L, s.Days[0].Entries[0].Id);
    True(f.Server.EntryUrl.Contains("2026-09-27T00:00:00.0000000-04:00"));
    True(f.Server.EntryUrl.Contains("2026-09-29T00:00:00.0000000-04:00"));
    True(s.Days[0].Entries[0].Billable);
});
await Check("DST day has distinct start and end offsets", async () =>
{
    using var f = new Fixture();
    await f.Api.ReadAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1));
    True(f.Server.EntryUrl.Contains("00:00:00.0000000-04:00"));
    True(f.Server.EntryUrl.Contains("00:00:00.0000000-05:00"));
});
await Check("identity mismatch blocks reads", async () =>
{
    using var f = new Fixture(); f.Server.WrongIdentity = true;
    await Throws<InvalidOperationException>(() => f.Read()); Equal(0, f.Server.WriteCount);
});
await Check("setup discovers token-owner identity with no employee ID", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    var settings = f.Settings with { EmployeeId = "" };
    f.Server.IdentityJson = JsonSerializer.Serialize(new { result = "123.test;person@example.com;Pat Example" });
    var identity = await TimecardApi.DiscoverQuickbaseIdentityAsync(settings, new("TOP-SECRET-TOGGL", "TOP-SECRET-QB"), http);
    Equal("123.test", identity.EmployeeId); Equal("person@example.com", identity.Email); Equal("Pat Example", identity.DisplayName);
    Equal("", settings.EmployeeId); Equal(0, f.Server.WriteCount);
    using var sent = JsonDocument.Parse(f.Server.IdentityRequestJson);
    Equal(settings.TimecardsTable, sent.RootElement.GetProperty("from").GetString());
    Equal("UserToID(User()) & \";\" & UserToEmail(User()) & \";\" & UserToName(User())", sent.RootElement.GetProperty("formula").GetString());
    Equal(2, sent.RootElement.EnumerateObject().Count()); // no supplied email, employee ID, or record ID
    True(!f.Server.IdentityRequestJson.Contains("TOP-SECRET"));
    Equal("QB-USER-TOKEN TOP-SECRET-QB", f.Server.IdentityAuthorization);
    Equal(settings.Realm, f.Server.IdentityRealm); Equal(HttpMethod.Post, f.Server.IdentityMethod);
    Equal("https://api.quickbase.com/v1/formula/run", f.Server.IdentityUrl);
});
await Check("ordinary API constructor still rejects blank employee ID", () =>
{
    using var f = new Fixture();
    try { using var api = new TimecardApi(f.Settings with { EmployeeId = "" }, new("a", "b")); }
    catch (ArgumentException) { return Task.CompletedTask; }
    throw new Exception("Expected employee ID to remain mandatory outside discovery");
});
await Check("discovery ignores stale or malformed manual employee IDs without modifying settings", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    foreach (var stale in new[] { "999.stale", "stale invalid manual value" })
    {
        var settings = f.Settings with { EmployeeId = stale };
        var identity = await TimecardApi.DiscoverQuickbaseIdentityAsync(settings, new("a", "b"), http);
        Equal("123.test", identity.EmployeeId); Equal(stale, settings.EmployeeId);
    }
});
await Check("normal reads still reject a stale valid employee ID", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    using var api = new TimecardApi(f.Settings with { EmployeeId = "999.stale" }, new("a", "b"), http);
    await Throws<InvalidOperationException>(() => api.TestAsync()); Equal(0, f.Server.WriteCount);
});
await Check("discovery rejects another user's email without modifying settings", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    f.Server.IdentityJson = JsonSerializer.Serialize(new { result = "123.test;someoneelse@example.com;Someone Else" });
    var settings = f.Settings with { EmployeeId = "" };
    await Throws<InvalidOperationException>(() => TimecardApi.DiscoverQuickbaseIdentityAsync(settings, new("a", "b"), http));
    Equal("", settings.EmployeeId); Equal(0, f.Server.WriteCount);
});
await Check("discovery rejects empty, anonymous, malformed and duplicate identity results", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    foreach (var json in new[]
    {
        "{}", "[]", "null", "{\"result\":null}", "{\"result\":3}",
        "{\"result\":\";person@example.com;Person\"}",
        "{\"result\":\"1.ckbs;person@example.com;anonymous\"}",
        "{\"result\":\"123.test;person@example.com\"}",
        "{\"result\":\"123.test;not an email;Person\"}",
        "{\"result\":\"123.test;person@example.com;Person\",\"result\":\"456.test;person@example.com;Person\"}",
        "{\"error\":\"TOP-SECRET-QB\"}",
        JsonSerializer.Serialize(new { result = "123.test;person@example.com;" + new string('x', 2048) })
    })
    {
        f.Server.IdentityJson = json;
        try { await TimecardApi.DiscoverQuickbaseIdentityAsync(f.Settings with { EmployeeId = "" }, new("TOP-SECRET-TOGGL", "TOP-SECRET-QB"), http); }
        catch (InvalidOperationException ex) { True(!ex.ToString().Contains("TOP-SECRET", StringComparison.Ordinal)); continue; }
        throw new Exception("Expected invalid identity rejection");
    }
});
await Check("discovery malformed JSON errors never expose token or response", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    f.Server.IdentityJson = "{TOP-SECRET-QB";
    try { await TimecardApi.DiscoverQuickbaseIdentityAsync(f.Settings with { EmployeeId = "" }, new("TOP-SECRET-TOGGL", "TOP-SECRET-QB"), http); }
    catch (InvalidDataException ex) { True(!ex.ToString().Contains("TOP-SECRET", StringComparison.Ordinal)); return; }
    throw new Exception("Expected malformed JSON rejection");
});
await Check("discovery rejects legacy XML and external entities", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    f.Server.IdentityJson = "<!DOCTYPE qdbapi [<!ENTITY xxe SYSTEM 'https://example.invalid/'>]><qdbapi><errcode>0</errcode><user id='123.test'><email>&xxe;</email></user></qdbapi>";
    await Throws<InvalidDataException>(() => TimecardApi.DiscoverQuickbaseIdentityAsync(f.Settings with { EmployeeId = "" }, new("a", "b"), http));
});
await Check("identity HTTP failures explain saved app assignment and never expose response bodies", async () =>
{
    foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
    {
        using var f = new Fixture(); using var http = new HttpClient(f.Server);
        f.Server.IdentityStatus = status; f.Server.IdentityJson = "TOP-SECRET-QB TOP-SECRET-TOGGL private server details";
        try { await TimecardApi.DiscoverQuickbaseIdentityAsync(f.Settings with { EmployeeId = "" }, new("TOP-SECRET-TOGGL", "TOP-SECRET-QB"), http); }
        catch (HttpRequestException ex)
        {
            True(ex.Message.Contains($"HTTP {(int)status}")); True(ex.Message.Contains("save that token page"));
            True(!ex.ToString().Contains("TOP-SECRET")); True(!ex.Message.Contains("private server")); Equal(0, f.Server.WriteCount); continue;
        }
        throw new Exception("Expected identity HTTP rejection");
    }
});
await Check("identity display name safely falls back without losing verified email", async () =>
{
    foreach (var name in new[] { "", new string('x', 257), "bad\nname", "bad\u202ename", "TOP-SECRET-QB", "TOP-SECRET-TOGGL" })
    {
        using var f = new Fixture(); using var http = new HttpClient(f.Server);
        f.Server.IdentityJson = JsonSerializer.Serialize(new { result = "123.test;PERSON@example.com;" + name });
        var identity = await TimecardApi.DiscoverQuickbaseIdentityAsync(f.Settings with { EmployeeId = "" }, new("TOP-SECRET-TOGGL", "TOP-SECRET-QB"), http);
        Equal("123.test", identity.EmployeeId); Equal("PERSON@example.com", identity.Email); Equal(identity.Email, identity.DisplayName);
    }
});
await Check("Toggl can have a different email, with both identities displayed", async () =>
{
    using var f = new Fixture(); f.Server.WrongToggl = true;
    var identity = await f.Api.TestAsync(); True(identity.Contains("other@example.com")); True(identity.Contains("person@example.com"));
});
await Check("overnight entry started yesterday is not silently omitted", async () =>
{
    using var f = new Fixture(); f.Server.Overnight = true;
    await Throws<InvalidOperationException>(() => f.Read()); Equal(0, f.Server.WriteCount);
});
await Check("running timer started several days ago is not silently omitted", async () =>
{
    using var f = new Fixture(); f.Server.OldRunning = true;
    await Throws<InvalidOperationException>(() => f.Read()); Equal(0, f.Server.WriteCount);
});
await Check("multi-day read uses one range request and one running check", async () =>
{
    using var f = new Fixture(); await f.Api.ReadAsync(Fixture.Day.AddDays(-7), Fixture.Day);
    Equal(1, f.Server.EntryQueries); Equal(1, f.Server.CurrentQueries);
});
await Check("shared-agent upgrade preserves identity, preferences and custom links", () =>
{
    using var f = new Fixture();
    var legacy = f.Settings with { CopilotUrl = "https://m365.cloud.microsoft/chat", TimecardsHours = 0.25m, TargetHours = 9m };
    f.Store.SaveSettings(legacy);
    var upgraded = f.Store.LoadSettings();
    Equal(AppSettings.SharedCopilotUrl, upgraded.CopilotUrl);
    Equal(legacy.ProfileKey, upgraded.ProfileKey); Equal(legacy.EmployeeId, upgraded.EmployeeId);
    Equal(legacy.TimecardsHours, upgraded.TimecardsHours); Equal(legacy.TargetHours, upgraded.TargetHours);
    Equal(upgraded, f.Store.LoadSettings());
    True(File.ReadAllText(Path.Combine(f.Store.RootPath, "settings.json")).Contains("titleId="));
    var custom = legacy with { CopilotUrl = "https://m365.cloud.microsoft/chat/?titleId=custom-team-agent" };
    f.Store.SaveSettings(custom); Equal(custom, f.Store.LoadSettings());
    return Task.CompletedTask;
});
await Check("latest revised session wins tied source timestamp", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); f.Store.SaveSession(s);
    File.SetLastWriteTimeUtc(Path.Combine(f.Store.RootPath, "sessions", s.SessionId + ".json"), DateTime.UtcNow.AddMinutes(-1));
    var revised = s with { SessionId = Guid.NewGuid().ToString("N") }; f.Store.SaveSession(revised);
    Equal(revised.SessionId, f.Store.LoadLatestSession(s.Settings.ProfileKey)!.SessionId);
});
await Check("malicious realm is rejected before HTTP", () =>
{
    try { using var api = new TimecardApi(new AppSettings { Realm = "evil.example/", Email = "person@example.com", EmployeeId = "1.test" }, new("a", "b")); throw new Exception("Expected realm rejection"); }
    catch (ArgumentException) { return Task.CompletedTask; }
});
await Check("complete submission journals before every write", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); var rows = Rules.Validate(s, p).Rows;
    Equal(3, rows.Count);
    f.Server.BeforeWrite = () => True(f.Store.LoadReceipts().Single().Rows.Any(r => r.Status == "pending"));
    var receipt = await f.Service.SubmitAsync(s, p, rows);
    Equal("complete", receipt.Status); Equal(3, f.Server.WriteCount); True(receipt.Rows.All(r => r.RecordId > 0));
    True(!File.ReadAllText(Path.Combine(f.Store.RootPath, "settings.json")).Contains("TOP-SECRET"));
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(s, p, rows)); Equal(3, f.Server.WriteCount);
});
await Check("changed Toggl data aborts before write", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.ChangeDescription = true;
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows)); Equal(0, f.Server.WriteCount);
});
await Check("changed task relation aborts before write", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.ChangeTaskCategory = true;
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows)); Equal(0, f.Server.WriteCount);
});
await Check("changed existing cards abort before write", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.Existing.Add(new(990, Fixture.Day, 1, 100, 1, 10, null, "External change"));
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows)); Equal(0, f.Server.WriteCount);
});
await Check("reviewed rows cannot be modified after validation", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); var rows = Rules.Validate(s, p).Rows; rows[0] = rows[0] with { Hours = 3 };
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(s, p, rows)); Equal(0, f.Server.WriteCount);
});
await Check("warnings require acknowledgement and are recorded on receipt", async () =>
{
    using var f = new Fixture(); f.Server.Existing.Add(new(990, Fixture.Day, 7, 100, 1, 10, null, "Earlier work"));
    var s = await f.Read(); var p = f.Proposal(s); var validation = Rules.Validate(s, p);
    True(validation.IsValid); True(validation.Warnings.Count > 0);
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(s, p, validation.Rows)); Equal(0, f.Server.WriteCount);
    var receipt = await f.Service.SubmitAsync(s, p, validation.Rows, warningsAcknowledged: true);
    Equal(validation.Warnings.Count, receipt.ReviewedWarnings.Count); True(receipt.WarningsAcknowledgedAtUtc.HasValue);
    Equal(validation.Warnings.Count, f.Store.LoadReceipts().Single().ReviewedWarnings.Count);
});
await Check("equivalent hours formatting after a create does not stop remaining rows", async () =>
{
    foreach (var scale in new[] { 1, 2, 3 })
    {
        using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s);
        f.Server.ExistingHoursScale = scale;
        var receipt = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
        Equal("complete", receipt.Status); Equal(3, f.Server.WriteCount);
        True(receipt.Rows.All(r => r.Status == "created")); Equal(8m, f.Server.Existing.Sum(r => r.Hours));
    }
});
await Check("equivalent hours formatting in baseline records passes the freshness check", async () =>
{
    using var f = new Fixture(); f.Server.Existing.Add(new(990, Fixture.Day, 0.50m, 100, 44, 10, null, "Earlier internal work"));
    var s = await f.Read(); var p = f.Proposal(s); f.Server.ExistingHoursScale = 3;
    var receipt = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    Equal("complete", receipt.Status); Equal(3, f.Server.WriteCount); Equal(8m, f.Server.Existing.Sum(r => r.Hours));
});
await Check("real field changes after a create halt remaining writes and identify the record", async () =>
{
    var changes = new (string Field, Func<ExistingTimecard, ExistingTimecard> Change)[]
    {
        ("Hours", r => r with { Hours = r.Hours + 0.01m }),
        ("Project", r => r with { Project = r.Project + 1 }),
        ("Task", r => r with { Task = r.Task + 1 }),
        ("Category", r => r with { Category = r.Category + 1 }),
        ("Assignment", r => r with { Assignment = 99 }),
        ("Description", r => r with { Description = "TOP-SECRET-QB changed description" })
    };
    foreach (var (field, change) in changes)
    {
        using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s);
        f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.Existing[0] = change(f.Server.Existing[0]); };
        var receipt = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
        Equal("partial", receipt.Status); Equal(1, f.Server.WriteCount);
        Equal("created", receipt.Rows[0].Status); True(receipt.Rows.Skip(1).All(r => r.Status == "not_sent"));
        True(receipt.Message.Contains("row 2")); True(receipt.Message.Contains("#501")); True(receipt.Message.Contains(field));
        True(!receipt.Message.Contains("TOP-SECRET")); True(!receipt.Message.Contains("changed description"));
        Equal(receipt.Message, f.Store.LoadReceipts().Single().Message);
    }
});
await Check("a missing created record is named and never automatically resent", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s);
    f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.Existing.Clear(); };
    var receipt = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    Equal("partial", receipt.Status); Equal(1, f.Server.WriteCount);
    True(receipt.Message.Contains("#501")); True(receipt.Message.Contains("missing")); Equal("not_sent", receipt.Rows[1].Status);
});
await Check("an external added record halts remaining writes and names that record", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s);
    f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.Existing.Add(new(990, Fixture.Day, 0.25m, 100, 44, 10, null, "Outside edit")); };
    var receipt = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    Equal("partial", receipt.Status); Equal(1, f.Server.WriteCount);
    True(receipt.Message.Contains("#990")); True(receipt.Message.Contains("added outside"));
});
await Check("failed between-row reads preserve a safe HTTP reason without sending the next row", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s);
    f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.ExistingReadStatus = HttpStatusCode.TooManyRequests; };
    var receipt = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    Equal("partial", receipt.Status); Equal(1, f.Server.WriteCount); Equal("not_sent", receipt.Rows[1].Status);
    True(receipt.Message.Contains("HTTP 429")); True(!receipt.Message.Contains("TOP-SECRET"));
});
await Check("a fresh proposal can finish a partial day without replaying its created source", async () =>
{
    using var f = new Fixture();
    f.Server.EntriesResponse = new[]
    {
        new { id = 123456789012L, workspace_id = 55, start = "2026-09-28T13:00:00Z", stop = "2026-09-28T16:00:00Z", duration = 10800, description = "First work", project_name = "Internal", billable = false },
        new { id = 123456789013L, workspace_id = 55, start = "2026-09-28T16:00:00Z", stop = "2026-09-28T18:50:00Z", duration = 10200, description = "Second work", project_name = "Internal", billable = false }
    };
    var s = await f.Read(); var p = f.Proposal(s);
    p.Rows.Add(new() { Date = Fixture.Day, SourceEntryIds = [123456789013], Project = 100, Task = 44, Category = 10, Description = "Second work" });
    f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.ExistingReadStatus = HttpStatusCode.ServiceUnavailable; };
    var partial = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    Equal("partial", partial.Status); Equal(1, f.Server.WriteCount); Equal(3m, partial.Rows[0].Row.Hours);
    f.Server.BeforeExistingRead = null; f.Server.ExistingReadStatus = HttpStatusCode.OK; f.Server.ExistingHoursScale = 1;
    var fresh = await f.Read();
    var remaining = new ProposalEnvelope { SessionId = fresh.SessionId, EmployeeId = f.Settings.EmployeeId,
        Rows = [p.Rows[1]],
        AlreadyRecorded = [new() { SourceEntryId = p.Rows[0].SourceEntryIds[0], ExistingRecordId = partial.Rows[0].RecordId!.Value }] };
    var validation = Rules.Validate(fresh, remaining); True(validation.IsValid); Equal(3, validation.Rows.Count);
    Equal(2.83m, validation.Rows.Single(r => r.Kind == "work").Hours);
    Equal(0.17m, validation.Rows.Single(r => r.Kind == "timecards").Hours);
    Equal(2m, validation.Rows.Single(r => r.Kind == "misc_internal").Hours);
    var receipt = await f.Service.SubmitAsync(fresh, remaining, validation.Rows, warningsAcknowledged: true);
    Equal("complete", receipt.Status); Equal(4, f.Server.WriteCount);
    Equal(1, f.Server.Existing.Count(r => r.RecordId == 501)); Equal(8m, f.Server.Existing.Sum(r => r.Hours));
});
await Check("timeout becomes unknown, halts rows, and blocks next session", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.WriteMode = "timeout";
    var r = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    Equal("unknown", r.Status); Equal("unknown", r.Rows[0].Status); Equal("not_sent", r.Rows[1].Status); Equal(1, f.Server.WriteCount);
    var second = s with { SessionId = Guid.NewGuid().ToString("N") }; var p2 = f.Proposal(second);
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(second, p2, Rules.Validate(second, p2).Rows)); Equal(1, f.Server.WriteCount);
});
await Check("malformed success becomes unknown", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.WriteMode = "malformed";
    var r = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows); Equal("unknown", r.Status); Equal(1, f.Server.WriteCount);
});
await Check("line error becomes known failure and stops dependent rows", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.WriteMode = "line-error";
    var r = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows); Equal("failed", r.Rows[0].Status); Equal("partial", r.Status); Equal(1, f.Server.WriteCount);
});
await Check("fractional created IDs stay unknown and never become a confirmed record", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.WriteMode = "fractional-id";
    var receipt = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    Equal("unknown", receipt.Status); Equal("unknown", receipt.Rows[0].Status); Equal<int?>(null, receipt.Rows[0].RecordId); Equal(1, f.Server.WriteCount);
});
await Check("reconcile timeout with one exact new record", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.WriteMode = "commit-timeout";
    var r = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows); Equal("unknown", r.Status);
    r = await f.Service.ReconcileAsync(s, r); Equal("created", r.Rows[0].Status); Equal(1, f.Server.WriteCount); True(!r.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase));
});
await Check("reconcile absence stays unknown and never replays", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.WriteMode = "timeout";
    var r = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    r = await f.Service.ReconcileAsync(s, r); Equal("unknown", r.Rows[0].Status); Equal(1, f.Server.WriteCount);
});
await Check("reconcile multiple matching records stays blocked", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); f.Server.WriteMode = "commit-timeout";
    var r = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    f.Server.Existing.Add(f.Server.Existing[0] with { RecordId = 888 });
    r = await f.Service.ReconcileAsync(s, r); Equal("unknown", r.Rows[0].Status); Equal(1, f.Server.WriteCount);
});
await Check("pending from interrupted process is reconciled as uncertain", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); f.Store.SaveSession(s);
    var rows = Rules.Validate(s, f.Proposal(s)).Rows;
    var r = new SubmissionReceipt { SessionId = s.SessionId, ProfileKey = s.Settings.ProfileKey, Rows = [new() { Row = rows[0], Status = "pending" }] }; f.Store.SaveReceipt(r);
    r = await f.Service.ReconcileAsync(s, r); Equal("unknown", r.Rows[0].Status); Equal(0, f.Server.WriteCount);
});
await Check("receipt storage failure prevents the first side effect", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s);
    File.WriteAllText(Path.Combine(f.Store.RootPath, "receipts"), "blocks directory");
    await Throws<IOException>(() => f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows)); Equal(0, f.Server.WriteCount);
});
await Check("corrupt history fails closed", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s);
    Directory.CreateDirectory(Path.Combine(f.Store.RootPath, "receipts")); File.WriteAllText(Path.Combine(f.Store.RootPath, "receipts", "bad.json"), "garbled");
    await Throws<InvalidDataException>(() => f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows)); Equal(0, f.Server.WriteCount);
});
await Check("same Toggl source cannot be replayed in another session", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s); await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    var other = s with { SessionId = Guid.NewGuid().ToString("N") }; var p2 = f.Proposal(other);
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(other, p2, Rules.Validate(other, p2).Rows)); Equal(3, f.Server.WriteCount);
});
await Check("local cross-process file lock excludes a second writer", async () =>
{
    using var f = new Fixture(); var s = await f.Read(); var p = f.Proposal(s);
    using var held = new FileStream(Path.Combine(f.Store.RootPath, "submission.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows)); Equal(0, f.Server.WriteCount);
});
await Check("all Quickbase pages are consumed even if server returns short pages", async () =>
{
    using var f = new Fixture(); f.Server.CategoryPageSize = 1;
    var s = await f.Read(); Equal(2, s.Reference.Categories.Count); Equal(2, f.Server.CategoryQueries);
});
await Check("changing pagination total is rejected", async () =>
{
    using var f = new Fixture(); f.Server.CategoryPageSize = 1; f.Server.ChangingTotal = true;
    await Throws<InvalidDataException>(() => f.Read());
});
await Check("pagination cap rejects partial data", async () =>
{
    using var f = new Fixture(); f.Server.OverCap = true;
    await Throws<InvalidDataException>(() => f.Read());
});
await Check("Toggl cap rejects potentially truncated day", async () =>
{
    using var f = new Fixture(); f.Server.EntryCount = 1000;
    await Throws<InvalidDataException>(() => f.Read());
});
await Check("intentional deletion can be recovered and rewritten only through a fresh proposal", async () =>
{
    using var f = new Fixture(); var (original, receipt) = await f.CreatedDay();
    var finished = receipt.FinishedAtUtc; var message = receipt.Message;
    f.Server.Existing.Clear();
    var stale = await f.Read();
    var missing = await f.Service.FindMissingCreatedEntriesAsync(original, receipt);
    Equal(1, missing.Count); Equal<int?>(501, missing[0].RecordId); Equal(3, f.Server.WriteCount);
    var recovered = await f.Service.ConfirmDeletedEntriesAsync(original, receipt, [missing[0].Row.RowId], true);
    Equal("reopened", recovered.Status); Equal(finished, recovered.FinishedAtUtc); Equal(message, recovered.Message);
    Equal("created", recovered.Rows[0].Status); Equal<int?>(501, recovered.Rows[0].RecordId);
    True(recovered.Rows[0].DeletionConfirmedAtUtc.HasValue); Equal(3, f.Server.WriteCount);
    True(f.Store.LoadReceipts().Single().Rows[0].DeletionConfirmedAtUtc.HasValue);
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(stale, f.Proposal(stale), Rules.Validate(stale, f.Proposal(stale)).Rows));
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(original, f.Proposal(original), Rules.Validate(original, f.Proposal(original)).Rows));
    var fresh = await f.Read(); var p = f.Proposal(fresh);
    var replacement = await f.Service.SubmitAsync(fresh, p, Rules.Validate(fresh, p).Rows);
    Equal("complete", replacement.Status); Equal(6, f.Server.WriteCount); Equal(8m, f.Server.Existing.Sum(r => r.Hours));
    Equal(2, f.Store.LoadReceipts().Count);
    var next = await f.Read(); var np = f.Proposal(next);
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(next, np, Rules.Validate(next, np).Rows, warningsAcknowledged: true));
    Equal(6, f.Server.WriteCount);
});
await Check("deleted first row of a partial day can be replaced with corrected source hours", async () =>
{
    using var f = new Fixture();
    object Entries(int seconds) => new[]
    {
        new { id = 123456789012L, workspace_id = 55, start = "2026-09-28T13:00:00Z", stop = DateTimeOffset.Parse("2026-09-28T13:00:00Z").AddSeconds(seconds).ToString("O"), duration = seconds, description = "First work", project_name = "Internal", billable = false },
        new { id = 123456789013L, workspace_id = 55, start = "2026-09-28T17:00:00Z", stop = "2026-09-28T19:50:00Z", duration = 10200, description = "Second work", project_name = "Internal", billable = false }
    };
    f.Server.EntriesResponse = Entries(10800);
    var s = await f.Read(); var p = f.Proposal(s);
    p.Rows.Add(new() { Date = Fixture.Day, SourceEntryIds = [123456789013], Project = 100, Task = 44, Category = 10, Description = "Second work" });
    f.Server.BeforeExistingRead = () => { if (f.Server.WriteCount == 1) f.Server.ExistingReadStatus = HttpStatusCode.ServiceUnavailable; };
    var partial = await f.Service.SubmitAsync(s, p, Rules.Validate(s, p).Rows);
    Equal("partial", partial.Status); Equal(1, f.Server.WriteCount);
    f.Server.BeforeExistingRead = null; f.Server.ExistingReadStatus = HttpStatusCode.OK;
    f.Server.Existing.Clear(); f.Server.EntriesResponse = Entries(11700);
    await f.Service.ConfirmDeletedEntriesAsync(s, partial, [partial.Rows[0].Row.RowId], true);
    var fresh = await f.Read(); var replacement = p with { SessionId = fresh.SessionId };
    var validation = Rules.Validate(fresh, replacement); True(validation.IsValid); Equal(4, validation.Rows.Count);
    True(validation.Rows.Select(r => r.Hours).SequenceEqual(new[] { 3.25m, 2.83m, 0.17m, 1.75m }));
    var result = await f.Service.SubmitAsync(fresh, replacement, validation.Rows);
    Equal("complete", result.Status); Equal(5, f.Server.WriteCount); Equal(8m, f.Server.Existing.Sum(r => r.Hours));
    var original = f.Store.LoadReceipts().Single(r => r.SubmissionId == partial.SubmissionId);
    Equal(3m, original.Rows[0].Row.Hours); True(original.Rows.Skip(1).All(r => r.Status == "not_sent"));
});
await Check("deleted record remains blocked until explicitly confirmed", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay(); f.Server.Existing.Clear();
    var missing = await f.Service.FindMissingCreatedEntriesAsync(s, r); Equal(1, missing.Count);
    var fresh = await f.Read(); var p = f.Proposal(fresh);
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(fresh, p, Rules.Validate(fresh, p).Rows));
    True(f.Store.LoadReceipts().Single().Rows.All(row => row.DeletionConfirmedAtUtc is null)); Equal(3, f.Server.WriteCount);
});
await Check("recovery checks record IDs without date or employee restrictions", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay();
    f.Server.Existing[0] = f.Server.Existing[0] with { Date = Fixture.Day.AddDays(1), Description = "Moved record" };
    Equal(0, (await f.Service.FindMissingCreatedEntriesAsync(s, r)).Count);
    Equal("{3.EX.'501'}", f.Server.RecordIdQueryWhere);
    await Throws<InvalidOperationException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true));
    Equal(3, f.Server.WriteCount);
});
await Check("record restored between discovery and confirmation blocks recovery", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay(); var old = f.Server.Existing[0]; f.Server.Existing.Clear();
    Equal(1, (await f.Service.FindMissingCreatedEntriesAsync(s, r)).Count); f.Server.Existing.Add(old);
    await Throws<InvalidOperationException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true));
    True(f.Store.LoadReceipts().Single().Rows[0].DeletionConfirmedAtUtc is null); Equal(3, f.Server.WriteCount);
});
await Check("restored record blocks a later rewrite even outside the selected date", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay(); var old = f.Server.Existing[0]; f.Server.Existing.Clear();
    await f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true);
    var fresh = await f.Read(); var p = f.Proposal(fresh);
    // Visible only to the global ID query, representing a moved/reassigned record.
    f.Server.RecordIdRows = [FakeApi.Row((3, old.RecordId))];
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(fresh, p, Rules.Validate(fresh, p).Rows));
    Equal(3, f.Server.WriteCount); Equal(1, f.Store.LoadReceipts().Count);
});
await Check("restored record between replacement writes halts remaining rows", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay(); f.Server.Existing.Clear();
    await f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true);
    var fresh = await f.Read(); var p = f.Proposal(fresh);
    f.Server.BeforeRecordIdRead = () => { if (f.Server.WriteCount == 4) f.Server.RecordIdRows = [FakeApi.Row((3, 501))]; };
    var result = await f.Service.SubmitAsync(fresh, p, Rules.Validate(fresh, p).Rows);
    Equal("partial", result.Status); Equal(4, f.Server.WriteCount); True(result.Message.Contains("#501"));
    True(result.Rows.Skip(1).All(row => row.Status == "not_sent"));
});
await Check("empty unconfirmed duplicate foreign and automatic selections cannot release sources", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay(); f.Server.Existing.Clear();
    var id = r.Rows[0].Row.RowId;
    await Throws<InvalidOperationException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [id], false));
    foreach (string[] selection in new string[][] { [], [id, id], ["foreign-row"], [r.Rows[1].Row.RowId] })
        await Throws<InvalidOperationException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, selection, true));
    True(f.Store.LoadReceipts().Single().Rows.All(row => row.DeletionConfirmedAtUtc is null)); Equal(3, f.Server.WriteCount);
});
await Check("unknown and pending receipts cannot be released with caller-edited statuses", async () =>
{
    foreach (var status in new[] { "pending", "unknown" })
    {
        using var f = new Fixture(); var (s, r) = await f.CreatedDay(); f.Server.Existing.Clear();
        r.Rows[1].Status = status; f.Store.SaveReceipt(r); r.Rows[1].Status = "created";
        await Throws<InvalidOperationException>(() => f.Service.FindMissingCreatedEntriesAsync(s, r));
        await Throws<InvalidOperationException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true));
        True(f.Store.LoadReceipts().Single().Rows[0].DeletionConfirmedAtUtc is null);
    }
});
await Check("recovery rejects wrong account session demo and missing baseline", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay(); f.Server.Existing.Clear();
    foreach (var altered in new[] { s with { Demo = true }, s with { SessionId = "another" }, s with { Settings = s.Settings with { EmployeeId = "other.user" } } })
        await Throws<InvalidOperationException>(() => f.Service.FindMissingCreatedEntriesAsync(altered, r));
    f.Store.SaveSession(s with { Demo = true });
    await Throws<InvalidOperationException>(() => f.Service.FindMissingCreatedEntriesAsync(s, r));
    File.Delete(Path.Combine(f.Store.RootPath, "sessions", s.SessionId + ".json"));
    await Throws<InvalidOperationException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true));
    Equal(3, f.Server.WriteCount);
});
await Check("recovery query failures never confirm deletion", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
    {
        using var f = new Fixture(); var (s, r) = await f.CreatedDay(); f.Server.Existing.Clear();
        f.Server.ExistingReadStatus = status;
        await Throws<HttpRequestException>(() => f.Service.FindMissingCreatedEntriesAsync(s, r));
        await Throws<HttpRequestException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true));
        True(f.Store.LoadReceipts().Single().Rows[0].DeletionConfirmedAtUtc is null); Equal(3, f.Server.WriteCount);
    }
});
await Check("unexpected malformed and incomplete recovery query results fail closed", async () =>
{
    foreach (var mode in new[] { "unexpected", "malformed", "incomplete" })
    {
        using var f = new Fixture(); var (s, r) = await f.CreatedDay(); f.Server.Existing.Clear();
        f.Server.RecordIdRows = mode == "unexpected" ? [FakeApi.Row((3, 999))] : mode == "malformed" ? [FakeApi.Row((3, 501.5m))] : [];
        if (mode == "incomplete") f.Server.RecordIdDeclaredTotal = 1;
        if (mode == "malformed") await Throws<QuickbaseDataException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true));
        else await Throws<InvalidDataException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true));
        True(f.Store.LoadReceipts().Single().Rows[0].DeletionConfirmedAtUtc is null); Equal(3, f.Server.WriteCount);
    }
});
await Check("identity mismatch cannot confirm deletion", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay(); f.Server.Existing.Clear(); f.Server.WrongIdentity = true;
    await Throws<InvalidOperationException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true));
    True(f.Store.LoadReceipts().Single().Rows[0].DeletionConfirmedAtUtc is null); Equal(3, f.Server.WriteCount);
});
await Check("journal replacement failure leaves original duplicate protection intact", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay(); f.Server.Existing.Clear();
    var path = Path.Combine(f.Store.RootPath, "receipts", r.SubmissionId + ".json");
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        await Throws<UnauthorizedAccessException>(() => f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true));
    True(f.Store.LoadReceipts().Single().Rows[0].DeletionConfirmedAtUtc is null);
    var fresh = await f.Read(); var p = f.Proposal(fresh);
    await Throws<InvalidOperationException>(() => f.Service.SubmitAsync(fresh, p, Rules.Validate(fresh, p).Rows)); Equal(3, f.Server.WriteCount);
});
await Check("legacy receipts load without a deletion field and audit survives serialization", async () =>
{
    using var f = new Fixture(); var (s, r) = await f.CreatedDay();
    True(!JsonSerializer.Serialize(r, JsonDefaults.Options).Contains("deletion_confirmed_at_utc"));
    Equal(3, f.Store.LoadReceipts().Single().Rows.Count); f.Server.Existing.Clear();
    await f.Service.ConfirmDeletedEntriesAsync(s, r, [r.Rows[0].Row.RowId], true);
    var saved = f.Store.LoadReceipts().Single(); True(saved.Rows[0].DeletionConfirmedAtUtc.HasValue);
    True(JsonSerializer.Serialize(saved, JsonDefaults.Options).Contains("deletion_confirmed_at_utc"));
});
Console.WriteLine($"{passed} API/storage/submission tests passed; {failures.Count} failed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name + ": " + ex); }
}
static void True(bool value) { if (!value) throw new Exception("Expected true"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
static async Task<QuickbaseDataException> DataError(Func<Task> action)
{
    try { await action(); } catch (QuickbaseDataException error) { return error; }
    throw new Exception("Expected contextual Quickbase error");
}

sealed class Fixture : IDisposable
{
    public static readonly DateOnly Day = new(2026, 9, 28);
    public readonly AppSettings Settings = new() { Email = "person@example.com", EmployeeId = "123.test", InternalProjectId = 100 };
    public readonly FakeApi Server = new();
    public readonly SessionStore Store;
    public readonly TimecardApi Api;
    public readonly SubmissionService Service;
    public Fixture()
    {
        Store = new SessionStore(Path.Combine(Path.GetTempPath(), "timekeeper-api-tests", Guid.NewGuid().ToString("N")));
        Store.SaveSettings(Settings);
        Api = new TimecardApi(Settings, new Credentials("TOP-SECRET-TOGGL", "TOP-SECRET-QB"), new HttpClient(Server));
        Service = new SubmissionService(Api, Store);
    }
    public Task<ReadSession> Read() => Api.ReadAsync(Day, Day);
    public async Task<(ReadSession Session, SubmissionReceipt Receipt)> CreatedDay()
    {
        var session = await Read(); var proposal = Proposal(session);
        var receipt = await Service.SubmitAsync(session, proposal, Rules.Validate(session, proposal).Rows);
        return (session, receipt);
    }
    public ProposalEnvelope Proposal(ReadSession s) => new() { SessionId = s.SessionId, EmployeeId = Settings.EmployeeId, Rows = [new() { Date = Day, SourceEntryIds = [123456789012], Project = 100, Task = 1, Category = 10, Description = "Work completed" }] };
    public void Dispose() { Api.Dispose(); Server.Dispose(); Directory.Delete(Store.RootPath, true); }
}

sealed class FakeApi : HttpMessageHandler
{
    public Dictionary<string, List<object>> RowsByTable = [];
    public string EntryUrl = "";
    public string? IdentityJson;
    public object? EntriesResponse;
    public string IdentityRequestJson = "", IdentityAuthorization = "", IdentityRealm = "", IdentityUrl = "";
    public HttpMethod? IdentityMethod;
    public HttpStatusCode IdentityStatus = HttpStatusCode.OK;
    public string WriteMode = "success";
    public bool WrongIdentity, WrongToggl, ChangeDescription, ChangeTaskCategory, ChangingTotal, OverCap, Overnight, OldRunning;
    public int WriteCount, CategoryQueries, CategoryPageSize = 1000, EntryCount = 1, EntryQueries, CurrentQueries;
    public List<ExistingTimecard> Existing = [];
    public Action? BeforeWrite, BeforeExistingRead;
    public Action? BeforeRecordIdRead;
    public string RecordIdQueryWhere = "";
    public List<object>? RecordIdRows;
    public int? RecordIdDeclaredTotal;
    public int? ExistingHoursScale;
    public HttpStatusCode ExistingReadStatus = HttpStatusCode.OK;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        if (url.AbsolutePath == "/v1/formula/run")
        {
            IdentityRequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            IdentityAuthorization = request.Headers.Authorization?.ToString() ?? "";
            IdentityRealm = request.Headers.GetValues("QB-Realm-Hostname").Single();
            IdentityMethod = request.Method; IdentityUrl = url.AbsoluteUri;
            return new(IdentityStatus) { Content = new StringContent(IdentityJson ?? JsonSerializer.Serialize(new { result = $"{(WrongIdentity ? "999.wrong" : "123.test")};person@example.com;Pat Example" }), Encoding.UTF8, "application/json") };
        }
        if (url.AbsolutePath.EndsWith("/me")) return Json(new { email = WrongToggl ? "other@example.com" : "person@example.com" });
        if (url.AbsolutePath.EndsWith("/time_entries/current")) { CurrentQueries++; return OldRunning ? Json(new { start = "2026-09-24T13:00:00Z", duration = -1 }) : Text("null"); }
        if (url.AbsolutePath.EndsWith("/time_entries"))
        {
            EntryQueries++;
            EntryUrl = Uri.UnescapeDataString(url.ToString());
            if (EntriesResponse is not null) return Json(EntriesResponse);
            return Json(Enumerable.Range(0, EntryCount).Select(n => new { id = 123456789012L + n, workspace_id = 55, start = Overnight ? "2026-09-28T03:00:00Z" : "2026-09-28T13:00:00Z", stop = "2026-09-28T20:00:00Z", duration = 25200, description = ChangeDescription ? "Edited" : "Work", project_name = "Internal", billable = true }).ToArray());
        }
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (url.AbsolutePath.EndsWith("/records/query"))
        {
            var root = body.RootElement; var table = root.GetProperty("from").GetString(); var skip = root.GetProperty("options").GetProperty("skip").GetInt32();
            if (table == "bd3bsxtbp" && body.RootElement.GetProperty("select").GetArrayLength() == 1)
            {
                BeforeRecordIdRead?.Invoke();
                if (ExistingReadStatus != HttpStatusCode.OK) return new(ExistingReadStatus) { Content = new StringContent("TOP-SECRET") };
                RecordIdQueryWhere = body.RootElement.GetProperty("where").GetString()!;
                var requested = System.Text.RegularExpressions.Regex.Matches(RecordIdQueryWhere, @"\{3\.EX\.'(\d+)'\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
                var idRows = RecordIdRows ?? Existing.Where(e => requested.Contains(e.RecordId)).Select(e => Row((3, e.RecordId))).ToList();
                return Json(new { data = idRows.Skip(skip).Take(1000).ToArray(), metadata = new { totalRecords = RecordIdDeclaredTotal ?? idRows.Count } });
            }
            if (table == "bd3bsxtbp")
            {
                BeforeExistingRead?.Invoke();
                if (ExistingReadStatus != HttpStatusCode.OK) return new(ExistingReadStatus) { Content = new StringContent("TOP-SECRET-QB private response") };
            }
            object ReadHours(decimal hours) => ExistingHoursScale is int scale
                ? JsonSerializer.Deserialize<JsonElement>(hours.ToString("F" + Math.Max(scale, (decimal.GetBits(hours)[3] >> 16) & 0xff), System.Globalization.CultureInfo.InvariantCulture))
                : hours;
            List<object> rows = table switch
            {
                "beb9dvs6p" => [Row((3, 10), (6, "Internal")), Row((3, 20), (6, "Billable"))],
                "bd3bsxtbn" => [Row((3, 1), (7, "Work"), (11, true), (17, ChangeTaskCategory ? 20 : 10)), Row((3, 44), (7, "Internal work"), (11, true), (17, 10))],
                "bd3bsxtbj" => [Row((3, 100), (6, "Internal"))],
                "biqs87fvg" => [],
                "bd3bsxtbp" => Existing.Select(e => Row((3, e.RecordId), (7, e.Date.ToString("yyyy-MM-dd")), (10, ReadHours(e.Hours)), (11, e.Project), (16, e.Task), (22, e.Category), (24, new { id = "123.test", email = "person@example.com" }), (19, e.Description), (73, e.Assignment))).ToList(),
                _ => throw new Exception("Unexpected table")
            };
            if (RowsByTable.TryGetValue(table!, out var overrideRows)) rows = overrideRows;
            if (table == "beb9dvs6p") CategoryQueries++;
            var total = OverCap ? 20001 : rows.Count + (ChangingTotal && skip > 0 ? 1 : 0);
            return Json(new { data = rows.Skip(skip).Take(table == "beb9dvs6p" ? CategoryPageSize : 1000).ToArray(), metadata = new { totalRecords = total } });
        }
        if (url.AbsolutePath.EndsWith("/records"))
        {
            BeforeWrite?.Invoke(); WriteCount++;
            if (WriteMode is "commit-timeout" or "success")
            {
                var row = body.RootElement.GetProperty("data")[0];
                JsonElement Value(string id) => row.GetProperty(id).GetProperty("value");
                Existing.Add(new(500 + WriteCount, DateOnly.Parse(Value("7").GetString()!), Value("10").GetDecimal(), Value("11").GetInt32(), Value("16").GetInt32(), Value("22").GetInt32(), row.TryGetProperty("73", out var assignment) ? assignment.GetProperty("value").GetInt32() : null, Value("19").GetString()!));
            }
            if (WriteMode is "timeout" or "commit-timeout") throw new TaskCanceledException("Sensitive transport data must not escape");
            if (WriteMode == "malformed") return Text("invalid-json");
            if (WriteMode == "fractional-id") return Json(new { metadata = new { createdRecordIds = new[] { 501.5m } } });
            if (WriteMode == "line-error") return Json(new { metadata = new { createdRecordIds = Array.Empty<int>(), lineErrors = new Dictionary<string, string[]> { ["1"] = ["Rejected"] } } });
            return Json(new { metadata = new { createdRecordIds = new[] { 500 + WriteCount }, lineErrors = new Dictionary<string, string[]>() } });
        }
        throw new Exception("Unexpected mock request: " + url.AbsolutePath);
    }
    public static object Row(params (int Id, object? Value)[] cells) => cells.ToDictionary(c => c.Id.ToString(), c => new { value = c.Value });
    private static HttpResponseMessage Json(object value) => Text(JsonSerializer.Serialize(value));
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
}
