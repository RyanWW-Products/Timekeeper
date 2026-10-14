namespace Timekeeper.Core;

// Local edits are separate from the agent workbook and proposal. Keep source evidence intact.
public sealed record EmailActivityHours(string ActivityId, int OriginalMinutes, decimal Hours);
public sealed record EmailReviewEdits
{
    public List<EmailActivityHours> ActivityHours { get; init; } = [];
    public bool FillWeekdays { get; init; }
    public decimal TargetHours { get; init; } = 8m;
    public bool AddTimecards { get; init; }
    public static EmailReviewEdits FromSettings(AppSettings settings) => new()
    {
        FillWeekdays = settings.EmailApplyDailyDefaults && settings.FillWeekdays,
        TargetHours = settings.TargetHours,
        AddTimecards = settings.EmailApplyDailyDefaults && settings.AddTimecards
    };
}
