using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Timekeeper.Core;

// Developer-only opt-in release smoke check. Reads a process-scoped token and
// verifies the real release download; it never launches the downloaded file.
if (args.Length == 2 && args[0] == "--release-smoke")
{
    using var client = new UpdateClient(Environment.GetEnvironmentVariable("TIMEKEEPER_UPDATE_TEST_TOKEN") ?? "");
    var release = await client.CheckAsync(new Version(0, 0, 0)) ?? throw new Exception("No release found");
    string path = await client.DownloadAsync(release, args[1]);
    Console.WriteLine($"PASS: release {release.Tag} downloaded and SHA-256 verified ({new FileInfo(path).Length} bytes). Installer was not launched.");
    return 0;
}

int passed = 0, failed = 0;
await Check("newer stable release selects exact Windows asset", async () =>
{
    using var fixture = new Fixture(); var release = await fixture.Check();
    Equal(new Version(0, 3, 0), release!.Version);
    Equal("Timekeeper-Setup-0.3.0-win-x64.exe", release.FileName);
});
await Check("equal version including assembly revision does not update", async () =>
{
    using var fixture = new Fixture(); Equal<AppRelease?>(null, await fixture.Client.CheckAsync(new Version(0, 3, 0, 0)));
});
await Check("older release does not downgrade", async () =>
{
    using var fixture = new Fixture(); Equal<AppRelease?>(null, await fixture.Client.CheckAsync(new Version(1, 0, 0)));
});
await Check("prerelease is rejected", async () => { using var f = new Fixture { Prerelease = true }; await Reject(() => f.Check()); });
await Check("draft is rejected", async () => { using var f = new Fixture { Draft = true }; await Reject(() => f.Check()); });
await Check("version traversal rejected", async () => { using var f = new Fixture { Tag = "v../../file" }; await Reject(() => f.Check()); });
await Check("missing installer rejected", async () => { using var f = new Fixture { AssetName = "other.exe" }; await Reject(() => f.Check()); });
await Check("missing checksum rejected", async () => { using var f = new Fixture { Digest = null }; await Reject(() => f.Check()); });
await Check("oversized installer rejected", async () => { using var f = new Fixture { Size = 300L * 1024 * 1024 }; await Reject(() => f.Check()); });
await Check("foreign asset URL rejected", async () => { using var f = new Fixture { AssetUrl = "https://api.github.com/repos/another/app/releases/assets/1" }; await Reject(() => f.Check()); });
await Check("anonymous private feed error explains browser login and token setup", async () =>
{
    using var f = new Fixture(token: "") { Status = HttpStatusCode.NotFound };
    var error = await AccessError(() => f.Check());
    True(error.Message.Contains("browser's GitHub login")); True(error.Message.Contains(UpdateClient.Repository));
    True(!f.SawApiToken);
});
await Check("authenticated private feed error explains repository and organization access", async () =>
{
    using var f = new Fixture { Status = HttpStatusCode.NotFound };
    var error = await AccessError(() => f.Check());
    True(error.Message.Contains("saved token")); True(error.Message.Contains("organization approval"));
    True(error.Message.Contains(UpdateClient.Repository));
});
await Check("invalid token offers replacement", async () =>
{
    using var f = new Fixture { Status = HttpStatusCode.Unauthorized };
    True((await AccessError(() => f.Check())).Message.Contains("expired or revoked"));
});
await Check("forbidden access offers permission guidance", async () =>
{
    using var f = new Fixture { Status = HttpStatusCode.Forbidden };
    True((await AccessError(() => f.Check())).Message.Contains("Contents: read access"));
});
await Check("GitHub rate limits are distinguished from access failures", async () =>
{
    foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
    {
        using var f = new Fixture { Status = status, RateLimited = true };
        try { await f.Check(); throw new Exception("Expected failure"); }
        catch (InvalidOperationException e) { True(e is not UpdateAccessException); True(e.Message.Contains("request limit")); }
    }
});
await Check("verified download preserves bytes", async () =>
{
    using var f = new Fixture(); string path = await f.Download(); True(File.Exists(path));
    Equal(Convert.ToHexString(f.Bytes), Convert.ToHexString(await File.ReadAllBytesAsync(path)));
    True(!Directory.EnumerateFiles(f.Directory, "*.partial", SearchOption.AllDirectories).Any());
});
await Check("redirect downloads do not forward GitHub access token", async () =>
{
    using var f = new Fixture { Redirect = "https://release-assets.githubusercontent.com/file.exe" }; await f.Download();
    True(f.SawApiToken); True(f.SawCdn); True(!f.SawCdnToken);
});
await Check("redirect to unknown domain rejected without request", async () =>
{
    using var f = new Fixture { Redirect = "https://unexpected.example/file.exe" }; await Reject(() => f.Download()); True(!f.SawCdn);
});
await Check("insecure download redirect rejected", async () =>
{
    using var f = new Fixture { Redirect = "http://release-assets.githubusercontent.com/file.exe" }; await Reject(() => f.Download());
});
await Check("checksum mismatch leaves no runnable installer", async () =>
{
    using var f = new Fixture { Digest = "sha256:" + new string('0', 64) }; await Reject(() => f.Download()); Empty(f.Directory);
});
await Check("truncated download leaves no runnable installer", async () =>
{
    using var f = new Fixture(); f.Size += 1; await Reject(() => f.Download()); Empty(f.Directory);
});
await Check("cancelled download leaves no runnable installer", async () =>
{
    using var f = new Fixture(); var release = await f.Check(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await f.Client.DownloadAsync(release!, f.Directory, cancellationToken: cancellation.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Empty(f.Directory); }
});
Console.WriteLine($"{passed} updater checks passed; {failed} failed.");
return failed == 0 ? 0 : 1;

async Task Check(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS: " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL: " + name + " — " + e.Message); }
}
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Empty(string path) { if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any()) throw new Exception("Failed update left files behind"); }
static async Task Reject(Func<Task> action)
{
    try { await action(); } catch (InvalidOperationException) { return; }
    throw new Exception("Expected rejected update");
}
static async Task<UpdateAccessException> AccessError(Func<Task> action)
{
    try { await action(); } catch (UpdateAccessException e) { return e; }
    throw new Exception("Expected update access error");
}

