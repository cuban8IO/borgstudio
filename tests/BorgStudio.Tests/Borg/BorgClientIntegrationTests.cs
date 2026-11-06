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

    [BorgFact]
    public async Task Backs_up_with_progress_and_leaves_out_what_is_excluded()
    {
        await _client.InitAsync(Borg, Location(), BorgEncryption.None, null);
        var source = _directory.Combine("source");
        Directory.CreateDirectory(Path.Combine(source, "docs"));
        Directory.CreateDirectory(Path.Combine(source, "node_modules", "lib"));
        Directory.CreateDirectory(Path.Combine(source, "skip-me"));
        File.WriteAllText(Path.Combine(source, "docs", "letter.txt"), "keep");
        File.WriteAllText(Path.Combine(source, "docs", "disc.ISO"), "image");
        File.WriteAllText(Path.Combine(source, "node_modules", "lib", "index.js"), "dependency");
        File.WriteAllText(Path.Combine(source, "skip-me", "notes.txt"), "excluded folder");
        var exclusions = new Core.Backups.BackupExclusions
        {
            // The test folder is under AppData\Local\Temp, which this template leaves out.
            CachesAndTemp = false,
            DevelopmentFolders = true,
            Extensions = [".iso"],
            Folders = [Path.Combine(source, "skip-me")],
        };
        var options = new List<string> { "--compression", "zstd,3" };
        options.AddRange(Core.Backups.BackupPatterns.Arguments(exclusions, path => BorgClient.BorgPath(Borg, path)));
        var updates = new List<BorgCreateProgress>();

        var created = await _client.CreateAsync(Borg, Location(), "test-{now:%Y%m%d%H%M%S}", [source], options, null,
            progress: new SynchronousProgress<BorgCreateProgress>(updates.Add));

        Assert.True(created.Succeeded, created.Error?.ToString());
        Assert.StartsWith("test-", created.Value!.ArchiveName);
        Assert.Equal(1, created.Value.Files);
        Assert.Empty(created.Value.Warnings);
        Assert.NotEmpty(updates);

        var listed = await BorgOutputAsync("list", "--short", $"{BorgClient.BorgPath(Borg, Location().Url)}::{created.Value.ArchiveName}");
        var files = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains(files, file => file.EndsWith("docs/letter.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.Contains("node_modules", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.EndsWith(".ISO", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.Contains("skip-me", StringComparison.Ordinal));
    }

    [BorgFact]
    public async Task Lists_archives_and_their_details()
    {
        await _client.InitAsync(Borg, Location(), BorgEncryption.RepokeyBlake2, Passphrase);
        var source = _directory.Combine("source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), new string('a', 10_000));
        Assert.True((await _client.CreateAsync(Borg, Location(), "first", [source], [], Passphrase)).Succeeded);
        File.WriteAllText(Path.Combine(source, "b.txt"), new string('b', 20_000));
        Assert.True((await _client.CreateAsync(Borg, Location(), "second", [source], [], Passphrase)).Succeeded);

        var listed = await _client.ListArchivesAsync(Borg, Location(), Passphrase);
        Assert.True(listed.Succeeded, listed.Error?.ToString());
        Assert.Equal(["second", "first"], listed.Value!.Select(archive => archive.Name)); // newest first
        Assert.All(listed.Value!, archive => Assert.Null(archive.Stats));
        Assert.Equal(64, listed.Value![0].Id.Length);

        var details = await _client.ArchiveDetailsAsync(Borg, Location(), 1, Passphrase);
        Assert.True(details.Succeeded, details.Error?.ToString());
        var second = Assert.Single(details.Value!);
        Assert.Equal(listed.Value![0].Id, second.Id);
        Assert.Equal(2, second.Stats!.Files);
        Assert.Equal(30_000, second.Stats.OriginalSize);
        Assert.True(second.Stats.DeduplicatedSize < second.Stats.OriginalSize); // a.txt is already stored
        Assert.NotNull(second.Duration);
        Assert.Equal(listed.Value![0].Start, second.Start);

        var wrong = await _client.ListArchivesAsync(Borg, Location(), "wrong");
        Assert.Equal(BorgErrorKind.PassphraseWrong, wrong.Error!.Kind);
    }
    [BorgFact]
    public async Task Cancelling_interrupts_borg_and_leaves_a_checkpoint()
    {
        if (OperatingSystem.IsWindows() && Borg.Runtime != BorgRuntime.Wsl)
            return; // no signals for a native Windows borg

        await _client.InitAsync(Borg, Location(), BorgEncryption.None, null);
        var source = _directory.Combine("big");
        Directory.CreateDirectory(source);
        var random = new Random(1);
        var block = new byte[1024 * 1024];
        for (var file = 0; file < 300; file++)
        {
            random.NextBytes(block);
            File.WriteAllBytes(Path.Combine(source, $"file{file:000}.bin"), block);
        }

        using var cancel = new CancellationTokenSource();
        var progress = new SynchronousProgress<BorgCreateProgress>(update =>
        {
            if (update.Files > 3)
                cancel.Cancel();
        });
        // Short checkpoints, so the interrupted run has something to keep.
        var created = await _client.CreateAsync(Borg, Location(), "big", [source], ["--compression", "none", "--checkpoint-interval", "1"],
            null, progress: progress, cancellationToken: cancel.Token);

        Assert.Equal(BorgErrorKind.Cancelled, created.Error?.Kind);
        var archives = await BorgOutputAsync("list", "--short", "--consider-checkpoints", BorgClient.BorgPath(Borg, Location().Url)!);
        Assert.Contains("big.checkpoint", archives);
    }

    /// <summary>Calls the progress callback right away (Progress&lt;T&gt; would post it to a thread pool).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Output of a plain borg command (unencrypted repositories only).</summary>
    private static async Task<string> BorgOutputAsync(params string[] arguments)
    {
        var runner = new Core.Processes.ProcessRunner();
        var environment = new Dictionary<string, string?> { ["BORG_UNKNOWN_UNENCRYPTED_REPO_ACCESS_IS_OK"] = "yes" };
        var result = Borg.Runtime == BorgRuntime.Wsl
            ? await runner.RunAsync(Path.Combine(Environment.SystemDirectory, "wsl.exe"),
                ["-e", "env", "BORG_UNKNOWN_UNENCRYPTED_REPO_ACCESS_IS_OK=yes", Borg.Path, .. arguments], TimeSpan.FromMinutes(2))
            : await runner.RunAsync(Borg.Path, arguments, TimeSpan.FromMinutes(2), environment);
        Assert.True(result?.ExitCode == 0, result?.StandardError);
        return result!.StandardOutput;
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
