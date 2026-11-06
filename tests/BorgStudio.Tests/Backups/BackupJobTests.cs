using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Backups;

public sealed class BackupJobTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static readonly Func<string, string?> Wsl = path => BorgClient.ToWslPath(path);

    [Fact]
    public void Templates_extensions_folders_and_own_patterns_become_borg_excludes()
    {
        var exclusions = new BackupExclusions
        {
            CachesAndTemp = true,
            DevelopmentFolders = true,
            RecycleBin = false,
            CacheDirTag = true,
            Extensions = ["iso", "*.TMP", ".iso"],
            Folders = [@"C:\Users\me\Videos\", @"\\nas\share"],
            Patterns = ["sh:**/*.bak", "  "],
        };

        var arguments = BackupPatterns.Arguments(exclusions, Wsl);

        Assert.Equal("--exclude-caches", arguments[0]);
        var patterns = arguments.Where((_, index) => index > 0 && arguments[index - 1] == "--exclude").ToList();
        Assert.Contains("sh:**/AppData/Local/Temp", patterns);
        Assert.Contains("sh:**/node_modules", patterns);
        Assert.DoesNotContain("sh:**/$RECYCLE.BIN", patterns);
        Assert.Equal(1, patterns.Count(pattern => pattern == @"re:(?i)\.iso$"));
        Assert.Contains(@"re:(?i)\.tmp$", patterns);
        Assert.Contains("pp:/mnt/c/Users/me/Videos", patterns);
        Assert.DoesNotContain(patterns, pattern => pattern.Contains("nas", StringComparison.Ordinal)); // not reachable from WSL
        Assert.Equal("sh:**/*.bak", patterns[^1]);
        Assert.Equal(patterns.Count * 2 + 1, arguments.Count);
    }

    [Fact]
    public void Extension_lists_are_read_leniently()
    {
        Assert.Equal([".iso", ".tmp", ".log"], BackupPatterns.ParseExtensions("iso, .TMP; *.log  iso"));
        Assert.Empty(BackupPatterns.ParseExtensions(" , ; *"));
    }

    [Fact]
    public void The_scan_counts_what_would_be_backed_up()
    {
        var source = _directory.Combine("source");
        Write(Path.Combine(source, "letter.txt"), 100);
        Write(Path.Combine(source, "disc.ISO"), 1000);
        Write(Path.Combine(source, "project", "node_modules", "lib.js"), 1000);
        Write(Path.Combine(source, "project", "main.cs"), 50);
        Write(Path.Combine(source, "cache", "CACHEDIR.TAG"), 10);
        Write(Path.Combine(source, "cache", "blob"), 1000);
        Write(Path.Combine(source, "videos", "film.mp4"), 1000);
        var exclusions = new BackupExclusions
        {
            DevelopmentFolders = true,
            Extensions = [".iso"],
            Folders = [Path.Combine(source, "videos")],
        };

        var size = SourceScanner.Scan([source, _directory.Combine("missing")], exclusions);

        Assert.Equal(new SourceSize(150, 2), size);
    }

    [Fact]
    public void A_source_inside_an_excluded_area_is_recognized()
    {
        var temp = _directory.Combine("AppData", "Local", "Temp", "work");
        Directory.CreateDirectory(temp);

        Assert.True(SourceScanner.IsExcluded(temp, new BackupExclusions { CachesAndTemp = true }));
        Assert.False(SourceScanner.IsExcluded(temp, new BackupExclusions { CachesAndTemp = false }));
        Assert.True(SourceScanner.IsExcluded(temp, new BackupExclusions { CachesAndTemp = false, Folders = [_directory.Combine("AppData")] }));
        // (The test folder itself lies in the temp folder on Windows, so that template stays off here.)
        Assert.False(SourceScanner.IsExcluded(_directory.Combine("Documents"), new BackupExclusions { CachesAndTemp = false }));
    }

    [Fact]
    public void Jobs_are_saved_and_loaded_again()
    {
        var store = new BackupJobStore(_directory.Combine("data", "jobs.json"));
        Assert.Empty(store.Load());
        var job = new BackupJob
        {
            Id = Guid.NewGuid(),
            Name = "Documents",
            RepositoryId = Guid.NewGuid(),
            Sources = [@"C:\Users\me\Documents"],
            Exclusions = new BackupExclusions { Extensions = [".iso"], Folders = [@"C:\Users\me\Documents\Old"] },
            Compression = "lz4",
            LastRun = new BackupRunSummary
            {
                Started = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero),
                Duration = TimeSpan.FromMinutes(3),
                Outcome = BackupOutcome.Warnings,
                OriginalSize = 1000,
                Message = "file locked",
            },
        };

        store.Save([job]);
        var loaded = Assert.Single(store.Load());

        Assert.Equal(job.Name, loaded.Name);
        Assert.Equal(job.Sources, loaded.Sources);
        Assert.Equal(job.Exclusions.Extensions, loaded.Exclusions.Extensions);
        Assert.Equal(job.Exclusions.Folders, loaded.Exclusions.Folders);
        Assert.Equal(job.LastRun, loaded.LastRun);
        Assert.Contains("\"outcome\": \"warnings\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void A_broken_job_file_is_set_aside()
    {
        var store = new BackupJobStore(_directory.Combine("jobs.json"));
        File.WriteAllText(store.FilePath, "{ broken");

        Assert.Empty(store.Load());
        Assert.False(File.Exists(store.FilePath));
        Assert.Single(Directory.GetFiles(_directory.Path, "jobs.json.broken-*"));
    }

    private static void Write(string path, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }
}
