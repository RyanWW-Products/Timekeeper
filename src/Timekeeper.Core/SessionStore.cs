using System.Text.Json;
using System.Text.RegularExpressions;

namespace Timekeeper.Core;

/// <summary>Durable, non-secret state. A failed or corrupt journal is always an error, never an empty history.</summary>
public sealed class SessionStore
{
    public string RootPath { get; }
    public SessionStore(string? root = null)
    {
        RootPath = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Timekeeper"));
        Directory.CreateDirectory(RootPath);
    }
    public AppSettings LoadSettings()
    {
        var settings = Read<AppSettings>(Path.Combine(RootPath, "settings.json")) ?? new();
        // Upgrade only the old generic default; preserve deliberately configured links.
        if (settings.CopilotUrl is "https://m365.cloud.microsoft/chat" or "https://m365.cloud.microsoft/chat/")
        {
            settings = settings with { CopilotUrl = AppSettings.SharedCopilotUrl };
            SaveSettings(settings);
        }
        return settings;
    }
    public void SaveSettings(AppSettings settings) => Write(Path.Combine(RootPath, "settings.json"), settings);
    public void SaveSession(ReadSession session) => Write(SessionPath(session.SessionId), session);
    public List<ManualTimecard> LoadManualDrafts(string profileKey)
    {
        var drafts = Read<List<ManualTimecard>>(Path.Combine(RootPath, "manual-drafts", SafeId(profileKey) + ".json")) ?? [];
        var errors = ManualTimecards.ValidateDrafts(drafts);
        if (errors.Count > 0) throw new InvalidDataException("Saved manual timecards are invalid: " + string.Join(" ", errors));
        return drafts;
    }
    public void SaveManualDrafts(string profileKey, IReadOnlyList<ManualTimecard> drafts, IReadOnlyList<ManualTimecard>? expectedEntries = null)
    {
        var snapshot = drafts?.ToList();
        var expected = expectedEntries?.ToList();
        var errors = ManualTimecards.ValidateDrafts(snapshot);
        if (expected is not null) errors.AddRange(ManualTimecards.ValidateDrafts(expected));
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
        var path = Path.Combine(RootPath, "manual-drafts", SafeId(profileKey) + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var draftLock = AcquireManualDraftLock(path + ".lock");
        if (expected is not null && !LoadManualDrafts(profileKey).SequenceEqual(expected))
            throw new InvalidOperationException("Manual timecards changed in another Timekeeper window. Close and reopen Timekeeper to load the current drafts before saving. Your older list was not saved.");
        Write(path, snapshot!);
    }
    private static FileStream AcquireManualDraftLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another Timekeeper window is saving manual timecards. Wait for it to finish, then close and reopen Timekeeper to load the current drafts."); }
    }
    public ReadSession? LoadSession(string sessionId) => Read<ReadSession>(SessionPath(sessionId));
    public ReadSession? LoadLatestSession(string? profileKey = null)
    {
        var directory = Path.Combine(RootPath, "sessions");
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "*.json").Select(p => new { Session = Read<ReadSession>(p), SavedAt = File.GetLastWriteTimeUtc(p) })
            .Where(s => s.Session is not null && (profileKey is null || s.Session.Settings.ProfileKey == profileKey))
            .OrderByDescending(s => s.Session!.GeneratedAtUtc).ThenByDescending(s => s.SavedAt).Select(s => s.Session).FirstOrDefault();
    }
    public List<SubmissionReceipt> LoadReceipts(string? profileKey = null)
    {
        var directory = Path.Combine(RootPath, "receipts");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json").Select(p => Read<SubmissionReceipt>(p)
                ?? throw new InvalidDataException("A submission receipt is empty. Restore the journal before writing."))
            .Where(r => profileKey is null || r.ProfileKey == profileKey)
            .OrderByDescending(r => r.StartedAtUtc).ToList();
    }
    public void SaveReceipt(SubmissionReceipt receipt) => Write(Path.Combine(RootPath, "receipts", SafeId(receipt.SubmissionId) + ".json"), receipt);
    public string GetExportPath(ReadSession session)
    {
        var directory = Path.Combine(RootPath, "exports");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "Timekeeper-" + SafeId(session.SessionId) + ".json");
    }
    // FileShare.None works across processes; unlike a named mutex this lock can cross async continuations.
    internal FileStream AcquireSubmissionLock()
    {
        try { return new FileStream(Path.Combine(RootPath, "submission.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another Timekeeper window is submitting or reconciling. Wait for it to finish."); }
    }
    private string SessionPath(string id) => Path.Combine(RootPath, "sessions", SafeId(id) + ".json");
    private static string SafeId(string id) => Regex.IsMatch(id ?? "", "\\A[A-Za-z0-9_-]{1,80}\\z") ? id! : throw new InvalidDataException("Invalid local record identifier.");
    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("A local record exceeds the supported size.");
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options)
                ?? throw new InvalidDataException("A local record is empty.");
        }
        catch (JsonException) { throw new InvalidDataException("A saved Timekeeper record is damaged or incompatible. Restore it before writing."); }
    }
    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, JsonDefaults.Options);
                stream.Flush(flushToDisk: true);
            }
            // Atomic same-volume replacement prevents a crash from leaving a partial JSON journal.
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
