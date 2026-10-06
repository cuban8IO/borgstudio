using BorgStudio.Plugins;

namespace BorgStudio.SamplePlugin;

/// <summary>Entry point: BorgStudio creates this class and calls <see cref="Register"/> at startup.</summary>
public sealed class SamplePlugin : IBorgStudioPlugin
{
    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new SampleRepositoryProvider());
}

/// <summary>
/// A made-up backup service ("backup.example.com") reachable via SSH, to show the moving parts of a provider:
/// fields, validation and the borg location built from them.
/// </summary>
public sealed class SampleRepositoryProvider : IRepositoryProvider
{
    private const string UserKey = "user";
    private const string FolderKey = "folder";

    // Never change an id once repositories use it.
    public string Id => "sample";

    // Real plugins return these texts in CultureInfo.CurrentUICulture (e.g. from .resx files).
    public string DisplayName => "Sample provider";

    public string Description => "Example plugin: repositories on backup.example.com.";

    public IReadOnlyList<ProviderField> Fields =>
    [
        new(UserKey, "Account") { Hint = "Your account name, e.g. jdoe" },
        new(FolderKey, "Repository folder") { DefaultValue = "borg", Hint = "Folder in your home directory" },
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(values.GetValueOrDefault(UserKey)))
            errors.Add("Please enter your account name.");
        if (string.IsNullOrWhiteSpace(values.GetValueOrDefault(FolderKey)))
            errors.Add("Please enter a repository folder.");
        return errors;
    }

    public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values) =>
        new($"ssh://{values[UserKey].Trim()}@backup.example.com/./{values[FolderKey].Trim()}")
        {
            BorgArguments = ["--remote-path=borg-1.4"],
        };
}
