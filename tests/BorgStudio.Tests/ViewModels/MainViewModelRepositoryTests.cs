using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Repositories;
using BorgStudio.Providers.Local;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

public sealed class MainViewModelRepositoryTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly FakeBorg _borg = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly RepositoryStore _store;

    public MainViewModelRepositoryTests() => _store = new RepositoryStore(_directory.Combine("repositories.json"));

    public void Dispose() => _directory.Dispose();

    private async Task<MainViewModel> StartAsync()
    {
        var plugins = PluginCatalog.Load([new LocalProviderPlugin()], "no-plugins-here");
        var viewModel = new MainViewModel(new AppServices(_borg.Detector(), plugins, _store, _secrets, new BorgClient(_borg), _dialogs));
        await viewModel.CheckBorgCommand.ExecuteAsync(null);
        _borg.Calls.Clear();
        return viewModel;
    }

    private RepositoryConfig Saved(PassphraseMode mode = PassphraseMode.Stored, string name = "Disk") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "local",
        ProviderValues = new Dictionary<string, string> { ["path"] = _directory.Combine(name) },
        PassphraseMode = mode,
        EncryptionMode = "repokey-blake2",
        BorgRepositoryId = "repo-id-1",
    };

    [Fact]
    public async Task Loads_saved_repositories_and_selects_the_first()
    {
        _store.Save([Saved(name: "A"), Saved(name: "B")]);

        var viewModel = await StartAsync();

        Assert.Equal(["A", "B"], viewModel.Repositories.Select(item => item.Name));
        Assert.Equal("A", viewModel.SelectedRepository?.Name);
        Assert.True(viewModel.TestRepositoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Adding_a_new_repository_saves_it_and_offers_the_key_backup()
    {
        var viewModel = await StartAsync();
        _dialogs.Editor = async editor =>
        {
            editor.Name = "New";
            editor.Fields.Single().Value = _directory.Combine("new");
            editor.CreateNew = true;
            editor.Passphrase = editor.PassphraseConfirmation = "secret";
            await editor.ConfirmCommand.ExecuteAsync(null);
        };
        _dialogs.ConfirmAnswers.Enqueue(true);
        _dialogs.SaveFile = _directory.Combine("new.key");

        await viewModel.AddRepositoryCommand.ExecuteAsync(null);

        var item = Assert.Single(viewModel.Repositories);
        Assert.Same(item, viewModel.SelectedRepository);
        Assert.Equal("New", Assert.Single(_store.Load().Repositories).Name);
        Assert.Contains(Strings.BackupKeyTitle, _dialogs.Shown);
        Assert.Equal(["init", "info", "key"], _borg.Commands);
        Assert.True(item.HasSuccess);
        Assert.Contains(_directory.Combine("new.key"), item.ResultText);
    }

    [Fact]
    public async Task Testing_uses_the_stored_passphrase()
    {
        var repository = Saved();
        _store.Save([repository]);
        _secrets.Secrets[repository.SecretKey] = "secret";
        _borg.CorrectPassphrase = "secret";
        var viewModel = await StartAsync();

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Equal("secret", Assert.Single(_borg.Calls).Passphrase);
        Assert.True(viewModel.SelectedRepository!.HasSuccess);
        Assert.Equal(Strings.TestSucceededNoDate, viewModel.SelectedRepository.ResultText);
    }

    [Fact]
    public async Task Testing_asks_for_the_passphrase_and_can_be_cancelled()
    {
        _store.Save([Saved(PassphraseMode.Ask)]);
        _borg.CorrectPassphrase = "secret";
        var viewModel = await StartAsync();

        _dialogs.Passphrases.Enqueue(null);
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);
        Assert.Empty(_borg.Calls);

        _dialogs.Passphrases.Enqueue("wrong");
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);
        Assert.True(viewModel.SelectedRepository!.HasError);
        Assert.Equal(Strings.BorgErrorPassphraseWrong, viewModel.SelectedRepository.ResultText);
    }

    [Fact]
    public async Task Warns_when_the_location_holds_another_repository()
    {
        _store.Save([Saved() with { BorgRepositoryId = "some-other-id" }]);
        _secrets.Secrets[_store.Load().Repositories[0].SecretKey] = "secret";
        var viewModel = await StartAsync();

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.True(viewModel.SelectedRepository!.HasError);
        Assert.Contains(Strings.RepositoryIdChanged, viewModel.SelectedRepository.ResultText);
    }

    [Fact]
    public async Task Borg2_repositories_are_refused_before_running_borg()
    {
        _store.Save([Saved() with { BorgVersion = BorgVersionPreference.Borg2 }]);
        var viewModel = await StartAsync();

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Empty(_borg.Calls);
        Assert.Equal(Strings.Borg2NotSupported, viewModel.SelectedRepository!.ResultText);
    }

    [Fact]
    public async Task Removing_deletes_only_the_list_entry_and_the_stored_passphrase()
    {
        var repository = Saved();
        _store.Save([repository, Saved(name: "Other")]);
        _secrets.Secrets[repository.SecretKey] = "secret";
        var viewModel = await StartAsync();

        _dialogs.ConfirmAnswers.Enqueue(false);
        await viewModel.RemoveRepositoryCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.Repositories.Count);

        _dialogs.ConfirmAnswers.Enqueue(true);
        await viewModel.RemoveRepositoryCommand.ExecuteAsync(null);

        Assert.Equal("Other", Assert.Single(viewModel.Repositories).Name);
        Assert.Equal("Other", viewModel.SelectedRepository?.Name);
        Assert.Single(_store.Load().Repositories);
        Assert.Empty(_secrets.Secrets);
        Assert.Empty(_borg.Calls);
    }
}
