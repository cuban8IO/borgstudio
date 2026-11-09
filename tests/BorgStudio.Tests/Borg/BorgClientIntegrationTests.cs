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
        Assert.Equal(listed.Value![0].Start.ToUnixTimeSeconds(), second.Start.ToUnixTimeSeconds()); // borg 1.2 lists whole seconds

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

    [BorgFact]
    public async Task Prunes_the_archives_of_one_job_and_frees_the_space()
    {
        await _client.InitAsync(Borg, Location(), BorgEncryption.RepokeyBlake2, Passphrase);
        var source = _directory.Combine("source");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "Docs-{hostname}-1", "Docs-{hostname}-2", "Docs-Old-{hostname}-1", "Docs-{hostname}-3" })
        {
            // Different content each time, so deleting an archive leaves something to free.
            var random = new Random(name.GetHashCode(StringComparison.Ordinal));
            var data = new byte[200_000];
            random.NextBytes(data);
            File.WriteAllBytes(Path.Combine(source, "data.bin"), data);
            Assert.True((await _client.CreateAsync(Borg, Location(), name, [source], ["--compression", "none"], Passphrase)).Succeeded);
        }
        var keepLast = new Core.Backups.RetentionPolicy { Last = 1 }.Arguments();

        var preview = await _client.PruneAsync(Borg, Location(), "Docs-{hostname}-*", keepLast, dryRun: true, Passphrase);

        Assert.True(preview.Succeeded, preview.Error?.ToString());
        Assert.Equal(3, preview.Value!.Archives.Count); // "Docs-Old-…" is another job
        var kept = preview.Value.Archives[0];
        Assert.True(kept.Kept);
        Assert.EndsWith("-3", kept.Name, StringComparison.Ordinal);
        Assert.Equal(("secondly", 1), (kept.Rule, kept.RuleNumber)); // borg's name for --keep-last
        Assert.Equal(64, kept.Id.Length);
        Assert.Equal(2, preview.Value.Pruned.Count);
        Assert.Equal(4, (await _client.ListArchivesAsync(Borg, Location(), Passphrase)).Value!.Count); // nothing deleted yet

        var pruned = await _client.PruneAsync(Borg, Location(), "Docs-{hostname}-*", keepLast, dryRun: false, Passphrase);

        Assert.True(pruned.Succeeded, pruned.Error?.ToString());
        Assert.Equal(2, pruned.Value!.Pruned.Count);
        var left = (await _client.ListArchivesAsync(Borg, Location(), Passphrase)).Value!.Select(archive => archive.Name).ToList();
        Assert.Equal(2, left.Count);
        Assert.Contains(left, name => name.StartsWith("Docs-Old-", StringComparison.Ordinal));
        Assert.Contains(left, name => name.EndsWith("-3", StringComparison.Ordinal));

        var progress = new List<double>();
        var compacted = await _client.CompactAsync(Borg, Location(), progress: new SynchronousProgress<double>(progress.Add));

        Assert.True(compacted.Succeeded, compacted.Error?.ToString());
        Assert.True(compacted.Value!.FreedBytes > 100_000, $"freed {compacted.Value.FreedBytes}");
        Assert.NotEmpty(progress);
    }

    [BorgFact]
    public async Task Reads_the_contents_of_an_archive_as_a_tree()
    {
        await _client.InitAsync(Borg, Location(), BorgEncryption.RepokeyBlake2, Passphrase);
        var source = _directory.Combine("source");
        Directory.CreateDirectory(Path.Combine(source, "sub folder"));
        File.WriteAllText(Path.Combine(source, "a.txt"), new string('a', 1000));
        File.WriteAllText(Path.Combine(source, "sub folder", "b c.txt"), new string('b', 2500));
        Assert.True((await _client.CreateAsync(Borg, Location(), "contents", [source], [], Passphrase)).Succeeded);
        var read = new List<long>();

        var listed = await _client.ListContentsAsync(Borg, Location(), "contents", Passphrase,
            entriesRead: new SynchronousProgress<long>(read.Add));

        Assert.True(listed.Succeeded, listed.Error?.ToString());
        var tree = listed.Value!;
        var borgPath = BorgClient.BorgPath(Borg, source)!.TrimStart('/');
        var folder = tree.Find(borgPath);
        Assert.NotNull(folder);
        Assert.Equal(["sub folder", "a.txt"], folder!.Children.Select(node => node.Name));
        Assert.Equal(3500, folder.Size);
        Assert.Equal(2, folder.FileCount);
        Assert.Equal(borgPath + "/sub folder/b c.txt", tree.Find(borgPath + "/sub folder/b c.txt")!.BorgPath);
        if (Borg.Runtime == BorgRuntime.Wsl)
            Assert.Equal(Path.GetFullPath(source), folder.DisplayPath, ignoreCase: true); // "mnt/c/…" shown as "C:\…"
        Assert.True(folder.Modified > DateTimeOffset.Now.AddHours(-1));
        Assert.Equal(tree.EntryCount, read[^1]);

        var wrong = await _client.ListContentsAsync(Borg, Location(), "contents", "wrong");
        Assert.Equal(BorgErrorKind.PassphraseWrong, wrong.Error!.Kind);
    }

    [BorgFact]
    public async Task Restores_into_a_folder_and_back_to_the_original_location()
    {
        if (OperatingSystem.IsWindows() && Borg.Runtime != BorgRuntime.Wsl)
            return; // no way back for a native Windows borg

        await _client.InitAsync(Borg, Location(), BorgEncryption.RepokeyBlake2, Passphrase);
        var source = _directory.Combine("source");
        Directory.CreateDirectory(Path.Combine(source, "Invoices [2026]"));
        File.WriteAllText(Path.Combine(source, "letter.txt"), "letter");
        File.WriteAllBytes(Path.Combine(source, "Invoices [2026]", "a.pdf"), new byte[300_000]);
        Assert.True((await _client.CreateAsync(Borg, Location(), "restore", [source], [], Passphrase)).Succeeded);
        var tree = (await _client.ListContentsAsync(Borg, Location(), "restore", Passphrase)).Value!;
        var folder = tree.Find(BorgClient.BorgPath(Borg, source)!.TrimStart('/'))!;
        var invoices = folder.Children.Single(child => child.Name == "Invoices [2026]");
        var letter = folder.Children.Single(child => child.Name == "letter.txt");

        async Task<List<BorgExtractProgress>> RunAsync(RestorePlan plan)
        {
            var updates = new List<BorgExtractProgress>();
            foreach (var run in plan.Runs)
            {
                var result = await _client.ExtractAsync(Borg, Location(), "restore", run.Paths, run.TargetDirectory, run.StripComponents,
                    Passphrase, progress: new SynchronousProgress<BorgExtractProgress>(updates.Add));
                Assert.True(result.Succeeded, result.Error?.ToString());
            }
            return updates;
        }

        // Directly into a folder (brackets in the name are taken literally).
        var direct = _directory.Combine("direct");
        Directory.CreateDirectory(direct);
        var updates = await RunAsync(RestorePlan.IntoFolder([invoices, letter], direct, keepFullPath: false));
        Assert.Equal(300_000, new FileInfo(Path.Combine(direct, "Invoices [2026]", "a.pdf")).Length);
        Assert.Equal("letter", File.ReadAllText(Path.Combine(direct, "letter.txt")));
        Assert.Contains(updates, update => update is { Preparing: false, Total: > 0 });

        // Existing files are overwritten (after the app asked).
        File.WriteAllText(Path.Combine(direct, "letter.txt"), "changed");
        var plan = RestorePlan.IntoFolder([letter], direct, keepFullPath: false);
        Assert.Single(plan.FindConflicts());
        await RunAsync(plan);
        Assert.Equal("letter", File.ReadAllText(Path.Combine(direct, "letter.txt")));

        // With the full path as in the archive.
        var full = _directory.Combine("full");
        Directory.CreateDirectory(full);
        await RunAsync(RestorePlan.IntoFolder([letter], full, keepFullPath: true));
        Assert.True(File.Exists(Path.Combine([full, .. letter.BorgPath.Split('/')])));

        // Back where it came from (in WSL: "C:\" with "mnt/c" dropped).
        File.WriteAllText(Path.Combine(source, "letter.txt"), "changed at the source");
        await RunAsync(RestorePlan.ToOriginal([letter], Borg.Runtime)!);
        Assert.Equal("letter", File.ReadAllText(Path.Combine(source, "letter.txt")));
    }

    [BorgFact]
    public async Task Mounts_an_archive_and_the_whole_repository_and_unmounts_them()
    {
        await _client.InitAsync(Borg, Location(), BorgEncryption.RepokeyBlake2, Passphrase);
        var source = _directory.Combine("source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "letter.txt"), "letter");
        Assert.True((await _client.CreateAsync(Borg, Location(), "mounted", [source], [], Passphrase)).Succeeded);
        var name = "test-" + Guid.NewGuid().ToString("N");

        var setup = await _client.CheckMountSetupAsync(Borg);
        var archive = await _client.MountAsync(Borg, Location(), "mounted", BorgClient.MountFolder(name, "mounted", "all"), Passphrase);
        if (setup == MountSetup.AllowOtherMissing)
        {
            // Not set up (the app offers to do it): FUSE's refusal is recognised.
            Assert.Equal(BorgErrorKind.MountNotAllowed, archive.Error?.Kind);
            return;
        }
        if (setup == MountSetup.Unsupported || archive.Error?.Kind == BorgErrorKind.MountUnavailable)
            return; // no FUSE here (e.g. a CI runner)

        Assert.True(archive.Succeeded, archive.Error?.ToString());
        var mount = archive.Value!;
        BorgMount? repository = null;
        try
        {
            Assert.EndsWith($"/{BorgClient.MountRoot}/{name}/mounted", mount.Path);
            var letter = BorgClient.BorgPath(Borg, Path.Combine(source, "letter.txt"))!.TrimStart('/');
            await ShellAsync($"grep -q letter {BorgClient.ShellQuote(mount.Path + "/" + letter)}");
            if (Borg.Runtime == BorgRuntime.Wsl)
            {
                // Explorer's view of it (\\wsl.localhost\<distribution>\home\…): readable from Windows.
                Assert.StartsWith(@"\\wsl", mount.WindowsPath);
                Assert.Equal("letter", File.ReadAllText(Path.Combine([mount.WindowsPath!, .. letter.Split('/')])));
            }

            // The whole repository at the same time: one folder per archive.
            var mounted = await _client.MountAsync(Borg, Location(), null, BorgClient.MountFolder(name, null, "all"), Passphrase);
            Assert.True(mounted.Succeeded, mounted.Error?.ToString());
            repository = mounted.Value!;
            await ShellAsync($"test -d {BorgClient.ShellQuote(repository.Path + "/mounted")}");

            // Reading works meanwhile.
            Assert.True((await _client.ListArchivesAsync(Borg, Location(), Passphrase)).Succeeded);

            Assert.True(await mount.UnmountAsync());
            Assert.Null(await mount.Ended);
            Assert.True(await repository.UnmountAsync());
            Assert.Null(await repository.Ended);
            await ShellAsync($"! test -e {BorgClient.ShellQuote(mount.Path)} && ! test -e \"$(dirname {BorgClient.ShellQuote(mount.Path)})\"");
        }
        finally
        {
            // A failed check must not leave borg running (the test run would wait for it).
            await mount.UnmountAsync(force: true);
            if (repository is not null)
                await repository.UnmountAsync(force: true);
        }

        // A wrong passphrase stops borg before it is mounted.
        var wrong = await _client.MountAsync(Borg, Location(), "mounted", BorgClient.MountFolder(name, "mounted", "all"), "wrong");
        Assert.Equal(BorgErrorKind.PassphraseWrong, wrong.Error?.Kind);
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
