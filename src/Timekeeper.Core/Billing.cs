using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Timekeeper.ApiTests")]

namespace Timekeeper.Core;

public sealed record BillingCapabilities
{
    public int? OverrideFieldId { get; init; }
    public int? BillableFieldId { get; init; }
    public bool CanOverride { get; init; }
    public int? ProjectTypeFieldId { get; init; }
    public int? TaskBillableFieldId { get; init; }
    public Dictionary<int, string> ProjectTypes { get; init; } = [];
    public Dictionary<int, bool> TaskBilling { get; init; } = [];
    public bool? DefaultFor(int project, int task) => ProjectTypes.TryGetValue(project, out var type)
        ? type.Equals("Internal", StringComparison.OrdinalIgnoreCase) ? false : TaskBilling.TryGetValue(task, out var billable) ? billable : null
        : null;
    public string Message { get; init; } = "Quickbase billing permissions have not been checked.";
}

public sealed partial class TimecardApi
{
    private BillingCapabilities? billingCapabilities;

    public async Task<BillingCapabilities> ReadBillingCapabilitiesAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && billingCapabilities is not null) return billingCapabilities;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, QbBase + "fields?tableId=" + Uri.EscapeDataString(settings.TimecardsTable) + "&includeFieldPerms=true");
            request.Headers.Add("QB-Realm-Hostname", settings.Realm);
            request.Headers.Authorization = new AuthenticationHeaderValue("QB-USER-TOKEN", credentials.QuickbaseToken);
            using var response = await SendAsync(request, ct);
            EnsureSuccess(response, "Quickbase billing permissions");
            using var document = ParseJson(await ReadLimitedAsync(response, ct), "Quickbase billing permissions");
            return billingCapabilities = await ParseBillingCapabilitiesAsync(document.RootElement, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException)
        {
            // Billing is optional. Ordinary timecards retain Quickbase's default when metadata is unavailable.
            return billingCapabilities = new() { Message = "Quickbase billing permissions could not be confirmed. Keep Quickbase default, or check your connection and field permissions before choosing an override." };
        }
    }

    public async Task ValidateBillingAsync(IEnumerable<VerifiedRow> rows, CancellationToken ct = default)
    {
        var explicitRows = rows.Any(row => row.BillableOverride.HasValue);
        if (!explicitRows) return;
        var capability = await ReadBillingCapabilitiesAsync(refresh: true, ct: ct);
        if (!capability.CanOverride) throw new InvalidOperationException(capability.Message + " No timecards were sent.");
    }

    private async Task<BillingCapabilities> ParseBillingCapabilitiesAsync(JsonElement root, CancellationToken ct)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 10000)
            throw new InvalidDataException("Quickbase returned invalid billing field metadata.");
        var overrides = root.EnumerateArray().Where(f => String(f, "label").Equals("Billable Override", StringComparison.OrdinalIgnoreCase)).ToArray();
        var results = root.EnumerateArray().Where(f => String(f, "label").Equals("Bill This Task", StringComparison.OrdinalIgnoreCase)).ToArray();
        int? resultId = results.Length == 1 && String(results[0], "fieldType") == "checkbox" && String(results[0], "mode") == "formula" ? FieldId(results[0]) : null;
        var defaults = DefaultBillingMetadata(root, results);
        const string accessMessage = "Timekeeper could not confirm Modify access to the Timecards Billable Override field. Ask your Quickbase administrator to check your role and this field, then read again. Quickbase default remains available.";
        if (overrides.Length != 1) return defaults with { BillableFieldId = resultId, Message = accessMessage };
        var field = overrides[0];
        var id = FieldId(field);
        var type = String(field, "fieldType");
        var mode = String(field, "mode");
        bool safe = id.HasValue && id != resultId && type is "text" or "text-multiple-choice" && mode is "" or "normal";
        if (field.TryGetProperty("readOnly", out var readOnly) && readOnly.ValueKind != JsonValueKind.False) safe = false;
        if (field.TryGetProperty("permissions", out var permissions))
        {
            if (permissions.ValueKind != JsonValueKind.Array) safe = false;
            else if (permissions.GetArrayLength() > 0)
            {
                var rolePermissions = new Dictionary<int, string>();
                foreach (var permission in permissions.EnumerateArray())
                {
                    var access = String(permission, "permissionType");
                    if (!permission.TryGetProperty("roleId", out var role) || !TryRecordId(role, out var roleId) || roleId <= 0
                        || access is not ("None" or "View" or "Modify") || !rolePermissions.TryAdd(roleId, access)) safe = false;
                }
                if (safe)
                {
                    try
                    {
                        var ownRoles = await ReadCallerRoleIdsAsync(ct);
                        // Quickbase combines the permissions of the caller's roles. A restriction on
                        // somebody else's role must not deny a caller who has Modify through their own.
                        if (!ownRoles.Any(role => rolePermissions.TryGetValue(role, out var access) && access == "Modify")) safe = false;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException) { safe = false; }
                }
            }
        }
        if (field.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            if (String(properties, "formula").Length > 0 || properties.TryGetProperty("lookupReferenceFieldId", out _) || properties.TryGetProperty("lookupTargetFieldId", out _)) safe = false;
            if (properties.TryGetProperty("readOnly", out var propertyReadOnly) && propertyReadOnly.ValueKind != JsonValueKind.False) safe = false;
            if (properties.TryGetProperty("choices", out var choices))
            {
                if (choices.ValueKind != JsonValueKind.Array) safe = false;
                else
                {
                    var labels = choices.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()).ToHashSet(StringComparer.Ordinal);
                    if (!labels.Contains("Billable") || !labels.Contains("NonBillable")) safe = false;
                }
            }
            else if (type == "text-multiple-choice") safe = false;
        }
        else safe = false;
        if (!resultId.HasValue) safe = false;
        return defaults with { OverrideFieldId = safe ? id : null, BillableFieldId = resultId, CanOverride = safe,
            Message = safe ? "Billing selections keep the same project, assignment, and task. Quickbase permissions still apply when writing." : accessMessage };
    }

    private static BillingCapabilities DefaultBillingMetadata(JsonElement root, JsonElement[] results)
    {
        // Resolve this known rule only when the live formula and both relationship lookups match.
        // Unknown/custom formulas must not produce a guessed Billable label.
        const string expected = "If([Billable Override]=\"\",If([Project Type]=\"Internal\",false,[Bill Task]=true,true,false), [Billable Override]=\"NonBillable\",false,[Billable Override]=\"Billable\",true)";
        if (results.Length != 1 || String(results[0], "fieldType") != "checkbox" || String(results[0], "mode") != "formula" || !results[0].TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object
            || !string.Equals(System.Text.RegularExpressions.Regex.Replace(String(props, "formula"), @"\s+", ""),
                System.Text.RegularExpressions.Regex.Replace(expected, @"\s+", ""), StringComparison.OrdinalIgnoreCase)) return new();
        int? Lookup(string label, int reference, string type)
        {
            var matches = root.EnumerateArray().Where(f => String(f, "label") == label && String(f, "mode") == "lookup" && String(f, "fieldType") == type).ToArray();
            if (matches.Length != 1 || !matches[0].TryGetProperty("properties", out var p)
                || !p.TryGetProperty("lookupReferenceFieldId", out var r) || !r.TryGetInt32(out var id) || id != reference
                || !p.TryGetProperty("lookupTargetFieldId", out var target) || !target.TryGetInt32(out var targetId) || targetId <= 3) return null;
            return targetId;
        }
        return new() { ProjectTypeFieldId = Lookup("Project Type", 11, "text"), TaskBillableFieldId = Lookup("Bill Task", 16, "checkbox") };
    }

    public async Task<BillingCapabilities> ReadBillingDefaultsAsync(IEnumerable<int> projects, CancellationToken ct = default)
    {
        var billing = await ReadBillingCapabilitiesAsync(ct: ct);
        await ReadBillingDefaultsAsync(billing, projects, ct);
        return billing;
    }

    private async Task ReadBillingDefaultsAsync(BillingCapabilities billing, IEnumerable<int> projects, CancellationToken ct)
    {
        if (billing.ProjectTypeFieldId is not int projectField || billing.TaskBillableFieldId is not int taskField) return;
        try
        {
            if (billing.TaskBilling.Count == 0)
                foreach (var row in await QueryAllAsync(settings.TasksTable, [3, taskField], null, ct))
                    if (TryRecordId(Cell(row, 3), out var id) && BillingValue(Cell(row, taskField)) is bool value) billing.TaskBilling[id] = value;
            foreach (var batch in projects.Distinct().Where(id => !billing.ProjectTypes.ContainsKey(id)).Chunk(100))
            {
                var where = string.Join("OR", batch.Select(id => $"{{3.EX.'{id.ToString(CultureInfo.InvariantCulture)}'}}"));
                foreach (var row in await QueryAllAsync(settings.ProjectsTable, [3, projectField], where, ct))
                    if (TryRecordId(Cell(row, 3), out var id) && Cell(row, projectField).ValueKind == JsonValueKind.String)
                        billing.ProjectTypes[id] = CellString(row, projectField);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException)
        {
            // Unreadable defaults remain explicitly unknown; ordinary timecards can still be reviewed.
        }
    }
    private async Task<HashSet<int>> ReadCallerRoleIdsAsync(CancellationToken ct)
    {
        const string formula = "ToText(UserRoles(\"ID\"))";
        using var request = QuickbaseRequest("formula/run", new { from = settings.TimecardsTable, formula });
        using var response = await SendAsync(request, ct);
        EnsureSuccess(response, "Quickbase billing role verification");
        using var document = ParseJson(await ReadLimitedAsync(response, ct), "Quickbase billing role verification");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count(p => p.NameEquals("result")) != 1
            || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String || result.GetString()!.Length > 4096)
            throw new InvalidDataException("Quickbase did not return usable billing roles.");
        var roles = new HashSet<int>();
        foreach (var value in result.GetString()!.Split(';', StringSplitOptions.TrimEntries))
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var role) || role <= 0 || !roles.Add(role))
                throw new InvalidDataException("Quickbase did not return usable billing roles.");
        if (roles.Count is 0 or > 128) throw new InvalidDataException("Quickbase did not return usable billing roles.");
        return roles;
    }

    private static int? FieldId(JsonElement field)
    {
        if (!field.TryGetProperty("id", out var id) || !TryRecordId(id, out var value) || value <= 0 || new[] { 3, 7, 10, 11, 16, 19, 22, 24, 73 }.Contains(value)) return null;
        return value;
    }

    private static bool? BillingValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when value.GetString() is "true" or "1" => true,
        JsonValueKind.String when value.GetString() is "false" or "0" => false,
        JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1 => number == 1,
        _ => null
    };

    private async Task<RowOutcome> CreatedWithBillingAsync(VerifiedRow row, int recordId, JsonElement response, BillingCapabilities billing, CancellationToken ct)
    {
        // Creation is already proven. Any later billing problem must preserve that fact and ID.
        bool? actual = null;
        if (billing.BillableFieldId is int field)
        {
            if (response.TryGetProperty("data", out var returned) && returned.ValueKind == JsonValueKind.Array && returned.GetArrayLength() == 1
                && TryRecordId(Cell(returned[0], 3), out var returnedId) && returnedId == recordId)
                actual = BillingValue(Cell(returned[0], field));
            if (!actual.HasValue && row.BillableOverride.HasValue)
            {
                try
                {
                    var records = await QueryAllAsync(settings.TimecardsTable, [3, field], $"{{3.EX.'{recordId.ToString(CultureInfo.InvariantCulture)}'}}", ct);
                    if (records.Count == 1 && TryRecordId(Cell(records[0], 3), out var readId) && readId == recordId) actual = BillingValue(Cell(records[0], field));
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException or FormatException) { }
            }
        }
        var message = row.BillableOverride.HasValue && actual != row.BillableOverride
            ? actual.HasValue
                ? $"Created in Quickbase, but its billing status is {(actual.Value ? "billable" : "non-billable")} instead of your selection. Correct billing on this existing record in Quickbase. Do not resubmit it."
                : "Created in Quickbase, but its billing status could not be confirmed. Check billing on this existing record in Quickbase. Do not resubmit it."
            : "Created in Quickbase.";
        return new RowOutcome { Row = row, Status = "created", RecordId = recordId, ActualBillable = actual, Message = message };
    }
}
