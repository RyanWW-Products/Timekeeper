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
            using var api = _smoke ? null : MakeApi();
            var session = _session;
            var hasReceipt = session is not null && _store.LoadReceipts(_settings.ProfileKey).Any(r => r.SessionId == session.SessionId);
            if (session is null || hasReceipt || (session.ManualOnly && DateTimeOffset.UtcNow - session.GeneratedAtUtc > TimeSpan.FromHours(24)) || (existing is not null && !session.Days.Any(d => d.Date == existing.Date)))
            {
                var date = existing?.Date ?? (StartDate.SelectedDate is DateTime value ? DateOnly.FromDateTime(value) : _settings.Today());
                session = api is null ? DemoData.CreateSession() with { ManualOnly = true, Days = [new() { Date = date }] }
                    : await api.ReadManualBaselineAsync(date, date, new Progress<string>(text => StatusText.Text = text), ct);
                session = api is null ? session with { ManualTimecards = _manualDrafts.ToList() }
                    : await api.PrepareManualEntriesAsync(session, _manualDrafts, ct);
            }
            var editorSession = _selectedDates is null || session != _session || existing is not null ? session
                : session with { Days = session.Days.Where(d => _selectedDates.Contains(d.Date)).ToList() };
            var dialog = new ManualTimecardWindow(this, editorSession, api, existing);
            if (dialog.ShowDialog() != true || dialog.Entry is not ManualTimecard entry) return;
            var replacingSession = session != _session;
            session = session with { Reference = dialog.Session.Reference };
            if (replacingSession) PresentSession(session);
            else _session = session;
            ApplyManualEntry(entry);
            ShowDay_Click(this, new RoutedEventArgs());
            StatusText.Text = "Manual timecard saved. Review it below, or append it when you read or import your day.";
        }, true);
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
                emailProposal: _session.EmailWorkbook is null ? null : proposal, durationEdits: edits, emailEdits: emailEdits);
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

    private ManualCarryChoice ChooseManualCarry()
    {
        if (_manualDrafts.Count == 0) return ManualCarryChoice.Append;
        var dialog = new ManualCarryWindow(this, _manualDrafts);
        return dialog.ShowDialog() == true ? dialog.Choice : ManualCarryChoice.Cancel;
    }

    private List<ManualTimecard> CarryEntries(ManualCarryChoice choice) => choice == ManualCarryChoice.Append ? _manualDrafts.ToList() : [];

    private async void AcceptImportedProposal(string json)
    {
        if (_busy) return;
        if (_manualDrafts.Count == 0 || _session is null) { AcceptProposal(json); return; }
        var choice = ChooseManualCarry();
        if (choice == ManualCarryChoice.Cancel) return;
        await RunAsync(async ct =>
        {
            var original = _session;
            using var api = _smoke ? null : MakeApi();
            var prepared = api is null ? original with { ManualTimecards = CarryEntries(choice) }
                : await api.PrepareManualEntriesAsync(original, CarryEntries(choice), ct);
            _session = prepared;
            AcceptProposal(json);
            if (_validation is { IsValid: true }) { _store.SaveSession(prepared); SaveManualDrafts(prepared.ManualTimecards); }
            else { _session = original; UpdateManualPanel(); }
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
        _manualDrafts = []; PresentSession(sample);
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
        PresentSession(email); AcceptProposal("", emailProposal: EmailSmoke.Proposal(email));
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
        PresentSession(email); AcceptProposal("", emailProposal: imported);
        var other = _reviewRows.Single(r => !r.Row.ManualEntryIds.Contains(alias.Id));
        other.BillingIndex = 1;
        if (_manualDrafts.Single().BillableOverride != false)
            throw new InvalidOperationException("Changing unrelated imported billing discarded a manual entry's explicit nonbillable choice.");
        _manualDrafts = [];
        PresentSession(sample); ApplyManualEntry(draft);
        return "PASS: manual dashboard creates and edits direct-project time, saves billing/hours for carry-over, appends to email once, resets approval and deletes without altering imported sources\n";
    }
}
