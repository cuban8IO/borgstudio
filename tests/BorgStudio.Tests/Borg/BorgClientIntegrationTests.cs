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

    /// <summary>
    /// Like moving to another computer: borg's own keyfile is gone, the repository cannot be opened; the exported key,
    /// put back by <see cref="BorgClient.InstallKeyfileAsync"/>, opens it again.
    /// </summary>
    [BorgFact]
    public async Task An_exported_keyfile_key_opens_the_repository_again()
    {
        if (OperatingSystem.IsWindows() && Borg.Runtime != BorgRuntime.Wsl)
            return; // keyfile keys can be put back by BorgStudio only where a POSIX shell runs borg

        var created = await _client.InitAsync(Borg, Location(), BorgEncryption.KeyfileBlake2, Passphrase);
        Assert.True(created.Succeeded, created.Error?.ToString());
        var repositoryId = (await _client.InfoAsync(Borg, Location(), Passphrase)).Value!.Id;
        var keyFile = _directory.Combine("exported.key");
        Assert.True((await _client.ExportKeyAsync(Borg, Location(), keyFile)).Succeeded);

        // Move every keyfile of this repository out of borg's key folder.
        var keysDir = "${BORG_KEYS_DIR:-${BORG_CONFIG_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}/borg}/keys}";
        var park = $"/tmp/borgstudio-test-{Guid.NewGuid():N}";
        await ShellAsync($"mkdir -p {park} && grep -l 'BORG_KEY {repositoryId}' \"{keysDir}\"/* | xargs -r -I{{}} mv {{}} {park}/");
        try
        {
            Assert.False((await _client.InfoAsync(Borg, Location(), Passphrase)).Succeeded);

            var installed = await _client.InstallKeyfileAsync(Borg, repositoryId, File.ReadAllText(keyFile));
            Assert.True(installed.Succeeded, installed.Error?.ToString());

            var info = await _client.InfoAsync(Borg, Location(), Passphrase);
            Assert.True(info.Succeeded, info.Error?.ToString());
            Assert.Equal("keyfile-blake2", info.Value!.EncryptionMode);
        }
        finally
        {
            await ShellAsync($"grep -l 'BORG_KEY {repositoryId}' \"{keysDir}\"/* | xargs -r rm -f; rm -rf {park}");
        }
    }

    /// <summary>Runs a command in the shell borg runs in (inside WSL if borg is there).</summary>
    private static async Task ShellAsync(string script)
    {
        var runner = new Core.Processes.ProcessRunner();
        var result = Borg.Runtime == BorgRuntime.Wsl
            ? await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "wsl.exe"), ["-e", "sh", "-c", script], TimeSpan.FromSeconds(30))
            : await runner.RunAsync("sh", ["-c", script], TimeSpan.FromSeconds(30));
        Assert.Equal(0, result?.ExitCode);
    }
}
