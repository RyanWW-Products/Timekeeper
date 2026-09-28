using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Timekeeper.Core;

var failures = new List<string>();
var passed = 0;
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
    f.Server.IdentityXml = "<qdbapi><errcode>0</errcode><user id='123.test'><email>person@example.com</email><firstName>Pat</firstName><lastName>Example</lastName></user></qdbapi>";
    var identity = await TimecardApi.DiscoverQuickbaseIdentityAsync(settings, new("TOP-SECRET-TOGGL", "TOP-SECRET-QB"), http);
    Equal("123.test", identity.EmployeeId); Equal("person@example.com", identity.Email); Equal("Pat Example", identity.DisplayName);
    Equal("", settings.EmployeeId); Equal(0, f.Server.WriteCount);
    var sent = XElement.Parse(f.Server.IdentityRequestXml);
    True(sent.Element("email") is null); Equal("TOP-SECRET-QB", sent.Element("usertoken")!.Value);
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
    f.Server.IdentityXml = "<qdbapi><errcode>0</errcode><user id='123.test'><email>someoneelse@example.com</email></user></qdbapi>";
    var settings = f.Settings with { EmployeeId = "" };
    await Throws<InvalidOperationException>(() => TimecardApi.DiscoverQuickbaseIdentityAsync(settings, new("a", "b"), http));
    Equal("", settings.EmployeeId); Equal(0, f.Server.WriteCount);
});
await Check("discovery rejects empty, anonymous, malformed and duplicate identities", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    foreach (var xml in new[]
    {
        "<qdbapi><errcode>0</errcode><user id=''><email>person@example.com</email></user></qdbapi>",
        "<qdbapi><errcode>0</errcode><user id='1.ckbs'><email>person@example.com</email></user></qdbapi>",
        "<qdbapi><errcode>0</errcode><user id='123.test'><email>person@example.com</email><login>anonymous</login></user></qdbapi>",
        "<qdbapi><errcode>0</errcode><user id='123.test'><email>not an email</email></user></qdbapi>",
        "<qdbapi><errcode>0</errcode><user id='123.test'><email>person@example.com</email></user><user id='456.test'><email>person@example.com</email></user></qdbapi>",
        "<qdbapi><errcode>1</errcode><errtext>TOP-SECRET-QB</errtext></qdbapi>"
    })
    {
        f.Server.IdentityXml = xml;
        try { await TimecardApi.DiscoverQuickbaseIdentityAsync(f.Settings with { EmployeeId = "" }, new("TOP-SECRET-TOGGL", "TOP-SECRET-QB"), http); }
        catch (InvalidOperationException ex) { True(!ex.ToString().Contains("TOP-SECRET", StringComparison.Ordinal)); continue; }
        throw new Exception("Expected invalid identity rejection");
    }
});
await Check("discovery malformed XML errors never expose token or response", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    f.Server.IdentityXml = "<qdbapi><TOP-SECRET-QB";
    try { await TimecardApi.DiscoverQuickbaseIdentityAsync(f.Settings with { EmployeeId = "" }, new("TOP-SECRET-TOGGL", "TOP-SECRET-QB"), http); }
    catch (InvalidDataException ex) { True(!ex.ToString().Contains("TOP-SECRET", StringComparison.Ordinal)); return; }
    throw new Exception("Expected malformed XML rejection");
});
await Check("discovery rejects XML external entities", async () =>
{
    using var f = new Fixture(); using var http = new HttpClient(f.Server);
    f.Server.IdentityXml = "<!DOCTYPE qdbapi [<!ENTITY xxe SYSTEM 'https://example.invalid/'>]><qdbapi><errcode>0</errcode><user id='123.test'><email>&xxe;</email></user></qdbapi>";
    await Throws<InvalidDataException>(() => TimecardApi.DiscoverQuickbaseIdentityAsync(f.Settings with { EmployeeId = "" }, new("a", "b"), http));
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
    public ProposalEnvelope Proposal(ReadSession s) => new() { SessionId = s.SessionId, EmployeeId = Settings.EmployeeId, Rows = [new() { Date = Day, SourceEntryIds = [123456789012], Project = 100, Task = 1, Category = 10, Description = "Work completed" }] };
    public void Dispose() { Api.Dispose(); Server.Dispose(); Directory.Delete(Store.RootPath, true); }
}

sealed class FakeApi : HttpMessageHandler
{
    public string EntryUrl = "";
    public string? IdentityXml;
    public string IdentityRequestXml = "";
    public string WriteMode = "success";
    public bool WrongIdentity, WrongToggl, ChangeDescription, ChangeTaskCategory, ChangingTotal, OverCap, Overnight, OldRunning;
    public int WriteCount, CategoryQueries, CategoryPageSize = 1000, EntryCount = 1, EntryQueries, CurrentQueries;
    public List<ExistingTimecard> Existing = [];
    public Action? BeforeWrite;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        if (url.AbsolutePath == "/db/main")
        {
            IdentityRequestXml = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Text(IdentityXml ?? $"<qdbapi><errcode>0</errcode><user id='{(WrongIdentity ? "wrong" : "123.test")}'><email>person@example.com</email></user></qdbapi>");
        }
        if (url.AbsolutePath.EndsWith("/me")) return Json(new { email = WrongToggl ? "other@example.com" : "person@example.com" });
        if (url.AbsolutePath.EndsWith("/time_entries/current")) { CurrentQueries++; return OldRunning ? Json(new { start = "2026-09-24T13:00:00Z", duration = -1 }) : Text("null"); }
        if (url.AbsolutePath.EndsWith("/time_entries"))
        {
            EntryQueries++;
            EntryUrl = Uri.UnescapeDataString(url.ToString());
            return Json(Enumerable.Range(0, EntryCount).Select(n => new { id = 123456789012L + n, workspace_id = 55, start = Overnight ? "2026-09-28T03:00:00Z" : "2026-09-28T13:00:00Z", stop = "2026-09-28T20:00:00Z", duration = 25200, description = ChangeDescription ? "Edited" : "Work", project_name = "Internal", billable = true }).ToArray());
        }
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (url.AbsolutePath.EndsWith("/records/query"))
        {
            var root = body.RootElement; var table = root.GetProperty("from").GetString(); var skip = root.GetProperty("options").GetProperty("skip").GetInt32();
            List<object> rows = table switch
            {
                "beb9dvs6p" => [Row((3, 10), (6, "Internal")), Row((3, 20), (6, "Billable"))],
                "bd3bsxtbn" => [Row((3, 1), (7, "Work"), (11, true), (17, ChangeTaskCategory ? 20 : 10)), Row((3, 44), (7, "Internal work"), (11, true), (17, 10))],
                "bd3bsxtbj" => [Row((3, 100), (6, "Internal"))],
                "biqs87fvg" => [],
                "bd3bsxtbp" => Existing.Select(e => Row((3, e.RecordId), (7, e.Date.ToString("yyyy-MM-dd")), (10, e.Hours), (11, e.Project), (16, e.Task), (22, e.Category), (24, new { id = "123.test", email = "person@example.com" }), (19, e.Description), (73, e.Assignment))).ToList(),
                _ => throw new Exception("Unexpected table")
            };
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
            if (WriteMode == "line-error") return Json(new { metadata = new { createdRecordIds = Array.Empty<int>(), lineErrors = new Dictionary<string, string[]> { ["1"] = ["Rejected"] } } });
            return Json(new { metadata = new { createdRecordIds = new[] { 500 + WriteCount }, lineErrors = new Dictionary<string, string[]>() } });
        }
        throw new Exception("Unexpected mock request: " + url.AbsolutePath);
    }
    private static object Row(params (int Id, object? Value)[] cells) => cells.ToDictionary(c => c.Id.ToString(), c => new { value = c.Value });
    private static HttpResponseMessage Json(object value) => Text(JsonSerializer.Serialize(value));
    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
}
