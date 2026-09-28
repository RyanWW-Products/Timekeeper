using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Timekeeper.Core;

public sealed record AppRelease(Version Version, string Tag, string Notes, Uri Page, Uri AssetApi, string FileName, long Size, string Sha256);

public sealed class UpdateAccessException(string message) : InvalidOperationException(message);

/// <summary>Explicit, user-requested updates from the application's fixed GitHub release feed.</summary>
public sealed class UpdateClient : IDisposable
{
    public const string Repository = "RyanWW-Products/Timekeeper";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string ApiRoot = "https://api.github.com/repos/" + Repository;
    private const long MaxInstallerBytes = 200 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _token;

    public UpdateClient(string token = "", HttpClient? http = null)
    {
        _token = token.Trim();
        if (_token.Any(char.IsControl)) throw new ArgumentException("Enter a valid GitHub token.");
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<AppRelease?> CheckAsync(Version installed, CancellationToken cancellationToken = default)
    {
        using var request = Request(new Uri(ApiRoot + "/releases/latest"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        EnsureSuccess(response);
        var bytes = await ReadLimitedAsync(response.Content, 1024 * 1024, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidOperationException("The update feed did not return a stable release.");
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version))
            throw new InvalidOperationException("The update has an unsupported version number.");
        var current = new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build));
        if (version <= current) return null;
        var fileName = $"Timekeeper-Setup-{version}-win-x64.exe";
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == fileName).ToArray();
        if (assets.Length != 1) throw new InvalidOperationException("This release does not have a single Windows installer. Open the releases page for details.");
        var asset = assets[0];
        string digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
        if (!Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidOperationException("GitHub has not supplied this installer's checksum. Try again later or use the releases page.");
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxInstallerBytes) throw new InvalidOperationException("The update installer has an unexpected size.");
        string assetUrl = asset.GetProperty("url").GetString() ?? "";
        if (!assetUrl.StartsWith(ApiRoot + "/releases/assets/", StringComparison.Ordinal) ||
            !long.TryParse(assetUrl[(ApiRoot.Length + "/releases/assets/".Length)..], out var assetId) || assetId <= 0)
            throw new InvalidOperationException("The installer does not belong to the Timekeeper release feed.");
        return new(version, tag, root.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "",
            new Uri(ReleasesPage + "/tag/" + tag), new Uri(assetUrl), fileName, size, digest[7..]);
    }

    public async Task<string> DownloadAsync(AppRelease release, string outputDirectory, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!release.AssetApi.AbsoluteUri.StartsWith(ApiRoot + "/releases/assets/", StringComparison.Ordinal) ||
            release.FileName != $"Timekeeper-Setup-{release.Version}-win-x64.exe" ||
            release.Size <= 0 || release.Size > MaxInstallerBytes || !Regex.IsMatch(release.Sha256, @"^[a-fA-F0-9]{64}$"))
            throw new InvalidOperationException("The update metadata is invalid.");
        var directory = Path.Combine(Path.GetFullPath(outputDirectory), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, "download.partial"), final = Path.Combine(directory, release.FileName);
        try
        {
            using var response = await GetInstallerAsync(release.AssetApi, cancellationToken);
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new InvalidOperationException("The installer size does not match the release.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                byte[] buffer = new byte[81920]; long received = 0; int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    received += count;
                    if (received > release.Size) throw new InvalidOperationException("The installer exceeded its expected size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report((int)(received * 100 / release.Size));
                }
                if (received != release.Size) throw new InvalidOperationException("The installer download was incomplete. Please try again.");
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
                throw new InvalidOperationException("The installer checksum did not match. Nothing was installed. Please try again.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, final);
            return final;
        }
        catch
        {
            File.Delete(partial);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetInstallerAsync(Uri uri, CancellationToken ct)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 ||
                !(uri.Host == "api.github.com" || uri.Host == "github.com" || uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com" || uri.Host == "github-releases.githubusercontent.com"))
                throw new InvalidOperationException("GitHub redirected the installer to an unsupported address.");
            using var request = Request(uri, binary: true);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidOperationException("The installer download redirect was incomplete.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidOperationException("The installer download had too many redirects.");
    }
    private HttpRequestMessage Request(Uri uri, bool binary = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Timekeeper-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
        if (uri.Host == "api.github.com")
        {
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (_token.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
        return request;
    }
    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        bool rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests ||
            (response.StatusCode == HttpStatusCode.Forbidden &&
             (response.Headers.RetryAfter is not null ||
              (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"))));
        if (rateLimited)
            throw new InvalidOperationException("GitHub's request limit was reached. Wait before checking again, or download through Open releases page.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new UpdateAccessException(_token.Length == 0
                ? $"GitHub could not return the release. If {Repository} is private, save a GitHub token with Contents: read access below, or use Open releases page and sign in. Timekeeper cannot use your browser's GitHub login."
                : $"GitHub could not return the release with the saved token. Check that the token allows Contents: read access to {Repository} and has any required organization approval. Use Open releases page to confirm the release is available.");
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UpdateAccessException("GitHub did not accept the saved update access token. It may be expired or revoked. Replace it below and save, then check again.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new UpdateAccessException($"GitHub denied this update request. Check the saved token's Contents: read access to {Repository} and any required organization approval, or use Open releases page.");
        throw new InvalidOperationException($"GitHub could not complete the update request (HTTP {(int)response.StatusCode}). Try again later.");
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream();
        await using var input = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidOperationException("The update response was too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
