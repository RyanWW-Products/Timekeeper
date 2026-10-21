using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Timekeeper.Core;

namespace Timekeeper.App;

public partial class MainWindow
{
    private List<ManualTimecard> _manualDrafts = [];
    private sealed record ManualDraftView(ManualTimecard Entry, string Mapping, string Billing)
    {
        public DateOnly Date => Entry.Date;
        public decimal Hours => Entry.Hours;
        public string Description => Entry.Description;
    }

    private void LoadManualDrafts()
    {
        _manualDrafts = _store.LoadManualDrafts(_settings.ProfileKey);
        RetireCreatedManualDrafts();
        UpdateManualPanel();
    }

    private void SaveManualDrafts(IEnumerable<ManualTimecard> entries)
    {
        var values = entries.ToList();
        if (!_smoke) _store.SaveManualDrafts(_settings.ProfileKey, values, _manualDrafts);
        _manualDrafts = values;
        UpdateManualPanel();
    }

    private void RetireCreatedManualDrafts()
    {
        var created = _store.LoadReceipts(_settings.ProfileKey).SelectMany(r => r.Rows)
            .Where(r => r.Status == "created" && r.DeletionConfirmedAtUtc is null)
            .SelectMany(r => r.Row.ManualEntryIds).ToHashSet(StringComparer.Ordinal);
        if (_manualDrafts.Any(m => created.Contains(m.Id)))
            SaveManualDrafts(_manualDrafts.Where(m => !created.Contains(m.Id)));
    }

    private void UpdateManualPanel()
    {
        if (ManualGrid is null) return;
        var selected = (ManualGrid.SelectedItem as ManualDraftView)?.Entry.Id;
        var reference = _session?.Reference;
        ManualGrid.ItemsSource = _manualDrafts.OrderBy(m => m.Date).Select(m =>
        {
            var project = reference?.Projects.FirstOrDefault(p => p.Id == m.Project)?.Name
                ?? reference?.Assignments.FirstOrDefault(a => a.ProjectId == m.Project)?.ProjectName
                ?? (reference?.InternalProject?.Id == m.Project ? reference.InternalProject.Name : $"Project #{m.Project}");
            var task = reference?.Tasks.FirstOrDefault(t => t.Id == m.Task)?.Name ?? $"Task #{m.Task}";
            var billing = m.BillableOverride switch { true => "Billable", false => "Nonbillable", _ => reference?.Billing?.DefaultFor(m.Project, m.Task) switch { true => "Billable - Default", false => "Nonbillable - Default", _ => "Default unavailable" } };
            return new ManualDraftView(m, $"{project} / {task}", billing);
        }).ToList();
        ManualGrid.SelectedItem = ManualGrid.Items.Cast<ManualDraftView>().FirstOrDefault(m => m.Entry.Id == selected);
        ManualPanel.Visibility = _manualDrafts.Count == 0 || (_session is { Demo: true } && !_smoke) ? Visibility.Collapsed : Visibility.Visible;
        ManualPanel.Header = $"Saved manual timecards ({_manualDrafts.Count})";
        ManualSummary.Text = _manualDrafts.Count == 0 ? "Add work that was not captured in Toggl or email."
            : $"{_manualDrafts.Count} saved manual {(_manualDrafts.Count == 1 ? "timecard" : "timecards")}. Reads and imports ask whether to append or delete. Exact matches are counted once.";
        UpdateManualEnabled();
    }

    private void UpdateManualEnabled()
    {
        if (AddTimecardButton is null) return;
        var editable = !_busy && (_smoke || _session is not { Demo: true });
        AddTimecardButton.IsEnabled = editable;
        ManualGrid.IsEnabled = editable;
        EditManualButton.IsEnabled = DeleteManualButton.IsEnabled = editable && ManualGrid.SelectedItem is ManualDraftView;
    }

