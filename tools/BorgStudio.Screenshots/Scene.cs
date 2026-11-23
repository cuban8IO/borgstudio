using System.Globalization;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Storage;
using BorgStudio.Core.Updates;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Screenshots;

/// <summary>
/// Made-up example data for one picture: three repositories (a local disk, a NAS over SSH, a Hetzner Storage Box), their
/// backup jobs with twelve weeks of history, storage measurements, integrity checks and a restore test, and the archives
/// of the first one, served by fake borg. Nothing touches real data; everything lives in a temporary folder.
/// </summary>
public sealed class Scene : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    public TestServices Test { get; } = new(wsl: true);

    /// <summary>Where the picture goes.</summary>
    public required string Output { get; init; }

    /// <summary>Takes the picture of the window now.</summary>
    public void Save(Avalonia.Controls.Window window) => Program.Save(window, Output);

    public RepositoryConfig Laptop { get; }

    public RepositoryConfig Nas { get; }

    public RepositoryConfig StorageBox { get; }

    public BackupJob Documents { get; }

    public BackupJob Photos { get; }

    public BackupJob NasJob { get; }

    public BackupJob OffsiteJob { get; }

    /// <summary>"Now": the history ends at the present, so the app judges it as it would today.</summary>
    public static DateTime Now { get; } = DateTime.Now;

    /// <summary>The app's services, as this release (the fake updater otherwise says 0.2.0).</summary>
    public AppServices Services { get; }

    public Scene()
    {
        Services = Test.Services with
        {
            Updates = new Updater(Test.UpdateSource, "0.5.0", new InstallLocation(Test.AppFolder, FakeUpdateSource.Executable),
                Test.Directory.Combine("updates")) { Rid = "win-x64" },
        };
        Test.Settings.Save(new AppSettings
        {
            Language = AppLanguages.Current.TwoLetterISOLanguageName,
            WarrantyNoticeShown = true,
            TrayHintShown = true,
            RestoreTest = new RestoreTestSettings { Enabled = true },
            Notifications = new NotificationSettings
            {
                System = new SystemNotificationChannel { Enabled = true },
                Email = new EmailChannel
                {
                    Enabled = true, Host = "smtp.example.com", UserName = "backup@example.com", From = "BorgStudio <backup@example.com>",
                    To = "me@example.com", DailySummary = true,
                },
            },
        });

        // A made-up place: the pictures must not show the folder this runs in.
        Laptop = Repository("6b1f0c52-7d1e-4c55-9a43-2f1d3c4b5a61", Texts.Pick("Laptop backup", "Laptop-Backup"), "local",
            new() { ["path"] = @"D:\Backups\Laptop" }) with
        {
            Check = new CheckPolicy { AfterBackup = true, IntervalDays = 7 },
        };
        Nas = Repository("0c7d2e94-3b8a-4f21-8e5d-61a0b9c4d372", Texts.Pick("Home NAS", "NAS zu Hause"), "ssh",
            new() { ["host"] = "nas.local", ["port"] = "22", ["user"] = "backup", ["path"] = "/volume1/borg/laptop", ["remoteBorg"] = "borg" });
        StorageBox = Repository("a3e5f1b7-9c2d-4e86-b14f-7d9e0a2c5b18", "Storage Box", "hetzner-storage-box",
            new() { ["user"] = "u123456", ["path"] = "./borg/laptop", ["remoteBorg"] = "borg-1.4" });
        Test.Store.Save([Laptop, Nas, StorageBox]);

        Documents = Job("3f6a9d21-5b7c-4e08-a1d3-9c2e4f6b8a05", Texts.Pick("Documents", "Dokumente"), Laptop, @"C:\Users\Public\Documents", 1)
            with
            {
                Retention = new RetentionPolicy { Daily = 7, Weekly = 4, Monthly = 12 },
                Conditions = new RunConditions { NotOnBattery = true },
                RestoreTest = RestoreTestMode.Compare,
            };
        Photos = Job("8d2b4f60-1a9e-4c37-b5d8-0e6f2a4c9b13", Texts.Pick("Photos", "Fotos"), Laptop, @"C:\Users\Public\Pictures", 2);
        NasJob = Job("5c9e1a37-2d4b-4f86-9e0a-3b7d5f1c2a64", "Laptop", Nas, @"C:\Users\Public", 3);
        OffsiteJob = Job("e2a4c6b8-0d1f-4357-9b2c-6e8a0f4d1c73", "Laptop", StorageBox, @"C:\Users\Public", 4);

        // Twelve weeks of runs: mostly fine, now and then warnings or a failure; the Storage Box missed its last two.
        var random = new Random(7);
        var history = new BackupRunHistory(Path.Combine(Test.Jobs.DataDirectory, "history"));
        var jobs = new List<BackupJob>();
        foreach (var (job, gigabytes, missed) in new[] { (Documents, 1.2, 0), (Photos, 0.6, 0), (NasJob, 1.6, 0), (OffsiteJob, 1.4, 2) })
        {
            BackupRunSummary? last = null;
            for (var day = 84; day >= missed; day--)
            {
                var started = Now.Date.AddDays(-day).Add(job.Schedules[0].Time.ToTimeSpan());
                if (started > Now)
                    continue;
                var roll = random.NextDouble();
                var outcome = roll < 0.04 ? BackupOutcome.Failed : roll < 0.10 ? BackupOutcome.Warnings : BackupOutcome.Succeeded;
                var run = new BackupRunSummary
                {
                    Started = new DateTimeOffset(started),
                    Duration = TimeSpan.FromSeconds(240 + random.Next(300)),
                    Outcome = outcome,
                    ArchiveName = outcome == BackupOutcome.Failed ? null : $"{job.Name}-laptop-{started:yyyy-MM-ddTHH:mm:ss}",
                    DeduplicatedSize = outcome == BackupOutcome.Failed ? 0 : (long)(gigabytes * Gigabyte * (0.15 + random.NextDouble() * 1.7)),
                    OriginalSize = (long)(gigabytes * 180 * Gigabyte),
                    Files = 120_000 + random.Next(4000),
                    Automatic = true,
                };
                history.Add(job.Id, run);
                last = run;
            }
            jobs.Add(job with { LastRun = last });
        }
        Test.Jobs.Save(jobs);

        // Storage: the local disk fills up over months, the NAS has room, the Storage Box is nearly full.
        var storage = StorageUsageStore.ForDataDirectory(Test.Jobs.DataDirectory);
        Measure(storage, Laptop, StorageSource.Disk, 1000, 520, 250, 11);
        Measure(storage, Nas, StorageSource.Server, 4000, 1300, 1200, 6);
        Measure(storage, StorageBox, StorageSource.Server, 1000, 830, 600, 14);

        // Integrity checks and a restore test.
        var checks = RepositoryCheckStore.ForDataDirectory(Test.Jobs.DataDirectory);
        checks.Add(Laptop.Id, new CheckRecord(new DateTimeOffset(Now.Date.AddDays(-12).AddHours(1.5)), TimeSpan.FromMinutes(14), CheckDepth.Full,
            CheckOutcome.Ok) { Automatic = true });
        checks.Add(Laptop.Id, new CheckRecord(new DateTimeOffset(Now.Date.AddDays(-3).AddHours(1.4)), TimeSpan.FromMinutes(4), CheckDepth.Normal,
            CheckOutcome.Ok) { Automatic = true });
        checks.Add(StorageBox.Id, new CheckRecord(new DateTimeOffset(Now.Date.AddDays(-20).AddHours(4.5)), TimeSpan.FromMinutes(31),
            CheckDepth.Normal, CheckOutcome.Ok));
        checks.Add(Nas.Id, new CheckRecord(new DateTimeOffset(Now.Date.AddDays(-9).AddHours(22.5)), TimeSpan.FromMinutes(9), CheckDepth.Normal,
            CheckOutcome.Ok) { Automatic = true });
        var tests = RestoreTestStore.ForDataDirectory(Test.Jobs.DataDirectory);
        tests.Add(Documents.Id, new RestoreTestRecord(new DateTimeOffset(Now.Date.AddDays(-3).AddHours(1.6)), TimeSpan.FromSeconds(48),
            RestoreTestMode.Compare, RestoreTestOutcome.Ok)
        {
            Archive = $"{Documents.Name}-laptop-{Now.Date.AddDays(-3).AddHours(1):yyyy-MM-ddTHH:mm:ss}",
            Files = [.. Enumerable.Range(1, 20).Select(index => new RestoredFile($"Users/Public/Documents/file{index}.pdf", 250_000 + index * 9_000,
                RestoredFileResult.Identical))],
            Automatic = true,
        });

        // What every archive holds (borg in WSL: C:\ is mnt/c), and what changed between two of them.
        FakeBorg.DefaultContents.Clear();
        FakeBorg.DefaultContents.AddRange(Contents());
        Test.Borg.DiffLines.AddRange(DiffLines());

        // The archives of the laptop backup, oldest first.
        for (var day = 9; day >= 1; day--)
        {
            var started = Now.Date.AddDays(-day).AddHours(1);
            Test.Borg.Archives.Add(($"{Documents.Name}-laptop-{started:yyyy-MM-ddTHH:mm:ss}", started));
        }
    }

    private const string Home = "mnt/c/Users/Public/Documents";

    private static string Invoices => Texts.Pick("Invoices", "Rechnungen");

    private static string Invoice => Texts.Pick("invoice", "rechnung");

    /// <summary>A documents folder as people have it: folders, invoices, letters, a spreadsheet or two.</summary>
    private static List<(string Type, string Path, long Size)> Contents()
    {
        var taxes = Texts.Pick("Taxes 2025", "Steuern 2025");
        var letters = Texts.Pick("Letters", "Briefe");
        var projects = Texts.Pick("Projects", "Projekte");
        List<(string, string, long)> contents =
        [
            ("d", Home, 0),
            ("d", $"{Home}/{Invoices}", 0), ("d", $"{Home}/{taxes}", 0), ("d", $"{Home}/{letters}", 0), ("d", $"{Home}/{projects}", 0),
            ("d", $"{Home}/{projects}/{Texts.Pick("Garden house", "Gartenhaus")}", 0),
            ("-", $"{Home}/{Texts.Pick("budget-2026", "haushalt-2026")}.xlsx", 48_213),
            ("-", $"{Home}/{Texts.Pick("passport-scan", "ausweis-scan")}.pdf", 1_284_552),
            ("-", $"{Home}/{Texts.Pick("rental-contract", "mietvertrag")}.pdf", 2_310_784),
            ("-", $"{Home}/{Texts.Pick("notes", "notizen")}.txt", 6_120),
            ("-", $"{Home}/{Texts.Pick("CV", "Lebenslauf")}.docx", 92_340),
            ("-", $"{Home}/{taxes}/{Texts.Pick("tax-return", "steuererklaerung")}.pdf", 845_002),
            ("-", $"{Home}/{taxes}/{Texts.Pick("receipts", "belege")}.zip", 18_402_113),
            ("-", $"{Home}/{letters}/{Texts.Pick("insurance", "versicherung")}.pdf", 214_880),
            ("-", $"{Home}/{letters}/{Texts.Pick("bank", "bank")}.pdf", 158_312),
            ("-", $"{Home}/{projects}/{Texts.Pick("Garden house", "Gartenhaus")}/{Texts.Pick("plan", "plan")}.pdf", 3_402_551),
            ("-", $"{Home}/{projects}/{Texts.Pick("Garden house", "Gartenhaus")}/{Texts.Pick("costs", "kosten")}.xlsx", 31_877),
        ];
        for (var month = 1; month <= 11; month++)
            contents.Add(("-", $"{Home}/{Invoices}/{Invoice}-2026-{month:00}.pdf", 120_000 + month * 7_300));
        return contents;
    }

    /// <summary>borg diff's lines between two days: a new invoice, an edited spreadsheet, a removed draft.</summary>
    private static IEnumerable<string> DiffLines()
    {
        string Line(string changes, string path) => $$"""{"changes": [{{changes}}], "path": "{{Home}}/{{path}}"}""";
        var mtime = """{"new_mtime": "2026-11-22T17:56:28.566350", "old_mtime": "2026-11-21T09:12:05.982350", "type": "mtime"}""";
        yield return Line("""{"size": 127300, "type": "added"}""", $"{Invoices}/{Invoice}-2026-11.pdf");
        yield return Line("""{"size": 84210, "type": "added"}""", $"{Texts.Pick("Letters", "Briefe")}/{Texts.Pick("energy-supplier", "stromanbieter")}.pdf");
        yield return Line("""{"type": "added directory"}""", Texts.Pick("Holiday 2026", "Urlaub 2026"));
        yield return Line("""{"size": 2210, "type": "added"}""", $"{Texts.Pick("Holiday 2026", "Urlaub 2026")}/{Texts.Pick("packing-list", "packliste")}.txt");
        yield return Line("""{"added": 16384, "removed": 8192, "type": "modified"}, """ + mtime, $"{Texts.Pick("budget-2026", "haushalt-2026")}.xlsx");
        yield return Line("""{"added": 412, "removed": 96, "type": "modified"}, """ + mtime, $"{Texts.Pick("notes", "notizen")}.txt");
        yield return Line("""{"added": 65536, "removed": 32768, "type": "modified"}, """ + mtime,
            $"{Texts.Pick("Projects", "Projekte")}/{Texts.Pick("Garden house", "Gartenhaus")}/{Texts.Pick("costs", "kosten")}.xlsx");
        yield return Line("""{"size": 40122, "type": "removed"}""", $"{Texts.Pick("CV-draft", "Lebenslauf-Entwurf")}.docx");
        yield return Line("""{"new_mode": "-rw-------", "old_mode": "-rw-r--r--", "type": "mode"}""",
            $"{Texts.Pick("passport-scan", "ausweis-scan")}.pdf");
    }

    private static RepositoryConfig Repository(string id, string name, string provider, Dictionary<string, string> values) => new()
    {
        Id = Guid.Parse(id),
        Name = name,
        ProviderId = provider,
        ProviderValues = values,
        PassphraseMode = PassphraseMode.None,
        EncryptionMode = "repokey-blake2",
    };

    private static BackupJob Job(string id, string name, RepositoryConfig repository, string source, int hour) => new()
    {
        Id = Guid.Parse(id),
        Name = name,
        RepositoryId = repository.Id,
        Sources = [source],
        Schedules = [new BackupSchedule { Kind = ScheduleKind.Daily, Time = new TimeOnly(hour, 0) }],
    };

    /// <summary>A measurement a day for twelve weeks, growing by <paramref name="weeklyGigabytes"/> a week up to today's values.</summary>
    private static void Measure(StorageUsageStore store, RepositoryConfig repository, StorageSource source, long totalGigabytes,
        long usedGigabytes, long repositoryGigabytes, double weeklyGigabytes)
    {
        StorageUsage? usage = null;
        for (var day = 84; day >= 0; day--)
        {
            var behind = (long)(day / 7.0 * weeklyGigabytes * Gigabyte);
            var repositoryBytes = repositoryGigabytes * Gigabyte - behind;
            usage = new StorageUsage
            {
                Measured = new DateTimeOffset(Now.Date.AddDays(-day).AddHours(4.6)),
                Source = source,
                Total = totalGigabytes * Gigabyte,
                Free = (totalGigabytes - usedGigabytes) * Gigabyte + behind,
                Repository = new RepositoryStats
                {
                    TotalSize = repositoryBytes * 16,
                    TotalCompressedSize = repositoryBytes * 8,
                    UniqueSize = repositoryBytes * 2,
                    UniqueCompressedSize = repositoryBytes,
                },
            };
            store.History.Add(repository.Id, usage);
        }
        store.Set(repository.Id, usage!);
    }

    /// <summary>The main window's view model with borg found and <paramref name="repository"/> open on <paramref name="tab"/>.</summary>
    public async Task<MainViewModel> MainAsync(RepositoryConfig? repository = null, int? tab = null)
    {
        var main = new MainViewModel(Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        if (repository is not null)
        {
            main.ListedRepository = main.Repositories.Single(item => item.Config.Id == repository.Id);
            if (tab is { } selected)
                main.SelectedDetailsTab = selected;
            if (tab == MainViewModel.ArchivesTab)
                await main.ArchivesLoad;
        }
        return main;
    }

    public void Dispose() => Test.Dispose();
}

/// <summary>Example texts in the language being rendered.</summary>
public static class Texts
{
    public static bool German => AppLanguages.Current.TwoLetterISOLanguageName == "de";

    public static string Pick(string english, string german) => German ? german : english;

    /// <summary>Dates and numbers as people of that language write them.</summary>
    public static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language == "de" ? "de-DE" : "en-US");
}
