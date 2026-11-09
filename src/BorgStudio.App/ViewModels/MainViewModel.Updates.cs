using System.Text.Json;
using BorgStudio.App.Resources;
using BorgStudio.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>Updates: a banner when a new release is out, the update window, the outcome after installing.</summary>
public partial class MainViewModel
{
    public Updater Updates => _services.Updates;

    /// <summary>A newer release found by the automatic check (the banner shows while set).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateBannerText))]
    public partial UpdateCheck? AvailableUpdate { get; private set; }

    public bool HasUpdate => AvailableUpdate is { IsNewer: true };

    public string UpdateBannerText => AvailableUpdate?.Latest is { } release ? BorgTexts.Format(Strings.UpdateAvailable, release.Version) : "";

    /// <summary>Looks for a newer release when enabled; quietly gives up without a connection.</summary>
    public async Task CheckForUpdatesInBackgroundAsync()
    {
        // A local build is never replaced, so it does not ask GitHub either.
        if (!Settings.CheckForUpdates || Updates.Current is null)
            return;
        try
        {
            var check = await Updates.CheckAsync();
            OnViewModelThread(() => AvailableUpdate = check.IsNewer ? check : null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
        }
    }

    /// <summary>After an update: says whether it worked; removes the downloaded files.</summary>
    public void ShowUpdateOutcome()
    {
        if (Updates.TakeOutcome() is not { } outcome)
            return;
        Notice = outcome.Succeeded
            ? BorgTexts.Format(Strings.UpdateInstalledNotice, outcome.Version)
            : BorgTexts.Format(Strings.UpdateFailedNotice, outcome.Version, outcome.Error ?? "");
    }

    /// <summary>Opens the update window; with a release found already, it shows that one without asking again.</summary>
    [RelayCommand]
    private Task ShowUpdateAsync() => _services.Dialogs.ShowUpdateAsync(new UpdateViewModel(this, _services.Dialogs, AvailableUpdate));

    /// <summary>"Later": hides the banner until the next start.</summary>
    [RelayCommand]
    private void DismissUpdate() => AvailableUpdate = null;
}