    private void ManualGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateManualEnabled();
    private async void AddTimecard_Click(object sender, RoutedEventArgs e) => await EditManualAsync(null);
    private async void EditManual_Click(object sender, RoutedEventArgs e)
    {
        if (ManualGrid.SelectedItem is ManualDraftView selected) await EditManualAsync(selected.Entry);
    }

    private async Task EditManualAsync(ManualTimecard? existing)
    {
        if (_busy || (!_smoke && (_session is { Demo: true } || !Configured()))) return;
        await RunAsync(async ct =>
        {
            var range = ManualEditRange(existing);
            var editorDates = ManualEditorDates(range.Start, range.End, existing is null ? _selectedDates : null);
            using var api = _smoke ? null : MakeApi();
            var session = _session;
            var hasReceipt = session is not null && _store.LoadReceipts(_settings.ProfileKey).Any(r => r.SessionId == session.SessionId);
            if (NeedsManualBaseline(session, range.Start, range.End, hasReceipt, existing is null ? _selectedDates : null, editorDates))
            {
                var drafts = ManualDraftsForDates(editorDates);
                session = api is null ? DemoData.CreateSession() with
                {
                    ManualOnly = true,
                    Days = Enumerable.Range(0, range.End.DayNumber - range.Start.DayNumber + 1).Select(offset => new DaySnapshot { Date = range.Start.AddDays(offset) }).ToList()
                } : await api.ReadManualBaselineAsync(range.Start, range.End, new Progress<string>(text => StatusText.Text = text), ct);
                session = api is null ? session with { ManualTimecards = drafts }
                    : await api.PrepareManualEntriesAsync(session, drafts, ct);
            }
            var activeSession = session ?? throw new InvalidOperationException("Quickbase reference data could not be loaded for the selected date.");
            var editorSession = existing is not null ? activeSession
                : activeSession with { Days = activeSession.Days.Where(day => editorDates.Contains(day.Date)).ToList() };
            var dialog = new ManualTimecardWindow(this, editorSession, api, existing, initialDate: range.Start);
            if (dialog.ShowDialog() != true || dialog.Entry is not ManualTimecard entry) return;
            var replacingSession = session != _session;
            session = activeSession with { Reference = dialog.Session.Reference };
            if (replacingSession && !WorkspaceDates().Contains(entry.Date)) SetWorkspaceDates(entry.Date, entry.Date);
            if (replacingSession) PresentSession(session);
            else _session = session;
            ApplyManualEntry(entry);
            ShowDay_Click(this, new RoutedEventArgs());
            StatusText.Text = "Manual timecard saved. Review it below, or append it when you read or import your day.";
        }, true);
    }

    private (DateOnly Start, DateOnly End) ManualEditRange(ManualTimecard? existing)
    {
        if (existing is not null) return (existing.Date, existing.Date);
        if (StartDate.SelectedDate is not DateTime start || EndDate.SelectedDate is not DateTime end)
            throw new InvalidOperationException("Choose the date for your manual timecard at the top of the dashboard.");
        var first = DateOnly.FromDateTime(start); var last = DateOnly.FromDateTime(end);
        if (last < first || last.DayNumber - first.DayNumber >= 31)
            throw new InvalidOperationException("Choose a date or a range of up to 31 days for your manual timecard.");
        return (first, last);
    }

    private static IReadOnlyCollection<DateOnly> ManualEditorDates(DateOnly start, DateOnly end, IReadOnlyCollection<DateOnly>? selectedDates)
    {
        // A noncontiguous proposal scope is shown by its bounding dates in the workspace.
        // Keep its exact dates while adding work so an excluded middle date does not discard the source.
        if (selectedDates is { Count: > 0 } && selectedDates.Min() == start && selectedDates.Max() == end)
            return selectedDates.Order().ToArray();
        return Enumerable.Range(0, end.DayNumber - start.DayNumber + 1).Select(offset => start.AddDays(offset)).ToArray();
    }

