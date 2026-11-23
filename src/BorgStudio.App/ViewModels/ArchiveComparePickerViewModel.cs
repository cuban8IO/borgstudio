using System.Globalization;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>Choosing the two archives to compare: any two of the repository, the older one first.</summary>
public sealed partial class ArchiveComparePickerViewModel : ObservableObject
{
    public ArchiveComparePickerViewModel(IReadOnlyList<BorgArchive> archives, BorgArchive? older, BorgArchive? newer)
    {
        Options = archives.OrderByDescending(archive => archive.Start).Select(archive => new ArchiveOption(archive)).ToList();
        Older = Options.FirstOrDefault(option => option.Archive.Name == older?.Name) ?? Options.Skip(1).FirstOrDefault();
        Newer = Options.FirstOrDefault(option => option.Archive.Name == newer?.Name) ?? Options.FirstOrDefault();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ArchiveOption> Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompare), nameof(Problem))]
    public partial ArchiveOption? Older { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompare), nameof(Problem))]
    public partial ArchiveOption? Newer { get; set; }

    public bool CanCompare => Older is not null && Newer is not null && Older != Newer;

    /// <summary>Why "Vergleichen" is not possible; <c>null</c> when it is.</summary>
    public string? Problem => Older is not null && Older == Newer ? Resources.Strings.CompareSameArchive : null;

    [RelayCommand]
    private void Swap() => (Older, Newer) = (Newer, Older);

    /// <summary>The choice, older first by date (whatever the lists say); <c>null</c> if it is not a pair.</summary>
    public (BorgArchive Older, BorgArchive Newer)? Pair => CanCompare
        ? Older!.Archive.Start <= Newer!.Archive.Start ? (Older.Archive, Newer.Archive) : (Newer.Archive, Older.Archive)
        : null;
}

/// <summary>An archive in the lists: its name and when it was made.</summary>
public sealed record ArchiveOption(BorgArchive Archive)
{
    public override string ToString() =>
        $"{Archive.Name}  ·  {Archive.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
}
