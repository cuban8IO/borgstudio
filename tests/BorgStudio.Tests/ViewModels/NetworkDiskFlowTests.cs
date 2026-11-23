using BorgStudio.App.ViewModels;
using BorgStudio.Providers.NetworkDisk;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>A repository on a network disk: set up and connected by the editor, then logged in to before each use.</summary>
public sealed class NetworkDiskFlowTests : IDisposable
{
    private readonly TestServices _test = new();

    public NetworkDiskFlowTests()
    {
        NetworkDiskProvider.AppearTimeout = TimeSpan.FromMilliseconds(300);
        NetworkDiskProvider.PollInterval = TimeSpan.FromMilliseconds(20);
        _test.NetworkDisks.Volume = Volume;
        Directory.CreateDirectory(Path.Combine(Volume, "borg"));
    }

    public void Dispose() => _test.Dispose();

    private string Volume => _test.Directory.Combine("volumes", "E");

    private static FieldViewModel Field(RepositoryEditorViewModel editor, string key) => editor.Fields.Single(field => field.Key == key);

    private RepositoryEditorViewModel NewEditor()
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "network-disk");
        editor.NextCommand.Execute(null);
        editor.Name = "NAS LUN";
        Field(editor, "portal").Value = "nas.local";
        Field(editor, "target").Value = "iqn.2000-01.com.synology:nas.backup";
        Field(editor, "folder").Value = "borg/laptop";
        editor.CreateNew = true;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        editor.StorageAsk = true;
        return editor;
    }

    [Fact]
    public async Task The_editor_sets_up_logs_in_creates_the_repository_on_the_disk_and_logs_out()
    {
        var editor = NewEditor();

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.Single(_test.NetworkDisks.SetUps);
        Assert.Single(_test.NetworkDisks.Connects);
        Assert.Single(_test.NetworkDisks.Disconnects);
        var repository = Path.Combine(Volume, "borg", "laptop");
        Assert.Contains(repository, _test.Borg.Calls.Single(call => call.Arguments[0] == "init").Arguments);

        var saved = editor.Result!.Repository;
        Assert.Equal(repository, saved.ProviderValues[NetworkDiskProvider.LastPathKey]);
        Assert.Equal(saved.Id.ToString("N"), saved.ProviderValues[NetworkDiskProvider.DiskIdKey]);
        Assert.True(saved.ProviderValues.ContainsKey(NetworkDiskProvider.SetupKey));
    }

    [Fact]
    public async Task Refused_rights_save_nothing()
    {
        _test.NetworkDisks.SetUpRefusal = "Administrator rights were not given.";
        var editor = NewEditor();

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Administrator rights were not given.", Assert.Single(editor.Errors));
        Assert.Null(editor.Result);
        Assert.Empty(_test.NetworkDisks.Connects);
        Assert.Empty(_test.Borg.Calls);
    }

    [Fact]
    public async Task Saving_again_does_not_set_up_again()
    {
        var editor = NewEditor();
        await editor.ConfirmCommand.ExecuteAsync(null);
        var saved = editor.Result!.Repository;
        _test.Store.Save([saved]);

        var edit = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, saved);
        edit.Name = "NAS";
        await edit.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(edit.Errors);
        Assert.Single(_test.NetworkDisks.SetUps);
        Assert.Equal(saved.ProviderValues[NetworkDiskProvider.LastPathKey], edit.Result!.Repository.ProviderValues[NetworkDiskProvider.LastPathKey]);
    }
}
