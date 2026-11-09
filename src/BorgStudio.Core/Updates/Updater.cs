using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace BorgStudio.Core.Updates;

/// <summary>Why an update cannot be installed by the app (the release page still can be opened).</summary>
public enum UpdateBlocker
{
    None,

    /// <summary>A local build ("0.0.0-dev") is never replaced.</summary>
    DevelopmentBuild,

    /// <summary>No package is built for this operating system and processor.</summary>
    UnsupportedPlatform,

    /// <summary>The release has no package for this platform, or no checksums.</summary>
    NoPackage,

    /// <summary>The app's folder cannot be written (e.g. installed for all users).</summary>
    NotWritable,
}

/// <summary>Outcome of asking for the newest release.</summary>
public sealed record UpdateCheck(ReleaseInfo? Latest, bool IsNewer, ReleaseAsset? Package, ReleaseAsset? Checksums, UpdateBlocker Blocker)
{
    public bool CanInstall => IsNewer && Blocker == UpdateBlocker.None;
}

/// <summary>A downloaded, checked and unpacked release, ready to replace the running app.</summary>
public sealed record StagedUpdate(Version Version, string AppRoot);

public enum UpdateError
{
    /// <summary>The checksum file does not list the package.</summary>
    ChecksumMissing,

    /// <summary>The download does not match its checksum.</summary>
    ChecksumMismatch,

    /// <summary>The package does not contain the app where expected.</summary>
    UnexpectedPackage,
}

public sealed class UpdateException(UpdateError error) : Exception(error.ToString())
{
    public UpdateError Error { get; } = error;
}

/// <summary>Result of the last update, written by <see cref="UpdateApplier"/> and shown by the next start.</summary>
public sealed record UpdateOutcome(string Version, bool Succeeded, string? Error);

/// <summary>Where the running app lives: the folder replaced by an update and the executable inside it.</summary>
public sealed record InstallLocation(string Directory, string RelativeExecutable)
{
    public string Executable => Path.Combine(Directory, RelativeExecutable);

    /// <summary>The running app's folder; on macOS the whole BorgStudio.app bundle.</summary>
    public static InstallLocation? Detect()
    {
        if (Environment.ProcessPath is not { } executable || Path.GetDirectoryName(executable) is not { } folder)
            return null;
        var root = BundleOf(folder) ?? folder;
        return new InstallLocation(root, Path.GetRelativePath(root, executable));
    }

    /// <summary>"/Applications/BorgStudio.app" for "/Applications/BorgStudio.app/Contents/MacOS".</summary>
    public static string? BundleOf(string folder)
    {
        var macOs = new DirectoryInfo(folder);
        return macOs.Name == "MacOS" && macOs.Parent is { Name: "Contents", Parent: { } bundle }
            && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? bundle.FullName
                : null;
    }

