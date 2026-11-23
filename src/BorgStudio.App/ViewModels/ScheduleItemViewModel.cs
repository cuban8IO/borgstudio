using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// One schedule in the job editor (and the repository editor's checks): daily, on weekdays, every few hours or monthly
/// (on the nth weekday or a day of the month), at a time of day.
/// </summary>
public sealed partial class ScheduleItemViewModel : ObservableObject
{
    private readonly Action<ScheduleItemViewModel> _remove;

    public ScheduleItemViewModel(BackupSchedule schedule, Action<ScheduleItemViewModel> remove)
    {
        _remove = remove;
        SelectedKind = KindOptions.First(option => option.Value == schedule.Kind);
        Time = schedule.Time.ToTimeSpan();
        Hours = schedule.Hours;
        SelectedMinutes = MinuteOptions.FirstOrDefault(option => option.Value == schedule.Minutes) ?? MinuteOptions[5];
        // Monday first, as calendars in Germany (and ISO) start the week.
        Days = [.. WeekOrder.Select(day =>
            new ScheduleDayViewModel(day, CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day), schedule.Days.Contains(day)))];
        WeekdayOptions = [.. WeekOrder.Select(day => new Option<DayOfWeek>(day, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day)))];
        SelectedWeek = WeekOptions.FirstOrDefault(option => option.Value == schedule.Week) ?? WeekOptions[0];
        SelectedWeekday = WeekdayOptions.First(option => option.Value == schedule.Weekday);
        SelectedDayOfMonth = DayOfMonthOptions.FirstOrDefault(option => option.Value == schedule.DayOfMonth) ?? DayOfMonthOptions[0];
    }

    private static IEnumerable<DayOfWeek> WeekOrder => Enumerable.Range(1, 7).Select(offset => (DayOfWeek)(offset % 7));

    public IReadOnlyList<Option<ScheduleKind>> KindOptions { get; } =
    [
        new(ScheduleKind.Daily, Strings.JobEditorScheduleDaily),
        new(ScheduleKind.Weekdays, Strings.JobEditorScheduleWeekdays),
        new(ScheduleKind.EveryMinutes, Strings.JobEditorScheduleEveryMinutes),
        new(ScheduleKind.EveryHours, Strings.JobEditorScheduleEveryHours),
        new(ScheduleKind.MonthlyWeekday, Strings.ScheduleMonthlyWeekday),
        new(ScheduleKind.MonthlyDay, Strings.ScheduleMonthlyDay),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeekdays), nameof(IsEveryHours), nameof(IsMonthlyWeekday), nameof(IsMonthlyDay), nameof(IsMonthly),
        nameof(MonthlyLabel), nameof(TimeLabel), nameof(IsEveryMinutes), nameof(HasTime))]
    public partial Option<ScheduleKind> SelectedKind { get; set; }

    public bool IsWeekdays => SelectedKind?.Value == ScheduleKind.Weekdays;

    public bool IsEveryHours => SelectedKind?.Value == ScheduleKind.EveryHours;

    public bool IsEveryMinutes => SelectedKind?.Value == ScheduleKind.EveryMinutes;

    /// <summary>Every N minutes runs around the clock: no time of day.</summary>
    public bool HasTime => !IsEveryMinutes;

    /// <summary>"1 Minute", "2 Minuten", … "30 Minuten".</summary>
    public IReadOnlyList<Option<int>> MinuteOptions { get; } =
        [.. BackupSchedule.MinuteSteps.Select(minutes => new Option<int>(minutes,
            minutes == 1 ? Strings.ScheduleOneMinute : BorgTexts.Format(Strings.ScheduleMinutes, minutes)))];

    [ObservableProperty]
    public partial Option<int> SelectedMinutes { get; set; }

    public bool IsMonthlyWeekday => SelectedKind?.Value == ScheduleKind.MonthlyWeekday;

    public bool IsMonthlyDay => SelectedKind?.Value == ScheduleKind.MonthlyDay;

    public bool IsMonthly => IsMonthlyWeekday || IsMonthlyDay;

    /// <summary>"am" (the first Sunday / the 1st).</summary>
    public string MonthlyLabel => IsMonthlyWeekday ? Strings.ScheduleOnWeekday : Strings.ScheduleOnDay;

    /// <summary>"um" a time, or "ab" the first of several.</summary>
    public string TimeLabel => IsEveryHours ? Strings.JobEditorScheduleFrom : Strings.JobEditorScheduleAt;

    [ObservableProperty]
    public partial TimeSpan? Time { get; set; }

    [ObservableProperty]
    public partial decimal? Hours { get; set; }

    public IReadOnlyList<ScheduleDayViewModel> Days { get; }

    // --- Monthly ---

    /// <summary>"ersten", "zweiten", "dritten", "vierten", "letzten".</summary>
    public IReadOnlyList<Option<int>> WeekOptions { get; } =
    [
        new(1, Strings.ScheduleWeekFirst),
        new(2, Strings.ScheduleWeekSecond),
        new(3, Strings.ScheduleWeekThird),
        new(4, Strings.ScheduleWeekFourth),
        new(BackupSchedule.Last, Strings.ScheduleWeekLast),
    ];

    [ObservableProperty]
    public partial Option<int> SelectedWeek { get; set; }

    public IReadOnlyList<Option<DayOfWeek>> WeekdayOptions { get; }

    [ObservableProperty]
    public partial Option<DayOfWeek> SelectedWeekday { get; set; }

    /// <summary>"1." to "28." and "letzten Tag" (later days are not in every month).</summary>
    public IReadOnlyList<Option<int>> DayOfMonthOptions { get; } =
    [
        .. Enumerable.Range(1, 28).Select(day => new Option<int>(day, BorgTexts.Format(Strings.ScheduleDayOfMonth, day))),
        new(BackupSchedule.Last, Strings.ScheduleLastDayOfMonth),
    ];

    [ObservableProperty]
    public partial Option<int> SelectedDayOfMonth { get; set; }

    [RelayCommand]
    private void Remove() => _remove(this);

    public BackupSchedule ToSchedule() => new()
    {
        Kind = SelectedKind.Value,
        Time = TimeOnly.FromTimeSpan(Time is { } time && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) ? time : new TimeSpan(20, 0, 0)),
        Days = [.. Days.Where(day => day.IsChecked).Select(day => day.Day)],
        Hours = (int)Math.Clamp(Hours ?? 4, 1, 12),
        Minutes = SelectedMinutes.Value,
        Week = SelectedWeek.Value,
        Weekday = SelectedWeekday.Value,
        DayOfMonth = SelectedDayOfMonth.Value,
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
