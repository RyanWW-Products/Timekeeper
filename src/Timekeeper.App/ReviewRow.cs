using System.ComponentModel;
using System.Windows.Media;
using Timekeeper.Core;

namespace Timekeeper.App;

internal sealed class ReviewRow(VerifiedRow row, bool canOverride, Action changed) : INotifyPropertyChanged
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
    public string BillingHint => CanOverride ? "Changes billing only. Assignment and task stay the same. Related email activities regroup and hours recalculate." : "Timekeeper could not confirm Modify access to Billable Override. Check the message below the table. Quickbase default remains available.";
    public int BillingIndex
    {
        get => Row.BillableOverride is null ? 0 : Row.BillableOverride.Value ? 1 : 2;
        set
        {
            if (value is < 0 or > 2 || value == BillingIndex) return;
            Row = Row with { BillableOverride = value == 0 ? null : value == 1 };
            PropertyChanged?.Invoke(this, new(nameof(BillingIndex)));
            PropertyChanged?.Invoke(this, new(nameof(BillingLight)));
            PropertyChanged?.Invoke(this, new(nameof(BillingLabel)));
            changed();
        }
    }
    public string BillingLabel => BillingIndex switch { 1 => "Billable", 2 => "Non-billable", _ => "Quickbase default (determined when saved)" };
    public Brush BillingLight => new SolidColorBrush(BillingIndex switch { 1 => Color.FromRgb(63, 203, 125), 2 => Color.FromRgb(139, 75, 78), _ => Color.FromRgb(125, 133, 143) });
    public event PropertyChangedEventHandler? PropertyChanged;
}
