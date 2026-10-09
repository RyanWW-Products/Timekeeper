using System.ComponentModel;
using System.Windows.Media;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class ReviewRow(VerifiedRow row, bool canOverride, Action changed, BillingCapabilities? billing = null) : INotifyPropertyChanged
{
    public VerifiedRow Row { get; private set; } = row;
    public DateOnly Date => Row.Date;
    public decimal Hours => Row.Hours;
    public string ProjectName => Row.ProjectName;
    public string AssignmentName => Row.AssignmentName;
    public string TaskName => Row.TaskName;
    public string CategoryName => Row.CategoryName;
    public string Description => Row.Description;
    public bool CanOverride { get; } = canOverride;
    public bool HasUnavailableOverride => !CanOverride && Row.BillableOverride.HasValue;
    public string DurationHint => Row.OriginalHours is decimal original ? $"Edited from {original:0.00} h. Double-click to edit again." : "Double-click to edit hours. Single-clicking does not change them.";
    public string DefaultBillingLabel => billing?.DefaultFor(Row.Project, Row.Task) switch { true => "Billable - Default", false => "Nonbillable - Default", _ => "Default unavailable" };
    public string BillingHint => CanOverride ? "Changes billing only. Assignment and task stay the same. Related email activities regroup and hours recalculate." : "Timekeeper could not confirm Modify access to Billable Override. Check the message below the table. Quickbase default remains available.";
    public int BillingIndex
    {
        get => Row.BillableOverride is null ? 0 : Row.BillableOverride.Value ? 1 : 2;
        set
        {
            if (value is < 0 or > 2 || value == BillingIndex || (!CanOverride && value != 0)) return;
            Row = Row with { BillableOverride = value == 0 ? null : value == 1 };
            PropertyChanged?.Invoke(this, new(nameof(BillingIndex)));
            PropertyChanged?.Invoke(this, new(nameof(BillingLight)));
            PropertyChanged?.Invoke(this, new(nameof(BillingLabel)));
            PropertyChanged?.Invoke(this, new(nameof(HasUnavailableOverride)));
            changed();
        }
    }
    public string BillingLabel => BillingIndex switch { 1 => "Billable", 2 => "Nonbillable", _ => DefaultBillingLabel };
    public Brush BillingLight => new SolidColorBrush((Row.BillableOverride ?? billing?.DefaultFor(Row.Project, Row.Task)) switch { true => Color.FromRgb(63, 203, 125), false => Color.FromRgb(139, 75, 78), _ => Color.FromRgb(125, 133, 143) });
    public event PropertyChangedEventHandler? PropertyChanged;
}
