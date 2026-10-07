using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

public sealed class MainViewModelRepositoryTests : IDisposable
{
    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private FakeBorg Borg => _test.Borg;
    private FakeDialogs Dialogs => _test.Dialogs;
    private RepositoryStore Store => _test.Store;

    private async Task<MainViewModel> StartAsync()
    {
        var viewModel = new MainViewModel(_test.Services);
        await viewModel.CheckBorgCommand.ExecuteAsync(null);
        Borg.Calls.Clear();
        Borg.Environments.Clear();
        return viewModel;
    }

    private RepositoryConfig Saved(PassphraseMode mode = PassphraseMode.Stored, string name = "Disk") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "local",
        ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine(name) },
        PassphraseMode = mode,
        EncryptionMode = "repokey-blake2",
        BorgRepositoryId = "repo-id-1",
    };

    [Fact]
    public async Task Loads_saved_repositories_and_selects_the_first()
    {
        Store.Save([Saved(name: "A"), Saved(name: "B")]);

        var viewModel = await StartAsync();

        Assert.Equal(["A", "B"], viewModel.Repositories.Select(item => item.Name));
        Assert.Equal("A", viewModel.SelectedRepository?.Name);
        Assert.True(viewModel.TestRepositoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Adding_a_new_repository_saves_it_and_offers_the_key_backup()
    {
        var viewModel = await StartAsync();
        Dialogs.Editor = async editor =>
        {
            editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "local");
            editor.NextCommand.Execute(null);
            editor.Name = "New";
            editor.Fields.Single().Value = _test.Directory.Combine("new");
            editor.CreateNew = true;
            editor.Passphrase = editor.PassphraseConfirmation = "secret";
            await editor.ConfirmCommand.ExecuteAsync(null);
        };
        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.SaveFile = _test.Directory.Combine("new.key");

        await viewModel.AddRepositoryCommand.ExecuteAsync(null);

        var item = Assert.Single(viewModel.Repositories);
        Assert.Same(item, viewModel.SelectedRepository);
        Assert.Equal("New", Assert.Single(Store.Load().Repositories).Name);
        Assert.Contains(Strings.BackupKeyTitle, Dialogs.Shown);
        Assert.Equal(["init", "info", "key"], Borg.Commands);
        Assert.True(item.HasSuccess);
        Assert.Contains(_test.Directory.Combine("new.key"), item.ResultText);
    }

    [Fact]
    public async Task Testing_uses_the_stored_passphrase()
    {
        var repository = Saved();
        Store.Save([repository]);
        _test.Secrets.Secrets[repository.SecretKey] = "secret";
        Borg.CorrectPassphrase = "secret";
        var viewModel = await StartAsync();

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Equal("secret", Assert.Single(Borg.Calls).Passphrase);
        Assert.True(viewModel.SelectedRepository!.HasSuccess);
        Assert.Equal(Strings.TestSucceededNoDate, viewModel.SelectedRepository.ResultText);
    }

    [Fact]
    public async Task Testing_asks_for_the_passphrase_and_can_be_cancelled()
    {
        Store.Save([Saved(PassphraseMode.Ask)]);
        Borg.CorrectPassphrase = "secret";
        var viewModel = await StartAsync();

        Dialogs.Passphrases.Enqueue(null);
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);
        Assert.Empty(Borg.Calls);

        Dialogs.Passphrases.Enqueue("wrong");
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);
        Assert.True(viewModel.SelectedRepository!.HasError);
        Assert.Equal(Strings.BorgErrorPassphraseWrong, viewModel.SelectedRepository.ResultText);
    }

    [Fact]
    public async Task Warns_when_the_location_holds_another_repository()
    {
        Store.Save([Saved() with { BorgRepositoryId = "some-other-id" }]);
        _test.Secrets.Secrets[Store.Load().Repositories[0].SecretKey] = "secret";
        var viewModel = await StartAsync();

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.True(viewModel.SelectedRepository!.HasError);
        Assert.Contains(Strings.RepositoryIdChanged, viewModel.SelectedRepository.ResultText);
    }

    [Fact]
    public async Task Borg2_repositories_are_refused_before_running_borg()
    {
        Store.Save([Saved() with { BorgVersion = BorgVersionPreference.Borg2 }]);
        var viewModel = await StartAsync();

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Empty(Borg.Calls);
        Assert.Equal(Strings.Borg2NotSupported, viewModel.SelectedRepository!.ResultText);
    }

    [Fact]
    public async Task Removing_deletes_only_the_list_entry_and_the_stored_passphrase()
    {
        var repository = Saved();
        Store.Save([repository, Saved(name: "Other")]);
        _test.Secrets.Secrets[repository.SecretKey] = "secret";
        var viewModel = await StartAsync();

        Dialogs.ConfirmAnswers.Enqueue(false);
        await viewModel.RemoveRepositoryCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.Repositories.Count);

        Dialogs.ConfirmAnswers.Enqueue(true);
        await viewModel.RemoveRepositoryCommand.ExecuteAsync(null);

        Assert.Equal("Other", Assert.Single(viewModel.Repositories).Name);
        Assert.Equal("Other", viewModel.SelectedRepository?.Name);
        Assert.Single(Store.Load().Repositories);
        Assert.Empty(_test.Secrets.Secrets);
        Assert.Empty(Borg.Calls);
    }
}
