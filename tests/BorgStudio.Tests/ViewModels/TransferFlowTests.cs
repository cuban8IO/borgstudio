using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Ssh;
using BorgStudio.Core.Transfer;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Exporting everything on one computer and importing it on another (two separate test environments).</summary>
public sealed class TransferFlowTests : IDisposable
{
    private const string Pin = "123456";
    private const string ExportPassword = "a long export password";

    private readonly TestServices _source = new();
    private readonly TestServices _target = new();
    private readonly RepositoryConfig _local;
    private readonly RepositoryConfig _nas;
    private readonly SshKeyEntry _key;

    public TransferFlowTests()
    {
        _source.Vault.SetUp(Pin);
        _key = SshKeyEntry.Generate("NAS key", "me", DateTimeOffset.Now);
        _source.Vault.SetKey(_key);
        _local = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _source.Directory.Combine("disk") },
            PassphraseMode = PassphraseMode.Vault,
            EncryptionMode = "repokey-blake2",
            BorgRepositoryId = "aaaa1111aaaa1111",
        };
        _nas = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "NAS",
            ProviderId = "ssh",
            ProviderValues = new Dictionary<string, string>
            {
                ["host"] = "nas.local", ["port"] = "22", ["user"] = "backup", ["path"] = "borg", ["remoteBorg"] = "",
            },
            PassphraseMode = PassphraseMode.Stored,
            EncryptionMode = "keyfile-blake2",
            BorgRepositoryId = "bbbb2222bbbb2222",
            SshKeyId = _key.Id,
            SshKeyRestricted = true,
        };
        _source.Store.Save([_local, _nas]);
        _source.Vault.SetPassphrase(_local.Id, "disk passphrase");
        _source.Secrets.Secrets[_nas.SecretKey] = "nas passphrase";
        _source.KnownHosts.Add("nas.local", 22, FakeSsh.ServerKey);
        _source.Settings.Save(new AppSettings { VaultAutoLockMinutes = 30 });
        _source.Jobs.Save([new Core.Backups.BackupJob { Id = Guid.NewGuid(), Name = "Documents", RepositoryId = _nas.Id, Sources = [@"C:\Users\me\Documents"] }]);
    }

    public void Dispose()
    {
        _source.Dispose();
        _target.Dispose();
    }

    private async Task<MainViewModel> StartAsync(TestServices test)
    {
        var main = new MainViewModel(test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        return main;
    }

    private async Task<string> ExportAsync(bool includeBorgKeys = true)
    {
        var file = _source.Directory.Combine("BorgStudio.borgstudio");
        var export = new ExportViewModel(await StartAsync(_source), _source.Dialogs) { Kdf = TestServices.FastKdf };
        export.Password = export.PasswordConfirmation = ExportPassword;
        export.IncludeBorgKeys = includeBorgKeys;
        _source.Dialogs.SaveFile = file;

        await export.ExportCommand.ExecuteAsync(null);

        Assert.Empty(export.Errors);
        Assert.Empty(export.Problems);
        Assert.True(export.IsDone);
        return file;
    }

    private async Task<(MainViewModel Main, ImportViewModel Import)> OpenImportAsync(string file)
    {
        var main = await StartAsync(_target);
        _target.Dialogs.OpenFile = file;
        _target.Dialogs.Secrets.Enqueue(ExportPassword);
        return (main, (await ImportViewModel.OpenAsync(main, _target.Dialogs))!);
    }

    [Fact]
    public async Task The_export_password_is_checked_before_anything_is_written()
    {
        var export = new ExportViewModel(await StartAsync(_source), _source.Dialogs) { Kdf = TestServices.FastKdf };
        export.Password = "too short";
        await export.ExportCommand.ExecuteAsync(null);
        Assert.Contains(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.ExportPasswordTooShort, 12), export.Errors);

        export.Password = ExportPassword;
        export.PasswordConfirmation = "something else entirely";
        await export.ExportCommand.ExecuteAsync(null);
        Assert.Equal([Strings.ExportPasswordMismatch], export.Errors);
        Assert.False(export.IsDone);
    }

    [Fact]
    public async Task Everything_arrives_on_a_new_computer()
    {
        var file = await ExportAsync();
        var contents = ExportFile.Read(file, ExportPassword);
        Assert.Equal(2, contents.Repositories.Count);
        Assert.Equal("disk passphrase", contents.PassphraseFor(_local.Id));
        Assert.Equal("nas passphrase", contents.PassphraseFor(_nas.Id));
        Assert.Equal(_key, Assert.Single(contents.SshKeys));
        Assert.True(contents.BorgKeyFor(_nas.Id)!.Keyfile);
        Assert.False(contents.BorgKeyFor(_local.Id)!.Keyfile);
        Assert.Equal(30, contents.Settings.VaultAutoLockMinutes);
        Assert.Equal("Documents", Assert.Single(contents.Jobs).Name);

        // Wrong password first, then the right one.
        var main = await StartAsync(_target);
        _target.Dialogs.OpenFile = file;
        _target.Dialogs.Secrets.Enqueue("not the export password");
        _target.Dialogs.Secrets.Enqueue(ExportPassword);
        var import = (await ImportViewModel.OpenAsync(main, _target.Dialogs))!;
        Assert.Equal([null, Strings.ImportPasswordWrong], _target.Dialogs.SecretErrors);
        Assert.Equal(4, import.Items.Count);
        Assert.Contains(import.Items, item => item.Kind == Strings.ImportKindJob && item.Name == "Documents");
        Assert.False(import.HasConflicts);

        // A new computer has no PIN yet: importing sets one up.
        _target.Dialogs.NewPins.Enqueue(Pin);
        await import.ImportCommand.ExecuteAsync(null);

        Assert.True(import.IsDone);
        Assert.Equal(["Disk", "NAS"], main.Repositories.Select(item => item.Name));
        Assert.Equal(2, _target.Store.Load().Repositories.Count);
        Assert.Equal("disk passphrase", _target.Vault.GetPassphrase(_local.Id));
        Assert.Equal("nas passphrase", _target.Secrets.Secrets[_nas.SecretKey]);
        Assert.Equal(_key, _target.Vault.GetKey(_key.Id));
        Assert.Equal($"NAS key {Strings.SshKeyRestrictedSuffix}", main.Repositories[1].SshKeyText);
        Assert.Single(_target.KnownHosts.Find("nas.local", 22));
        Assert.Equal(15, main.Settings.VaultAutoLockMinutes); // merging keeps the settings here
        Assert.Equal(_nas.Id, Assert.Single(_target.Jobs.Load()).RepositoryId);

        if (OperatingSystem.IsWindows())
            Assert.Contains(import.Problems, problem => problem.Contains("NAS", StringComparison.Ordinal));
        else
            Assert.Equal([_target.Borg.ExportedKey], _target.Borg.InstalledKeyfiles);
    }

    [Fact]
    public async Task Without_a_keychain_the_vault_takes_the_passphrase()
    {
        using var noKeychain = new TestServices(keychainAvailable: false);
        var file = await ExportAsync(includeBorgKeys: false);
        var main = await StartAsync(noKeychain);
        noKeychain.Dialogs.OpenFile = file;
        noKeychain.Dialogs.Secrets.Enqueue(ExportPassword);
        noKeychain.Dialogs.NewPins.Enqueue(Pin);
        var import = (await ImportViewModel.OpenAsync(main, noKeychain.Dialogs))!;

        await import.ImportCommand.ExecuteAsync(null);

        Assert.Equal(PassphraseMode.Vault, main.Repositories.Single(item => item.Name == "NAS").Config.PassphraseMode);
        Assert.Equal("nas passphrase", noKeychain.Vault.GetPassphrase(_nas.Id));
    }

    [Fact]
    public async Task Merging_replaces_existing_entries_only_where_ticked()
    {
        var file = await ExportAsync(includeBorgKeys: false);
        _target.Vault.SetUp(Pin);
        _target.Store.Save([_local with { Name = "Disk here" }, _nas with { Name = "NAS here" }]);
        var (main, import) = await OpenImportAsync(file);
        Assert.True(import.HasConflicts);
        var disk = import.Items.Single(item => item.Id == _local.Id);
        Assert.True(disk.Exists);
        Assert.True(disk.ShowReplaceChoice);

        disk.Replace = true;
        await import.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["Disk", "NAS here"], main.Repositories.Select(item => item.Name));
        Assert.Equal("disk passphrase", _target.Vault.GetPassphrase(_local.Id));
        Assert.False(_target.Secrets.Secrets.ContainsKey(_nas.SecretKey)); // kept as it was here
        Assert.NotNull(_target.Vault.GetKey(_key.Id)); // the key was new here
    }

    [Fact]
    public async Task Replacing_removes_what_is_not_in_the_export()
    {
        var file = await ExportAsync(includeBorgKeys: false);
        _target.Vault.SetUp(Pin);
        var old = _local with { Id = Guid.NewGuid(), Name = "Old", PassphraseMode = PassphraseMode.Stored };
        var oldKey = SshKeyEntry.Generate("old key", "x", DateTimeOffset.Now);
        _target.Vault.SetKey(oldKey);
        _target.Secrets.Secrets[old.SecretKey] = "old passphrase";
        _target.Store.Save([old]);
        var (main, import) = await OpenImportAsync(file);

        import.IsReplace = true;
        Assert.All(import.Items, item => Assert.False(item.ShowReplaceChoice));
        await import.ImportCommand.ExecuteAsync(null);
        Assert.False(import.IsDone); // not confirmed

        _target.Dialogs.ConfirmAnswers.Enqueue(true);
        await import.ImportCommand.ExecuteAsync(null);

        Assert.True(import.IsDone);
        Assert.Equal(["Disk", "NAS"], main.Repositories.Select(item => item.Name));
        Assert.False(_target.Secrets.Secrets.ContainsKey(old.SecretKey));
        Assert.Equal([_key.Id], _target.Vault.Keys.Select(key => key.Id));
        Assert.Equal(30, main.Settings.VaultAutoLockMinutes);
        Assert.Equal(30, _target.Settings.Load().VaultAutoLockMinutes);
    }

    [Fact]
    public async Task Unreadable_files_are_reported()
    {
        var file = _target.Directory.Combine("not-an-export.borgstudio");
        File.WriteAllText(file, "hello");
        var main = await StartAsync(_target);
        _target.Dialogs.OpenFile = file;
        _target.Dialogs.Secrets.Enqueue(ExportPassword);

        Assert.Null(await ImportViewModel.OpenAsync(main, _target.Dialogs));
        Assert.Equal([Strings.ImportTitle], _target.Dialogs.Shown);
    }
}