sealed class Fixture : HttpMessageHandler
{
    public byte[] Bytes = Encoding.UTF8.GetBytes("synthetic test installer bytes");
    public string? Digest;
    public long Size;
    public string Tag = "v0.3.0", AssetName = "Timekeeper-Setup-0.3.0-win-x64.exe";
    public string AssetUrl = "https://api.github.com/repos/" + UpdateClient.Repository + "/releases/assets/123";
    public bool Prerelease, Draft, SawApiToken, SawCdn, SawCdnToken, RateLimited;
    public string? Redirect;
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string Directory = Path.Combine(Path.GetTempPath(), "Timekeeper-update-tests-" + Guid.NewGuid().ToString("N"));
    public UpdateClient Client;
    private readonly HttpClient _http;
    public Fixture(string token = "synthetic-github-token")
    {
        Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Bytes)); Size = Bytes.Length;
        _http = new HttpClient(this, disposeHandler: false); Client = new UpdateClient(token, _http);
    }
    public Task<AppRelease?> Check() => Client.CheckAsync(new Version(0, 2, 0));
    public async Task<string> Download() => await Client.DownloadAsync((await Check())!, Directory);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri!.Host == "api.github.com") SawApiToken |= request.Headers.Authorization?.Parameter == "synthetic-github-token";
        else { SawCdn = true; SawCdnToken |= request.Headers.Authorization is not null; }
        if (request.RequestUri!.AbsolutePath.EndsWith("/latest"))
        {
            var response = new HttpResponseMessage(Status) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                tag_name = Tag, draft = Draft, prerelease = Prerelease, body = "Update notes",
                assets = new[] { new { name = AssetName, url = AssetUrl, digest = Digest, size = Size } }
            })) };
            if (RateLimited) response.Headers.Add("X-RateLimit-Remaining", "0");
            return Task.FromResult(response);
        }
        if (request.RequestUri.Host == "api.github.com" && Redirect is not null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(Redirect); return Task.FromResult(response);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
    }
    protected override void Dispose(bool disposing)
    {
        Client.Dispose(); _http.Dispose();
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        base.Dispose(disposing);
    }
}
