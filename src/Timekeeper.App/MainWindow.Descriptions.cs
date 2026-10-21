using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Timekeeper.Core;

namespace Timekeeper.App;

public partial class MainWindow
{
    private List<ReviewDescription> _descriptionEdits = [];
    private List<ReviewDescription> _parkedDescriptionEdits = [];
    private List<VerifiedRow> _parkedDescriptionRows = [];

    private void Description_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled=true;
        if(sender is FrameworkElement { DataContext:ReviewRow row }) EditDescription(row);
    }
    private void Description_KeyDown(object sender, KeyEventArgs e)
    {
        if(e.Key!=Key.F2) return; e.Handled=true;
        if(sender is FrameworkElement { DataContext:ReviewRow row }) EditDescription(row);
    }
    private void EditDescription_Click(object sender, RoutedEventArgs e)
    {
        if(ReviewGrid.SelectedItem is ReviewRow row) EditDescription(row);
    }
    private void ReviewSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(EditDescriptionButton is not null)
            EditDescriptionButton.IsEnabled=!_busy && _validation is { IsValid:true } && ReviewGrid.SelectedItem is ReviewRow { CanEditDescription:true };
    }
    private void EditDescription(ReviewRow row)
    {
        if(_busy || !row.CanEditDescription || _session is null || _proposal is null || _validation is not { IsValid:true }) return;
        var key=ReviewDurations.Key(row.Row);
        var original=_descriptionEdits.FirstOrDefault(d=>d.RowKey==key)?.OriginalDescription;
        var dialog=new DescriptionEditWindow(this,row.Description,original);
        if(dialog.ShowDialog()==true) ApplyDescription(row,dialog.Description);
    }

    private void ApplyDescription(ReviewRow row, string description)
    {
        if(_session is null || _proposal is null || _validation is not { IsValid:true }) return;
        var key=ReviewDurations.Key(row.Row);
        var scope=ProposalScope.Select(_session,_proposal,_selectedDates);
        var baseline=ReviewDurations.Apply(scope.Session,Rules.Validate(scope.Session,scope.Proposal,emailEdits:_emailEdits),_durationEdits);
        var original=baseline.Rows.Single(r=>ReviewDurations.Key(r)==key).Description;
        var edits=_descriptionEdits.Where(d=>d.RowKey!=key).ToList();
        if(description!=original) edits.Add(new(key,original,description));
        var checkedReview=ReviewDescriptions.Apply(scope.Session,baseline,edits);
        if(!checkedReview.IsValid) throw new InvalidOperationException(string.Join("\n",checkedReview.Errors));
        // A standalone manual draft must keep edits through later imports. A manual alias
        // merged into imported work stays an overlay so deduplication cannot split the row.
        if(row.Row.Kind=="manual" && row.Row.ManualEntryIds.Count==1)
        {
            var draft=_session.ManualTimecards.Single(m=>m.Id==row.Row.ManualEntryIds[0]);
            ApplyManualEntry(draft with { Description=description.Trim() });
        }
        else
        {
            _descriptionEdits=edits;
            var proposal=_proposal; var dates=_selectedDates?.ToArray();
            var billing=_reviewRows.ToDictionary(r=>ReviewDurations.Key(r.Row),r=>r.BillingIndex);
            // The newly changed row is supplied explicitly for regrouping; other rows keep their text.
            var editedRows=_reviewRows.Select(r=>r.Row with { Description=ReviewDurations.Key(r.Row)==key?description.Trim():r.Description }).ToList();
            _reviewRows=editedRows.Select(r=>new ReviewRow(r,_session.Reference.Billing?.CanOverride==true,BillingChanged,_session.Reference.Billing)).ToList();
            AcceptProposal(JsonSerializer.Serialize(proposal,JsonDefaults.Options),(_,_)=>dates,dates is not null,
                emailProposal:_session.EmailWorkbook is null?null:proposal,durationEdits:_durationEdits,emailEdits:_emailEdits,preserveDescriptions:true);
            _refreshingBilling=true;
            try { foreach(var item in _reviewRows) if(billing.TryGetValue(ReviewDurations.Key(item.Row),out var index)) item.BillingIndex=index; }
            finally { _refreshingBilling=false; }
        }
        AcknowledgeWarnings.IsChecked=false;
        StatusText.Text="Description updated. Review the text and confirm before submitting.";
        ReviewGrid.SelectedItem=_reviewRows.FirstOrDefault(r=>ReviewDurations.Key(r.Row)==key);
        UpdateEnabled();
    }

    private static List<ReviewDescription> RebaseDescriptions(IReadOnlyList<ReviewDescription> edits, IReadOnlyList<VerifiedRow> previous, IReadOnlyList<VerifiedRow> calculated)
    {
        if(edits.Count==0) return [];
        var result=new List<ReviewDescription>();
        var keys=edits.Select(e=>e.RowKey).ToHashSet(StringComparer.Ordinal);
        foreach(var row in calculated)
        {
            var key=ReviewDurations.Key(row);
            var exact=edits.SingleOrDefault(e=>e.RowKey==key);
            if(exact is not null)
            {
                if(exact.Description!=row.Description) result.Add(new(key,row.Description,exact.Description));
                continue;
            }
            if(row.Kind!="work") continue;
            // Billing may merge whole groups. Keep the text reviewed for each group and
            // combine it once, using immutable source IDs instead of descriptions as identity.
            var sources=DescriptionSources(row);
            var contributors=previous.Where(old=>old.Kind=="work" && old.Date==row.Date && old.Project==row.Project
                && old.Assignment==row.Assignment && old.Task==row.Task && old.Category==row.Category
                && DescriptionSources(old) is { Count:>0 } ids && ids.IsSubsetOf(sources)).ToList();
            if(!contributors.Any(old=>keys.Contains(ReviewDurations.Key(old)))) continue;
            if(!contributors.SelectMany(DescriptionSources).ToHashSet(StringComparer.Ordinal).SetEquals(sources))
                throw new InvalidOperationException("The edited activity changed groups. Review its description again before submitting.");
            var description=string.Join("; ",contributors.Select(old=>old.Description).Distinct(StringComparer.Ordinal));
            if(description!=row.Description) result.Add(new(key,row.Description,description));
        }
        return result;
    }

    private static HashSet<string> DescriptionSources(VerifiedRow row) => row.SourceEntryIds.Select(id=>"toggl:"+id)
        .Concat(row.SourceActivityIds.Select(id=>"email:"+id)).ToHashSet(StringComparer.Ordinal);

    internal string SmokeDescriptions()
    {
        if(!_smoke) throw new InvalidOperationException("Synthetic workspace required.");
        var session=DemoData.CreateSession(); var proposal=DemoData.CreateProposal(session);
        PresentSession(session,adoptDates:true); AcceptProposal(JsonSerializer.Serialize(proposal,JsonDefaults.Options));
        var row=_reviewRows.First(r=>r.Row.Kind=="work"); var key=ReviewDurations.Key(row.Row); var original=row.Description;
        var source=JsonSerializer.Serialize(session,JsonDefaults.Options); var originalProposal=JsonSerializer.Serialize(proposal,JsonDefaults.Options);
        AcknowledgeWarnings.IsChecked=true; ApplyDescription(row,"Researched case background.\nPrepared a concise update.");
        if(_validation is not { IsValid:true } || AcknowledgeWarnings.IsChecked==true || _reviewRows.Single(r=>ReviewDurations.Key(r.Row)==key).Description==original)
            throw new InvalidOperationException("Description edit was not reflected in the review or retained approval.");
        var edited=_reviewRows.Single(r=>ReviewDurations.Key(r.Row)==key);
        ApplyDuration(edited,edited.Hours+.25m);
        if(_reviewRows.Single(r=>ReviewDurations.Key(r.Row)==key).Description!=edited.Description)
            throw new InvalidOperationException("Hours editing discarded the description.");
        var automatic=_reviewRows.First(r=>r.Row.Kind=="timecards");
        ReviewGrid.SelectedItem=automatic;
        if(automatic.CanEditDescription || EditDescriptionButton.IsEnabled) throw new InvalidOperationException("Automatic labels must stay fixed for later reads.");
        ApplyDescription(_reviewRows.Single(r=>ReviewDurations.Key(r.Row)==key),original);
        if(_descriptionEdits.Any(e=>e.RowKey==key)) throw new InvalidOperationException("Restoring original description retained an unnecessary edit.");
        if(source!=JsonSerializer.Serialize(session,JsonDefaults.Options) || originalProposal!=JsonSerializer.Serialize(proposal,JsonDefaults.Options))
            throw new InvalidOperationException("Description edit mutated original sources or proposal.");
        var email=EmailSmoke.Session(); var emailProposal=EmailSmoke.Proposal(email);
        PresentSession(email,adoptDates:true); AcceptProposal("",emailProposal:emailProposal);
        var first=_reviewRows[0]; ApplyDescription(first,"Edited first email activity.");
        first=_reviewRows.Single(r=>r.Row.SourceActivityIds.Contains(first.Row.SourceActivityIds[0]));
        first.BillingIndex=2;
        if(_validation is not { IsValid:true } || _reviewRows.Count!=1 || !_reviewRows[0].Description.Contains("Edited first email activity.") || !_reviewRows[0].Description.Contains(email.EmailWorkbook!.Activities[1].Description))
            throw new InvalidOperationException("Billing regroup lost an edited email description.");
        ApplyBillingSnapshot(email.Reference.Billing!);
        if(!_reviewRows[0].Description.Contains("Edited first email activity.")) throw new InvalidOperationException("Billing refresh lost description.");
        var day=email.Days[0].Date; var secondDay=day.AddDays(1);
        var multi=email with { Days=[..email.Days,new() { Date=secondDay }],EmailWorkbook=email.EmailWorkbook! with
        {
            Metadata=email.EmailWorkbook!.Metadata with { PeriodEnd=secondDay },
            Activities=[..email.EmailWorkbook.Activities,..email.EmailWorkbook.Activities.Select(a=>a with { ActivityId=a.ActivityId+"-next",Date=secondDay,EvidenceIds=a.EvidenceIds.Select(id=>id+"-next").ToList() })]
        }};
        PresentSession(multi,adoptDates:true); var multiProposal=EmailSmoke.Proposal(multi);
        AcceptProposal("",(_,_)=>[day,secondDay],true,emailProposal:multiProposal);
        ApplyDescription(_reviewRows.First(r=>r.Date==secondDay),"Keep this description when dates are changed.");
        AcceptProposal("",(_,_)=>[day],true,emailProposal:multiProposal,preserveDescriptions:true);
        AcceptProposal("",(_,_)=>[day,secondDay],true,emailProposal:multiProposal,preserveDescriptions:true);
        if(!_reviewRows.Any(r=>r.Date==secondDay && r.Description=="Keep this description when dates are changed."))
            throw new InvalidOperationException("Narrowing and expanding dates lost an edited description.");
        _manualDrafts=[];
        var manual=email with { ManualOnly=true,EmailWorkbook=null,ManualTimecards=[] };
        PresentSession(manual,adoptDates:true);
        var entry=new ManualTimecard { Date=manual.Days[0].Date,Project=900,Task=44,Hours=.25m,Description="Manual original" };
        ApplyManualEntry(entry); ApplyDescription(_reviewRows.Single(),"Manual revised research.");
        if(_manualDrafts.Single().Description!="Manual revised research." || _session!.ManualTimecards.Single().Description!="Manual revised research.")
            throw new InvalidOperationException("Manual description was not saved for carry.");
        return "PASS: dashboard description edits preserve sources and approval gates, survive hours and billing changes, regroup email text, and save manual drafts\n";
    }
}
