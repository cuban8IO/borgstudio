using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>One schedule in the job editor: daily, on weekdays or every few hours, at a time of day.</summary>
public sealed partial class ScheduleItemViewModel : ObservableObject
{
    private readonly Action<ScheduleItemViewModel> _remove;

    public ScheduleItemViewModel(BackupSchedule schedule, Action<ScheduleItemViewModel> remove)
    {
        _remove = remove;
        SelectedKind = KindOptions.First(option => option.Value == schedule.Kind);
        Time = schedule.Time.ToTimeSpan();
        Hours = schedule.Hours;
        // Monday first, as calendars in Germany (and ISO) start the week.
        Days = [.. Enumerable.Range(1, 7).Select(offset => (DayOfWeek)(offset % 7)).Select(day =>
            new ScheduleDayViewModel(day, CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day), schedule.Days.Contains(day)))];
    }

    public IReadOnlyList<Option<ScheduleKind>> KindOptions { get; } =
    [
        new(ScheduleKind.Daily, Strings.JobEditorScheduleDaily),
        new(ScheduleKind.Weekdays, Strings.JobEditorScheduleWeekdays),
        new(ScheduleKind.EveryHours, Strings.JobEditorScheduleEveryHours),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeekdays), nameof(IsEveryHours), nameof(TimeLabel))]
    public partial Option<ScheduleKind> SelectedKind { get; set; }

    public bool IsWeekdays => SelectedKind?.Value == ScheduleKind.Weekdays;

    public bool IsEveryHours => SelectedKind?.Value == ScheduleKind.EveryHours;

    /// <summary>"um" a time, or "ab" the first of several.</summary>
    public string TimeLabel => IsEveryHours ? Strings.JobEditorScheduleFrom : Strings.JobEditorScheduleAt;

    [ObservableProperty]
    public partial TimeSpan? Time { get; set; }

    [ObservableProperty]
    public partial decimal? Hours { get; set; }

    public IReadOnlyList<ScheduleDayViewModel> Days { get; }

    [RelayCommand]
    private void Remove() => _remove(this);

    public BackupSchedule ToSchedule() => new()
    {
        Kind = SelectedKind.Value,
        Time = TimeOnly.FromTimeSpan(Time is { } time && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) ? time : new TimeSpan(20, 0, 0)),
        Days = [.. Days.Where(day => day.IsChecked).Select(day => day.Day)],
        Hours = (int)Math.Clamp(Hours ?? 4, 1, 12),
    };
}

/// <summary>A weekday to tick for a weekday schedule.</summary>
public sealed partial class ScheduleDayViewModel(DayOfWeek day, string label, bool isChecked) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;
}