    private static bool NeedsManualBaseline(ReadSession? session, DateOnly start, DateOnly end, bool hasReceipt, IReadOnlyCollection<DateOnly>? selectedDates, IReadOnlyCollection<DateOnly>? requiredDates = null)
    {
        if (session is null || hasReceipt || (session.ManualOnly && DateTimeOffset.UtcNow - session.GeneratedAtUtc > TimeSpan.FromHours(24))) return true;
        return (requiredDates ?? ManualEditorDates(start, end, null))
            .Any(date => !session.Days.Any(day => day.Date == date) || (selectedDates is not null && !selectedDates.Contains(date)));
    }

    private void ApplyManualEntry(ManualTimecard entry)
    {
        if (_session is null) throw new InvalidOperationException("Load Quickbase reference data before adding a manual timecard.");
        SaveManualDrafts(_manualDrafts.Where(m => m.Id != entry.Id).Append(entry));
        _session = _session with { ManualTimecards = _session.ManualTimecards.Where(m => m.Id != entry.Id).Append(entry).ToList() };
        _store.SaveSession(_session);
        RebuildManualReview();
    }

    private void DeleteManual_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ManualGrid.SelectedItem is not ManualDraftView selected) return;
        if (!ManualDeleteConfirmationWindow.Confirm(this, [selected.Entry])) return;
        try { DeleteManualEntry(selected.Entry.Id); }
        catch (Exception error) { new TextDialog(this, "Manual timecard", error.Message).ShowDialog(); }
    }

    private void DeleteManualEntry(string id)
    {
        SaveManualDrafts(_manualDrafts.Where(m => m.Id != id));
        if (_session is not null)
        {
            _session = _session with { ManualTimecards = _session.ManualTimecards.Where(m => m.Id != id).ToList() };
            if (!_store.LoadReceipts(_settings.ProfileKey).Any(r => r.SessionId == _session.SessionId)) _store.SaveSession(_session);
            RebuildManualReview();
        }
        StatusText.Text = "Manual draft deleted. Review the recalculated totals before writing.";
    }

    private void RebuildManualReview()
    {
        if (_session is null) return;
        var proposal = _proposal;
        if (proposal is null && _session.ManualOnly) proposal = new() { SessionId = _session.SessionId, EmployeeId = _session.Settings.EmployeeId };
        if (proposal is not null)
        {
            var automaticBilling = _reviewRows.Where(r => r.Row.Kind is "timecards" or "misc_internal")
                .ToDictionary(r => (r.Row.Date, r.Row.Kind), r => r.BillingIndex);
            var dates = _selectedDates?.ToArray(); var emailEdits = _emailEdits;
            var scope = ProposalScope.Select(_session, proposal, dates);
            var baseline = Rules.Validate(scope.Session, scope.Proposal, emailEdits: emailEdits);
            var edits = _durationEdits.Where(edit => baseline.Rows.Any(r => ReviewDurations.Key(r) == edit.RowKey && r.Hours == edit.OriginalHours)).ToList();
            AcceptProposal(JsonSerializer.Serialize(proposal, JsonDefaults.Options), (_, _) => dates, dates is not null,
                emailProposal: _session.EmailWorkbook is null ? null : proposal, durationEdits: edits, emailEdits: emailEdits, preserveDescriptions: true);
            _refreshingBilling = true;
            try
            {
                foreach (var row in _reviewRows.Where(r => r.Row.Kind is "timecards" or "misc_internal"))
                    if (automaticBilling.TryGetValue((row.Row.Date, row.Row.Kind), out var billing)) row.BillingIndex = billing;
            }
            finally { _refreshingBilling = false; }
        }
        else AcknowledgeWarnings.IsChecked = false;
        UpdateManualPanel(); UpdateEnabled();
    }

    private List<ManualTimecard> ManualDraftsForDates(IEnumerable<DateOnly> dates)
    {
        var included = dates.ToHashSet();
        return _manualDrafts.Where(entry => included.Contains(entry.Date)).ToList();
    }

    private ManualCarryChoice ChooseManualCarry(IReadOnlyList<ManualTimecard>? pending = null)
    {
        pending ??= _manualDrafts;
        if (pending.Count == 0) return ManualCarryChoice.Append;
        var dialog = new ManualCarryWindow(this, pending);
        return dialog.ShowDialog() == true ? dialog.Choice : ManualCarryChoice.Cancel;
    }

    private List<ManualTimecard> CarryEntries(ManualCarryChoice choice, IReadOnlyList<ManualTimecard>? pending = null) =>
        choice == ManualCarryChoice.Append ? (pending ?? _manualDrafts).ToList() : [];

    private void SaveCarriedManualDrafts(IReadOnlyList<ManualTimecard> replacedScope, IEnumerable<ManualTimecard> importedDrafts)
    {
        var replacedIds = replacedScope.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var merged = _manualDrafts.Where(entry => !replacedIds.Contains(entry.Id)).ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var entry in importedDrafts) merged[entry.Id] = entry;
        SaveManualDrafts(merged.Values);
    }

    private async void AcceptImportedProposal(string json)
    {
        if (_busy) return;
        if (_session is null) { AcceptProposal(json); return; }
        var original = _session;
        var pending = ManualDraftsForDates(original.Days.Select(day => day.Date));
        if (pending.Count == 0) { AcceptProposal(json); return; }
        var choice = ChooseManualCarry(pending);
        if (choice == ManualCarryChoice.Cancel) return;
        await RunAsync(async ct =>
        {
            using var api = _smoke ? null : MakeApi();
            var prepared = api is null ? original with { ManualTimecards = CarryEntries(choice, pending) }
                : await api.PrepareManualEntriesAsync(original, CarryEntries(choice, pending), ct);
            var saved = false;
            try
            {
                _session = prepared;
                AcceptProposal(json);
                if (_validation is not { IsValid: true }) return;
                _store.SaveSession(prepared); SaveCarriedManualDrafts(pending, prepared.ManualTimecards); saved = true;
            }
            finally
            {
                if (!saved)
                {
                    _session = original;
                    if (_validation is { IsValid: true }) ResetReview();
                    UpdateManualPanel(); UpdateEnabled();
                }
            }
        }, true);
    }

    internal string SmokeManualDashboard()
    {
        if (!_smoke) throw new InvalidOperationException("Manual review checks require synthetic mode.");
        var sample = DemoData.CreateSession();
        var date = sample.Days[0].Date;
        sample = sample with
        {
            ManualOnly = true, Days = [new() { Date = date }],
            Reference = sample.Reference with
            {
                Projects = [new(100, "Example Client / Spring launch"), sample.Reference.InternalProject!],
                Billing = new() { CanOverride = true, ProjectTypes = new() { [100] = "Client", [900] = "Internal" }, TaskBilling = new() { [44] = false, [61] = true } }
            }
        };
        var tomorrow = date.AddDays(1);
        if (!NeedsManualBaseline(null, tomorrow, tomorrow, false, null)
            || !NeedsManualBaseline(sample, tomorrow, tomorrow, false, null)
            || NeedsManualBaseline(sample, date, date, false, null)
            || !NeedsManualBaseline(sample, date, date, true, null)
            || !NeedsManualBaseline(sample with { GeneratedAtUtc = DateTimeOffset.UtcNow.AddHours(-25) }, date, date, false, null))
            throw new InvalidOperationException("Manual preload must read a Quickbase-only baseline for a new date, an absent source, a completed receipt or an expired baseline.");
        var multipleDays = sample with { Days = [new() { Date = date }, new() { Date = tomorrow }] };
        if (NeedsManualBaseline(multipleDays, tomorrow, tomorrow, false, null)
            || !NeedsManualBaseline(multipleDays, tomorrow, tomorrow, false, [date]))
            throw new InvalidOperationException("Manual preload must retain a usable baseline while avoiding a previous proposal's excluded dates.");
        var dateEditor = new ManualTimecardWindow(this, multipleDays, null, smoke: true, initialDate: tomorrow);
        try { dateEditor.SmokeSummary(); }
        finally { dateEditor.Close(); }
        _manualDrafts = []; PresentSession(sample, adoptDates: true);
        var entry = new ManualTimecard { Date = date, Hours = .25m, Project = 900, Task = 44, Description = "Internal coordination not captured in email.", BillableOverride = false };
        ApplyManualEntry(entry);
        if (_validation is not { IsValid: true } || _reviewRows.Count != 1 || _reviewRows[0].Row.Kind != "manual" || _reviewRows[0].Hours != .25m || _session!.EmailWorkbook is not null || _session.Days.Any(d => d.Entries.Count > 0) || WriteButton.IsEnabled)
            throw new InvalidOperationException("Manual-only dashboard did not validate without fabricated source data.");
        AcknowledgeWarnings.IsChecked = true;
        ApplyManualEntry(entry with { Hours = .5m, Project = 100, Task = 61, Description = "Researched project background and source materials.", BillableOverride = true });
        if (_manualDrafts.Count != 1 || _validation!.Rows.Count != 1 || _validation.Rows[0].Hours != .5m || _validation.Rows[0].Assignment is not null || AcknowledgeWarnings.IsChecked == true)
            throw new InvalidOperationException("Manual editing duplicated a draft, lost direct project time or retained approval.");
        _reviewRows[0].BillingIndex = 2;
        if (_manualDrafts[0].BillableOverride != false || _session.ManualTimecards[0].BillableOverride != false)
            throw new InvalidOperationException("Dashboard manual billing was not saved for the next import.");
        ApplyDuration(_reviewRows[0], .75m);
        if (_manualDrafts[0].Hours != .75m || _validation!.Rows[0].Hours != .75m)
            throw new InvalidOperationException("Manual duration changes were not saved for carry-over.");
        var draft = _manualDrafts[0];
        var email = EmailSmoke.Session();
        email = email with { ManualTimecards = [draft], Reference = email.Reference with { Projects = sample.Reference.Projects } };
        PresentSession(email, adoptDates: true); AcceptProposal("", emailProposal: EmailSmoke.Proposal(email));
        if (_validation is not { IsValid: true } || !_validation.Rows.Any(r => r.ManualEntryIds.Contains(entry.Id)) || _manualDrafts.Single().Id != entry.Id)
            throw new InvalidOperationException("Appending a manual timecard to email lost its stable identity or review row.");
        DeleteManualEntry(entry.Id);
        if (_manualDrafts.Count != 0 || _session.ManualTimecards.Count != 0 || _validation!.Rows.Any(r => r.ManualEntryIds.Count > 0))
            throw new InvalidOperationException("Manual deletion left a row in the combined review.");
        var imported = EmailSmoke.Proposal(email);
        imported = imported with { Rows = imported.Rows.Select((r, i) => i == 0 ? r with { BillableOverride = null } : r).ToList() };
        email = email with { ManualTimecards = [], Reference = email.Reference with { Billing = new() { CanOverride = true, TaskBilling = new() { [60] = false } } } };
        var calculated = EmailRules.Validate(email, imported).Rows[0];
        var alias = new ManualTimecard { Date = calculated.Date, Hours = calculated.Hours, Project = calculated.Project, Assignment = calculated.Assignment, Task = calculated.Task, Description = calculated.Description, BillableOverride = false };
        _manualDrafts = [alias]; email = email with { ManualTimecards = [alias] };
        PresentSession(email, adoptDates: true); AcceptProposal("", emailProposal: imported);
        var other = _reviewRows.Single(r => !r.Row.ManualEntryIds.Contains(alias.Id));
        other.BillingIndex = 1;
        if (_manualDrafts.Single().BillableOverride != false)
            throw new InvalidOperationException("Changing unrelated imported billing discarded a manual entry's explicit nonbillable choice.");
        var scopedEmail = EmailSmoke.Session(); var firstDay = scopedEmail.Days[0].Date; var lastDay = firstDay.AddDays(2);
        scopedEmail = scopedEmail with
        {
            Days = [new() { Date = firstDay }, new() { Date = firstDay.AddDays(1) }, new() { Date = lastDay }],
            EmailWorkbook = scopedEmail.EmailWorkbook! with
            {
                Metadata = scopedEmail.EmailWorkbook.Metadata with { PeriodEnd = lastDay },
                Activities = scopedEmail.EmailWorkbook.Activities.Concat(scopedEmail.EmailWorkbook.Activities.Select(activity => activity with
                {
                    ActivityId = activity.ActivityId + "-later", Date = lastDay, EvidenceIds = activity.EvidenceIds.Select(id => id + "-later").ToList()
                })).ToList()
            }
        };
        _manualDrafts = []; PresentSession(scopedEmail, adoptDates: true);
        AcceptProposal("", (_, _) => [firstDay, lastDay], emailProposal: EmailSmoke.Proposal(scopedEmail));
        var scopedRange = ManualEditRange(null); var scopedEditorDates = ManualEditorDates(scopedRange.Start, scopedRange.End, _selectedDates);
        if (_validation is not { IsValid: true } || scopedEditorDates.Count != 2 || scopedEditorDates.Contains(firstDay.AddDays(1))
            || NeedsManualBaseline(_session, scopedRange.Start, scopedRange.End, false, _selectedDates, scopedEditorDates))
            throw new InvalidOperationException("Adding manual work to a noncontiguous email scope would discard its existing proposal.");
        var scopedEntry = draft with { Id = Guid.NewGuid().ToString("N"), Date = firstDay, Project = 900, Task = 44, BillableOverride = false };
        ApplyManualEntry(scopedEntry);
        if (_session?.SessionId != scopedEmail.SessionId || _session.EmailWorkbook is null || _session.ManualOnly
            || _selectedDates?.Count != 2 || _proposal?.Rows.Count != 4 || _validation is not { IsValid: true }
            || !_validation.Rows.Any(row => row.ManualEntryIds.Contains(scopedEntry.Id)))
            throw new InvalidOperationException("Adding a manual timecard lost the imported email proposal or its selected dates.");
        var distant = draft with { Id = Guid.NewGuid().ToString("N"), Date = date.AddDays(-60) };
        _manualDrafts = [draft, distant];
        var pending = ManualDraftsForDates([date]);
        if (pending.Count != 1 || pending[0].Id != draft.Id)
            throw new InvalidOperationException("A read or import included manual drafts from unrelated dates.");
        var carried = CarryEntries(ManualCarryChoice.Append, pending);
        SaveCarriedManualDrafts(pending, carried.Concat(carried));
        if (_manualDrafts.Count != 2 || _manualDrafts.Single(entry => entry.Id == distant.Id) != distant || _manualDrafts.Count(entry => entry.Id == draft.Id) != 1)
            throw new InvalidOperationException("Appending manual drafts lost an unrelated date or duplicated a stable entry ID.");
        SaveCarriedManualDrafts(pending, CarryEntries(ManualCarryChoice.Delete, pending));
        if (_manualDrafts.Count != 1 || _manualDrafts[0] != distant)
            throw new InvalidOperationException("Deleting the selected dates' manual drafts removed an unrelated saved entry.");
        _manualDrafts = [];
        PresentSession(sample, adoptDates: true); ApplyManualEntry(draft);
        return "PASS: manual preload uses the chosen date before any source import, refreshes old or excluded dates, and defaults the editor to the chosen day\nPASS: adding manual work preserves an existing email proposal with noncontiguous selected dates\nPASS: manual carry scopes append and delete to the selected dates, preserves distant drafts and deduplicates stable entry IDs\nPASS: manual dashboard creates and edits direct-project time, saves billing/hours for carry-over, appends to email once, resets approval and deletes without altering imported sources\n";
    }
}
