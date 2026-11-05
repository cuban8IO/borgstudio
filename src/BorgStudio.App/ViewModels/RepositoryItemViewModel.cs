using BorgStudio.App.Resources;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BorgStudio.App.ViewModels;

/// <summary>A repository in the list and its details panel.</summary>
public sealed partial class RepositoryItemViewModel(RepositoryConfig config, PluginCatalog plugins) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Provider), nameof(ProviderName), nameof(Location), nameof(LocationText),
        nameof(EncryptionText), nameof(IsKeyfile), nameof(IsEncrypted), nameof(PassphraseText), nameof(BorgVersionText),
        nameof(IsSsh), nameof(SshKeyText))]
    public partial RepositoryConfig Config { get; set; } = config;

    public string Name => Config.Name;

    /// <summary><c>null</c> if the plugin providing it is gone.</summary>
    public IRepositoryProvider? Provider => plugins.FindRepositoryProvider(Config.ProviderId);

    public string ProviderName => Provider?.DisplayName ?? Config.ProviderId;

    public RepositoryLocation? Location
    {
        get
        {
            try
            {
                return Provider?.GetLocation(Config.ProviderValues);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or FormatException)
            {
                return null;
            }
        }
    }

    public string LocationText => Location?.Url ?? BorgTexts.Format(Strings.ProviderMissing, Config.ProviderId);

    public string EncryptionText => Config.EncryptionMode ?? Strings.EncryptionUnknown;

    public bool IsEncrypted => Config.EncryptionMode is not "none";

    public bool IsKeyfile => Config.EncryptionMode?.StartsWith("keyfile", StringComparison.Ordinal) == true;

    public string PassphraseText => BorgTexts.PassphraseMode(Config.PassphraseMode);

    public string BorgVersionText => BorgTexts.BorgVersion(Config.BorgVersion);

    public bool IsSsh => Provider?.UsesSsh == true;

    /// <summary>Name of the key in the vault; <c>null</c> while unknown (locked vault) or if the key is gone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SshKeyText))]
    public partial string? SshKeyName { get; set; }

    public string SshKeyText => Config switch
    {
        { HasLegacySshKey: true } => Strings.SshKeyLegacy,
        { SshKeyId: null } => Strings.SshKeyMissing,
        _ when SshKeyName is null => Strings.SshKeyMissing,
        { SshKeyRestricted: true } => $"{SshKeyName} {Strings.SshKeyRestrictedSuffix}",
        _ => SshKeyName,
    };

    /// <summary>Outcome of the last action on this repository (test, key export, ...).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(HasSuccess), nameof(HasError))]
    public partial string? ResultText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuccess), nameof(HasError))]
    public partial bool ResultIsError { get; private set; }

    public bool HasResult => ResultText is not null;

    public bool HasSuccess => HasResult && !ResultIsError;

    public bool HasError => HasResult && ResultIsError;

    public void ShowResult(string text, bool isError)
    {
        ResultIsError = isError;
        ResultText = text;
    }
}
