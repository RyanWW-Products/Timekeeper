namespace Timekeeper.Core;

/// <summary>Invented example data. Demo mode never needs credentials or permits network submission.</summary>
public static class DemoData
{
    public static ReadSession CreateSession()
    {
        var settings = new AppSettings { Realm = "demo.quickbase.com", Email = "alex@example.com", EmployeeId = "demo-employee", InternalProjectId = 900, InternalTaskId = 44 };
        var date = settings.Today();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var start = date.ToDateTime(new TimeOnly(8, 0));
        var offset = zone.GetUtcOffset(start);
        var morning = new DateTimeOffset(start, offset);
        return new ReadSession
        {
            Demo = true, Settings = settings,
            Reference = new ReferenceData
            {
                InternalProject = new ProjectRecord(900, "Example Company Internal"),
                Categories = [new(11, "Internal"), new(20, "Client work")],
                Tasks = [new(44, "Other", 11), new(60, "Design", 20), new(61, "Research", 20)],
                Assignments = [new(501, "Presentation design", 100, "Example Client • Spring launch", "Example Client", "Active", "Invented demo assignment"),
                    new(502, "Audience research", 101, "Example Client • Planning", "Example Client", "Active", "Invented demo assignment")]
            },
            Days = [new DaySnapshot
            {
                Date = date,
                Entries = [new(1001, 1, morning, morning.AddHours(4), 14_400, "Prepare presentation visuals", "Spring launch", false, true),
                    new(1002, 1, morning.AddHours(5), morning.AddHours(8), 10_800, "Review audience research", "Planning", false, true)]
            }]
        };
    }

    public static ProposalEnvelope CreateProposal(ReadSession session) => new()
    {
        SchemaVersion = 1, SessionId = session.SessionId, EmployeeId = session.Settings.EmployeeId,
        Rows = session.Days.SelectMany(day => day.Entries.Select(entry => new ProposalRow
        {
            Date = day.Date, SourceEntryIds = [entry.Id], Assignment = entry.Id == 1001 ? 501 : 502,
            Project = entry.Id == 1001 ? 100 : 101, Task = entry.Id == 1001 ? 60 : 61,
            Category = 20, Description = entry.Description
        })).ToList()
    };
}
