using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using BorgStudio.Core.Updates;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Updates;

/// <summary>Finding a release, downloading and checking its package, and replacing the app's files.</summary>
public sealed class UpdaterTests : IDisposable
{
    private const string Exe = FakeUpdateSource.Executable;

    private readonly TemporaryDirectory _directory = new();
    private readonly FakeUpdateSource _source = new();

    public void Dispose() => _directory.Dispose();

    private string App => _directory.Combine("app");

    private string Work => _directory.Combine("updates");

    private Updater NewUpdater(string version = "0.2.0", string? rid = "win-x64")
    {
        Directory.CreateDirectory(App);
        if (!File.Exists(Path.Combine(App, Exe)))
            File.WriteAllText(Path.Combine(App, Exe), "old");
        return new Updater(_source, version, new InstallLocation(App, Exe), Work) { Rid = rid };
    }

    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("0.0.0-dev", null)]
    [InlineData("nightly", null)]
    [InlineData("v1.2", null)]
    public void Versions_are_three_numbers(string text, string? expected) =>
        Assert.Equal(expected, UpdateVersion.Parse(text)?.ToString());

    [Fact]
    public void GitHubs_release_json_is_read()
    {
        var release = GitHubUpdateSource.ParseRelease("""
            {
              "tag_name": "v0.3.0",
              "html_url": "https://github.com/cuban8IO/borgstudio/releases/tag/v0.3.0",
              "body": "Notes",
              "assets": [
                { "name": "BorgStudio-0.3.0-win-x64.zip", "size": 1234,
                  "browser_download_url": "https://github.com/cuban8IO/borgstudio/releases/download/v0.3.0/BorgStudio-0.3.0-win-x64.zip" },
                { "name": "SHA256SUMS", "size": 99, "browser_download_url": "https://example.test/SHA256SUMS" }
              ]
            }
            """)!;

        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.Equal("Notes", release.Notes);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal(1234, release.Assets[0].Size);
        Assert.Null(GitHubUpdateSource.ParseRelease("""{ "tag_name": "nightly" }"""));
    }

    [Fact]
    public void Checksums_are_found_by_file_name()
    {
        var hash = new string('a', 64);
        var listing = $"{new string('b', 64)}  BorgStudio-0.3.0-linux-x64.tar.gz\n{hash} *BorgStudio-0.3.0-win-x64.zip\n";
        Assert.Equal(hash, Updater.FindChecksum(listing, "BorgStudio-0.3.0-win-x64.zip"));
        Assert.Null(Updater.FindChecksum(listing, "BorgStudio-0.3.0-osx-x64.zip"));
    }

    [Fact]
    public async Task A_newer_release_with_a_package_can_be_installed()
    {
        _source.Publish("0.3.0");
        var check = await NewUpdater().CheckAsync();

        Assert.True(check.IsNewer);
        Assert.True(check.CanInstall);
        Assert.Equal("BorgStudio-0.3.0-win-x64.zip", check.Package!.Name);
    }

    [Fact]
    public async Task The_same_or_an_older_release_is_no_update()
    {
        _source.Publish("0.2.0");
        Assert.False((await NewUpdater().CheckAsync()).IsNewer);
        _source.Publish("0.1.9");
        Assert.False((await NewUpdater().CheckAsync()).CanInstall);
        _source.Latest = null;
        Assert.Null((await NewUpdater().CheckAsync()).Latest);
    }

    [Fact]
    public async Task What_keeps_the_app_from_installing_is_named()
    {
        _source.Publish("0.3.0");
        Assert.Equal(UpdateBlocker.DevelopmentBuild, (await NewUpdater("0.0.0-dev").CheckAsync()).Blocker);
        Assert.Equal(UpdateBlocker.UnsupportedPlatform, (await NewUpdater(rid: null).CheckAsync()).Blocker);
        Assert.Equal(UpdateBlocker.NoPackage, (await NewUpdater(rid: "linux-x64").CheckAsync()).Blocker);

        _source.Publish("0.3.0", checksums: false);
        Assert.Equal(UpdateBlocker.NoPackage, (await NewUpdater().CheckAsync()).Blocker);

        _source.Publish("0.3.0");
        var updater = NewUpdater();
        File.SetAttributes(Path.Combine(App, Exe), FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(UpdateBlocker.NotWritable, (await updater.CheckAsync()).Blocker);
        }
        finally
        {
            File.SetAttributes(Path.Combine(App, Exe), FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task The_download_is_checked_and_unpacked()
    {
        _source.Publish("0.3.0");
        var updater = NewUpdater();
        var reported = new List<long>();

        var staged = await updater.DownloadAsync(await updater.CheckAsync(), new SyncProgress(reported.Add));

        Assert.Equal(new Version(0, 3, 0), staged.Version);
        Assert.Equal("new", File.ReadAllText(Path.Combine(staged.AppRoot, Exe)));
        Assert.True(File.Exists(Path.Combine(staged.AppRoot, "lib", "core.dll")));
        Assert.StartsWith(Work, staged.AppRoot);
        Assert.Equal(_source.Latest!.Assets[0].Size, reported[^1]);
        // The archive itself is not kept.
        Assert.Empty(Directory.GetFiles(Path.Combine(Work, "0.3.0")));
    }

    [Fact]
    public async Task A_damaged_download_is_refused()
    {
        _source.Publish("0.3.0", corrupt: true);
        var updater = NewUpdater();
        var error = await Assert.ThrowsAsync<UpdateException>(async () => await updater.DownloadAsync(await updater.CheckAsync(), null));
        Assert.Equal(UpdateError.ChecksumMismatch, error.Error);
    }

    [Fact]
    public async Task A_package_missing_from_the_checksums_is_refused()
    {
        var release = _source.Publish("0.3.0");
        _source.Files[release.Assets[1].Url] = "0000  something-else.zip\n"u8.ToArray();
        var updater = NewUpdater();
        var error = await Assert.ThrowsAsync<UpdateException>(async () => await updater.DownloadAsync(await updater.CheckAsync(), null));
        Assert.Equal(UpdateError.ChecksumMissing, error.Error);
    }

    [Fact]
    public void Tarballs_and_bundles_are_found_below_their_top_folder()
    {
        // Linux: BorgStudio/BorgStudio inside a tar.gz.
        var tarball = _directory.Combine("package.tar.gz");
        var content = _directory.Combine("content");
        Directory.CreateDirectory(Path.Combine(content, "BorgStudio"));
        File.WriteAllText(Path.Combine(content, "BorgStudio", "BorgStudio"), "linux");
        using (var file = File.Create(tarball))
        using (var zipped = new GZipStream(file, CompressionLevel.Fastest))
            TarFile.CreateFromDirectory(content, zipped, includeBaseDirectory: false);
        Updater.Extract(tarball, _directory.Combine("linux"));
        Assert.Equal(_directory.Combine("linux", "BorgStudio"), Updater.FindAppRoot(_directory.Combine("linux"), "BorgStudio"));

        // macOS: BorgStudio.app/Contents/MacOS/BorgStudio at the top of the zip.
        var macOs = _directory.Combine("mac", "BorgStudio.app", "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        File.WriteAllText(Path.Combine(macOs, "BorgStudio"), "mac");
        var relative = Path.Combine("Contents", "MacOS", "BorgStudio");
        Assert.Equal(_directory.Combine("mac", "BorgStudio.app"), Updater.FindAppRoot(_directory.Combine("mac"), relative));
        Assert.Null(Updater.FindAppRoot(_directory.Combine("mac"), "BorgStudio.exe"));
    }

    [Fact]
    public void The_bundle_is_the_install_folder_on_macOS()
    {
        var macOs = Path.Combine(_directory.Path, "Applications", "BorgStudio.app", "Contents", "MacOS");
        Assert.Equal(Path.Combine(_directory.Path, "Applications", "BorgStudio.app"), InstallLocation.BundleOf(macOs));
        Assert.Null(InstallLocation.BundleOf(_directory.Combine("app")));
    }

    [Fact]
    public async Task The_installer_is_the_new_version_with_where_to_install()
    {
        _source.Publish("0.3.0");
        var updater = NewUpdater();
        var staged = await updater.DownloadAsync(await updater.CheckAsync(), null);

        var start = updater.ApplyCommand(staged, 4711);

        Assert.Equal(Path.Combine(staged.AppRoot, Exe), start.FileName);
        Assert.Equal(staged.AppRoot, start.WorkingDirectory);
        Assert.True(UpdateApplier.IsApplyCall([.. start.ArgumentList]));
        var arguments = start.ArgumentList.ToList();
        Assert.Equal(App, arguments[arguments.IndexOf("--target") + 1]);
        Assert.Equal("4711", arguments[arguments.IndexOf("--pid") + 1]);
        Assert.Equal("0.3.0", arguments[arguments.IndexOf("--version") + 1]);
    }

    [Fact]
    public void Replacing_writes_the_package_and_leaves_everything_else()
    {
        var (source, target) = Folders();
        File.WriteAllText(Path.Combine(target, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(target, "old-only.dll"), "old");

        UpdateApplier.Replace(source, target, Exe, _directory.Combine("backup"));

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, Exe)));
        Assert.Equal("new lib", File.ReadAllText(Path.Combine(target, "lib", "core.dll")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(target, "old-only.dll")));
        Assert.False(Directory.Exists(_directory.Combine("backup")));
    }

    [Fact]
    public void A_failed_replace_puts_the_old_files_back()
    {
        var (source, target) = Folders();
        // "zz" sorts last: a folder where the package has a file makes the copy fail after the others.
        File.WriteAllText(Path.Combine(source, "zz"), "file");
        Directory.CreateDirectory(Path.Combine(target, "zz"));

        Assert.ThrowsAny<Exception>(() => UpdateApplier.Replace(source, target, Exe, _directory.Combine("backup"), attempts: 1));

        Assert.Equal("old", File.ReadAllText(Path.Combine(target, Exe)));
        Assert.False(File.Exists(Path.Combine(target, "lib", "core.dll")));
    }

    [Fact]
    public void Only_a_folder_with_the_app_in_it_is_written()
    {
        var (source, _) = Folders();
        var elsewhere = _directory.Combine("documents");
        Directory.CreateDirectory(elsewhere);
        Assert.Throws<InvalidOperationException>(() => UpdateApplier.Replace(source, elsewhere, Exe, _directory.Combine("backup")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
    }

    [Fact]
    public void The_installer_notes_the_outcome_for_the_next_start()
    {
        var (source, target) = Folders();
        var ended = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "true")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = OperatingSystem.IsWindows() ? "/c exit" : "",
        })!;
        ended.WaitForExit();

        var started = new List<ProcessStartInfo>();
        var code = UpdateApplier.Run([.. UpdateApplier.Arguments(source, new InstallLocation(target, Exe), ended.Id, Work, "0.3.0")], started.Add);

        Assert.Equal(0, code);
        Assert.Equal(Path.Combine(target, Exe), started.Single().FileName);
        Assert.Equal(target, started.Single().WorkingDirectory);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, Exe)));
        var outcome = JsonSerializer.Deserialize<UpdateOutcome>(File.ReadAllText(Path.Combine(Work, UpdateApplier.OutcomeFile)))!;
        Assert.True(outcome.Succeeded);
        Assert.Equal("0.3.0", outcome.Version);

        Directory.CreateDirectory(Path.Combine(Work, "0.3.0"));
        var updater = NewUpdater();
        Assert.Equal(outcome, updater.TakeOutcome());
        Assert.Null(updater.TakeOutcome());
        Assert.False(Directory.Exists(Path.Combine(Work, "0.3.0")));
    }

    private (string Source, string Target) Folders()
    {
        var source = _directory.Combine("staged");
        var target = _directory.Combine("installed");
        Directory.CreateDirectory(Path.Combine(source, "lib"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, Exe), "new");
        File.WriteAllText(Path.Combine(source, "lib", "core.dll"), "new lib");
        File.WriteAllText(Path.Combine(target, Exe), "old");
        return (source, target);
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
