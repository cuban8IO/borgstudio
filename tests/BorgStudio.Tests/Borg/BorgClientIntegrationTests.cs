using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Borg;

/// <summary>Against a real borg (installed in the Linux CI job); skipped where none is installed.</summary>
public sealed class BorgClientIntegrationTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly TemporaryDirectory _directory = new();
    private readonly BorgClient _client = BorgClient.CreateDefault();

    public void Dispose() => _directory.Dispose();

    private static BorgInstallation Borg => BorgFactAttribute.Borg.Value!;

    private RepositoryLocation Location(string name = "repo") => new(_directory.Combine(name));

    [BorgFact]
    public async Task Creates_an_encrypted_repository_and_reads_its_info()
    {
        var created = await _client.InitAsync(Borg, Location(), BorgEncryption.RepokeyBlake2, Passphrase);
        Assert.True(created.Succeeded, created.Error?.ToString());

        var info = await _client.InfoAsync(Borg, Location(), Passphrase);

        Assert.True(info.Succeeded, info.Error?.ToString());
        Assert.Equal("repokey-blake2", info.Value!.EncryptionMode);
        Assert.NotEmpty(info.Value.Id);
        Assert.Equal(1, RepositoryFormat.DetectLocal(Location().Url));
    }

    [BorgFact]
    public async Task Wrong_passphrase()
    {
        await _client.InitAsync(Borg, Location(), BorgEncryption.RepokeyBlake2, Passphrase);

        var info = await _client.InfoAsync(Borg, Location(), "wrong");

        Assert.Equal(BorgErrorKind.PassphraseWrong, info.Error?.Kind);
    }

    [BorgFact]
    public async Task Missing_repository_and_folder_without_repository()
    {
        var missing = await _client.InfoAsync(Borg, Location("missing"), Passphrase);
        Assert.Equal(BorgErrorKind.RepositoryNotFound, missing.Error?.Kind);

        Directory.CreateDirectory(_directory.Combine("empty"));
        var empty = await _client.InfoAsync(Borg, Location("empty"), Passphrase);
        Assert.Contains(empty.Error?.Kind, new BorgErrorKind?[] { BorgErrorKind.NotARepository, BorgErrorKind.RepositoryNotFound });
    }

    [BorgFact]
    public async Task Creating_twice_at_the_same_place_fails()
    {
        await _client.InitAsync(Borg, Location(), BorgEncryption.None, null);

        var again = await _client.InitAsync(Borg, Location(), BorgEncryption.None, null);

        Assert.Equal(BorgErrorKind.RepositoryExists, again.Error?.Kind);
    }

    [BorgFact]
    public async Task Unencrypted_repository_needs_no_passphrase()
    {
        await _client.InitAsync(Borg, Location(), BorgEncryption.None, null);

        var info = await _client.InfoAsync(Borg, Location(), null);

        Assert.True(info.Succeeded, info.Error?.ToString());
        Assert.Equal("none", info.Value!.EncryptionMode);
    }

    [BorgFact]
    public async Task Exports_the_repository_key()
    {
        await _client.InitAsync(Borg, Location(), BorgEncryption.RepokeyBlake2, Passphrase);
        var keyFile = _directory.Combine("exported.key");

        var exported = await _client.ExportKeyAsync(Borg, Location(), keyFile);

        Assert.True(exported.Succeeded, exported.Error?.ToString());
        Assert.StartsWith("BORG_KEY", File.ReadAllText(keyFile));
    }
}