    /// <summary>Whether the app may write its own folder (and so replace its files).</summary>
    public bool IsWritable()
    {
        try
        {
            var probe = Path.Combine(Directory, $".borgstudio-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return File.Exists(Executable) && !new FileInfo(Executable).IsReadOnly;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Finds, downloads and prepares updates. Releases are the portable packages on GitHub; each release lists the
/// SHA-256 of its packages in <see cref="ChecksumsFile"/>. Installing is done by <see cref="UpdateApplier"/> in the
/// new version's process once this one has closed.
/// </summary>
public sealed class Updater(IUpdateSource source, string currentVersion, InstallLocation? location, string workDirectory)
{
    public const string ChecksumsFile = "SHA256SUMS";

    public static IReadOnlyList<string> SupportedRids { get; } = ["win-x64", "linux-x64", "osx-arm64", "osx-x64"];

    public static Updater CreateDefault(string currentVersion) => new(new GitHubUpdateSource(currentVersion), currentVersion,
        InstallLocation.Detect(), Path.Combine(AppPaths.DataDirectory, "updates"));

    /// <summary>The running version; null for a local build.</summary>
    public Version? Current { get; } = UpdateVersion.Parse(currentVersion);

    public string CurrentText => currentVersion;

    public InstallLocation? Location => location;

    public string WorkDirectory => workDirectory;

    /// <summary>The package flavour this app needs ("win-x64"); null if none is built.</summary>
    public string? Rid { get; init; } = CurrentRid();

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var latest = await source.GetLatestAsync(cancellationToken);
        if (latest is null)
            return new UpdateCheck(null, false, null, null, UpdateBlocker.None);

        var package = Rid is null ? null : latest.Assets.FirstOrDefault(asset => asset.Name.EndsWith($"-{Rid}{Extension(Rid)}", StringComparison.OrdinalIgnoreCase));
        var checksums = latest.Assets.FirstOrDefault(asset => asset.Name == ChecksumsFile);
        var blocker = Current is null ? UpdateBlocker.DevelopmentBuild
            : Rid is null ? UpdateBlocker.UnsupportedPlatform
            : package is null || checksums is null ? UpdateBlocker.NoPackage
            : location is null || !location.IsWritable() ? UpdateBlocker.NotWritable
            : UpdateBlocker.None;
        return new UpdateCheck(latest, Current is not null && latest.Version > Current, package, checksums, blocker);
    }

    /// <summary>Downloads the package, checks it against the release's checksums and unpacks it.</summary>
    /// <param name="progress">Bytes downloaded so far (of <see cref="ReleaseAsset.Size"/>).</param>
    public async Task<StagedUpdate> DownloadAsync(UpdateCheck check, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        if (!check.CanInstall || check is not { Latest: { } release, Package: { } package, Checksums: { } checksums } || location is null)
            throw new InvalidOperationException("This update cannot be installed.");

        var folder = Path.Combine(workDirectory, release.Version.ToString());
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        var expected = FindChecksum(await source.GetTextAsync(checksums.Url, cancellationToken), package.Name)
            ?? throw new UpdateException(UpdateError.ChecksumMissing);
        var archive = Path.Combine(folder, package.Name);
        await source.DownloadAsync(package.Url, archive, progress, cancellationToken);
        if (!string.Equals(await Sha256Async(archive, cancellationToken), expected, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateError.ChecksumMismatch);

        var unpacked = Path.Combine(folder, "app");
        await Task.Run(() => Extract(archive, unpacked), cancellationToken);
        File.Delete(archive);
        var root = FindAppRoot(unpacked, location.RelativeExecutable) ?? throw new UpdateException(UpdateError.UnexpectedPackage);
        MakeExecutable(Path.Combine(root, location.RelativeExecutable));
        return new StagedUpdate(release.Version, root);
    }

    /// <summary>Starts the new version as installer; it waits for <paramref name="processId"/> to end.</summary>
    public ProcessStartInfo ApplyCommand(StagedUpdate staged, int processId)
    {
        if (location is null)
            throw new InvalidOperationException("The app's folder is unknown.");
        // Not inside the app's folder: a working directory there would keep it busy on Windows.
        var start = new ProcessStartInfo(Path.Combine(staged.AppRoot, location.RelativeExecutable))
        {
            UseShellExecute = false,
            WorkingDirectory = staged.AppRoot,
        };
        foreach (var argument in UpdateApplier.Arguments(staged.AppRoot, location, processId, workDirectory, staged.Version.ToString()))
            start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>The outcome of an update applied since the last start, if any; removes what the update left behind.</summary>
    public UpdateOutcome? TakeOutcome()
    {
        UpdateOutcome? outcome = null;
        var file = Path.Combine(workDirectory, UpdateApplier.OutcomeFile);
        try
        {
            if (File.Exists(file))
            {
                outcome = JsonSerializer.Deserialize<UpdateOutcome>(File.ReadAllText(file));
                File.Delete(file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        CleanUp();
        return outcome;
    }

    /// <summary>Deletes downloaded versions; one still in use (its installer just ending) goes next time.</summary>
    public void CleanUp()
    {
        if (!Directory.Exists(workDirectory))
            return;
        foreach (var folder in Directory.EnumerateDirectories(workDirectory))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static string? CurrentRid()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        var system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null;
        var rid = $"{system}-{architecture}";
        return SupportedRids.Contains(rid) ? rid : null;
    }

    /// <summary>Linux packages are tarballs (they keep the file modes), the others zips.</summary>
    public static string Extension(string rid) => rid.StartsWith("linux", StringComparison.Ordinal) ? ".tar.gz" : ".zip";

    /// <summary>The checksum of <paramref name="fileName"/> in a "sha256sum" listing ("&lt;hex&gt;  &lt;name&gt;").</summary>
    public static string? FindChecksum(string listing, string fileName)
    {
        foreach (var line in listing.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName && parts[0].Length == 64)
                return parts[0];
        }
        return null;
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>Unpacks a zip or tar.gz; entries pointing outside <paramref name="destination"/> are refused by .NET.</summary>
    public static void Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(archive);
            using var unzipped = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(unzipped, destination, overwriteFiles: true);
        }
        else
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
        }
    }

    /// <summary>The unpacked folder holding the app: the package itself or its only top folder (BorgStudio, BorgStudio.app).</summary>
    public static string? FindAppRoot(string unpacked, string relativeExecutable)
    {
        if (File.Exists(Path.Combine(unpacked, relativeExecutable)))
            return unpacked;
        return Directory.EnumerateDirectories(unpacked).FirstOrDefault(folder => File.Exists(Path.Combine(folder, relativeExecutable)));
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
