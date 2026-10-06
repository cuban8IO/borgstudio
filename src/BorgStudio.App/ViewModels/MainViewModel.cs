using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly BorgDetector _borgDetector;

    public MainViewModel() : this(BorgDetector.CreateDefault())
    {
    }

    public MainViewModel(BorgDetector borgDetector)
    {
        _borgDetector = borgDetector;
    }

    /// <summary>Status bar text, e.g. "borg 1.4.5 · /usr/bin/borg".</summary>
    [ObservableProperty]
    public partial string BorgStatus { get; set; } = Strings.BorgChecking;

    [ObservableProperty]
    public partial bool HasBorgProblem { get; set; }

    [ObservableProperty]
    public partial string? BorgProblemTitle { get; set; }

    [ObservableProperty]
    public partial string? BorgProblemText { get; set; }

    public Uri InstallationGuideUrl { get; } = new("https://borgbackup.readthedocs.io/en/stable/installation.html");

    [RelayCommand]
    private async Task CheckBorgAsync()
    {
        BorgStatus = Strings.BorgChecking;
        try
        {
            ShowBorgResult(await Task.Run(() => _borgDetector.DetectAsync()));
        }
        catch (Exception exception)
        {
            BorgStatus = Strings.BorgStatusNotFound;
            ShowProblem(Strings.BorgCheckFailedTitle, exception.Message);
        }
    }

    private void ShowBorgResult(BorgInstallation? borg)
    {
        if (borg is null)
        {
            BorgStatus = Strings.BorgStatusNotFound;
            var text = Format(Strings.BorgMissingText, BorgCompatibility.MinimumVersion);
            ShowProblem(Strings.BorgMissingTitle,
                OperatingSystem.IsWindows() ? text + " " + Strings.BorgMissingWindowsHint : text);
            return;
        }

        BorgStatus = Format(borg.Runtime == BorgRuntime.Wsl ? Strings.BorgStatusWsl : Strings.BorgStatusNative,
            borg.Version, borg.Path);

        switch (borg.Support)
        {
            case BorgSupport.TooOld:
                ShowProblem(Format(Strings.BorgTooOldTitle, borg.Version),
                    Format(Strings.BorgTooOldText, BorgCompatibility.MinimumVersion));
                break;
            case BorgSupport.NotYetSupported:
                ShowProblem(Format(Strings.BorgNotYetSupportedTitle, borg.Version), Strings.BorgNotYetSupportedText);
                break;
            default:
                HasBorgProblem = false;
                break;
        }
    }

    private void ShowProblem(string title, string text)
    {
        BorgProblemTitle = title;
        BorgProblemText = text;
        HasBorgProblem = true;
    }

    private static string Format(string format, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, format, arguments);
}
