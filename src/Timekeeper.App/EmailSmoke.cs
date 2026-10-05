using Timekeeper.Core;

namespace Timekeeper.App;

internal static class EmailSmoke
{
    internal static ReadSession Session()
    {
        var sample=DemoData.CreateSession(); var date=sample.Days[0].Date;
        return sample with
        {
            Days=[new DaySnapshot { Date=date }],
            Reference=sample.Reference with { Billing=new BillingCapabilities { CanOverride=true,OverrideFieldId=180,BillableFieldId=72,Message="Synthetic permission" } },
            EmailWorkbook=new EmailWorkbook
            {
                Metadata=new() { EmployeeEmail=sample.Settings.Email,TimeZone=sample.Settings.TimeZoneId,PeriodStart=date,PeriodEnd=date,GeneratedAtUtc=DateTimeOffset.UtcNow,EmployeeConfirmed=true,RetrievalLimitations="Teams call data unavailable in this synthetic example." },
                Activities=[new() { ActivityId="sample-revision",Date=date,Minutes=1,MatterHint="Example Client • Spring launch",AssignmentHint="Presentation design",TaskHint="Design",Description="Confirmed a requested presentation revision.",Billable=true,TimeBasis="employee_confirmed_estimate",EvidenceIds=["synthetic-message-001"] },
                    new() { ActivityId="sample-followup",Date=date,Minutes=1,MatterHint="Example Client • Spring launch",AssignmentHint="Presentation design",TaskHint="Design",Description="Followed up on the revision schedule.",Billable=false,TimeBasis="employee_confirmed_estimate",EvidenceIds=["synthetic-message-002"] }]
            }
        };
    }
    internal static ProposalEnvelope Proposal(ReadSession session)=>new()
    {
        SessionId=session.SessionId,EmployeeId=session.Settings.EmployeeId,
        Rows=session.EmailWorkbook!.Activities.Select(a=>new ProposalRow { Date=a.Date,SourceActivityIds=[a.ActivityId],Project=100,Assignment=501,Task=60,Category=20,Description=a.Description,BillableOverride=a.Billable }).ToList()
    };
}
