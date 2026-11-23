using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Borg;

/// <summary>What an archive (or a folder of it) is exported as.</summary>
public enum ExportFormat
{
    Zip,
    Tar,
    TarGz,

    /// <summary>Written by borg itself; needs zstd where borg runs.</summary>
    TarZst,
}

public static class ExportFormats
{
    /// <summary>".zip", ".tar", ".tar.gz", ".tar.zst".</summary>
    public static string Extension(this ExportFormat format) => format switch
    {
        ExportFormat.Tar => ".tar",
        ExportFormat.TarGz => ".tar.gz",
        ExportFormat.TarZst => ".tar.zst",
        _ => ".zip",
    };

    /// <summary>The format a file name asks for; <c>null</c> for none of them.</summary>
    public static ExportFormat? FromFileName(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        return name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal) ? ExportFormat.TarGz
            : name.EndsWith(".tar.zst", StringComparison.Ordinal) || name.EndsWith(".tar.zstd", StringComparison.Ordinal) ? ExportFormat.TarZst
            : name.EndsWith(".tar", StringComparison.Ordinal) ? ExportFormat.Tar
            : name.EndsWith(".zip", StringComparison.Ordinal) ? ExportFormat.Zip
            : null;
    }
}

/// <summary>An export that worked.</summary>
/// <param name="SkippedLinks">Links a zip file cannot hold (symbolic and hard links), left out.</param>
public sealed record BorgExportResult(long Files, int SkippedLinks);

public sealed partial class BorgClient
{
    /// <summary>
    /// <c>borg export-tar</c>: writes the archive – or the paths of it given (borg paths), with the first
    /// <paramref name="stripComponents"/> path segments dropped – to <paramref name="targetFile"/> (a file on this computer).
    /// zip, tar and tar.gz are written by BorgStudio from borg's tar stream (no helper programs, any target folder); tar.zst
    /// by borg (zstd must be installed where borg runs). Progress as for extracting. A cancelled or failed export leaves no file.
    /// </summary>
    public async Task<BorgResult<BorgExportResult>> ExportAsync(BorgInstallation borg, RepositoryLocation location, string archive,
        IReadOnlyList<string> paths, int stripComponents, string targetFile, ExportFormat format, string? passphrase, SshAccess? ssh = null,
        IProgress<BorgExtractProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        void OnLine(string line)
        {
            if (ParseProgressPercent(line) is { MessageId: "extract" } update && update is { Current: { } done, Total: { } total and > 0 })
                progress?.Report(new BorgExtractProgress((long)done, (long)total, update.Info));
            else if (line.Contains("\"extract\"", StringComparison.Ordinal) && line.Contains("Calculating", StringComparison.Ordinal))
                progress?.Report(new BorgExtractProgress(0, 0, null, Preparing: true));
        }

        List<string> options = ["--progress"];
        if (stripComponents > 0)
            options.AddRange(["--strip-components", stripComponents.ToString(CultureInfo.InvariantCulture)]);
        var selected = paths.Select(path => "pp:" + path).ToList();

        try
        {
            if (format == ExportFormat.TarZst)
            {
                if (BorgPath(borg, targetFile) is not { } target)
                    return BorgResult<BorgExportResult>.Failure(new BorgError(BorgErrorKind.UnsupportedPath, targetFile));
                var written = await RunAsync(borg, location, ssh, ["export-tar"], options, [target, .. selected], passphrase, CreateTimeout,
                    cancellationToken, archive, OnLine, interruptible: true);
                return written.Succeeded
                    ? BorgResult<BorgExportResult>.Success(new BorgExportResult(0, 0))
                    : Failed(written.Error!);
            }

            long files = 0;
            var links = 0;
            async Task WriteAsync(Stream tar, CancellationToken token)
            {
                await using var file = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                switch (format)
                {
                    case ExportFormat.Tar:
                        await tar.CopyToAsync(file, token);
                        break;
                    case ExportFormat.TarGz:
                        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
                            await tar.CopyToAsync(gzip, token);
                        break;
                    default:
                        (files, links) = await TarToZipAsync(tar, file, token);
                        break;
                }
            }

            var result = await RunAsync(borg, location, ssh, ["export-tar"], options, ["-", .. selected], passphrase, CreateTimeout,
                cancellationToken, archive, OnLine, interruptible: true, standardOutputStream: WriteAsync);
            return result.Succeeded
                ? BorgResult<BorgExportResult>.Success(new BorgExportResult(files, links))
                : Failed(result.Error!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            Delete(targetFile);
            return BorgResult<BorgExportResult>.Failure(new BorgError(BorgErrorKind.Other, exception.Message));
        }

        BorgResult<BorgExportResult> Failed(BorgError error)
        {
            Delete(targetFile);
            return BorgResult<BorgExportResult>.Failure(error);
        }
    }

    /// <summary>Turns a tar stream into a zip file as it comes: folders and files; links (zip has none) are counted and left out.</summary>
    internal static async Task<(long Files, int SkippedLinks)> TarToZipAsync(Stream tar, Stream target, CancellationToken cancellationToken)
    {
        long files = 0;
        var links = 0;
        using var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        await using var reader = new TarReader(tar, leaveOpen: true);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            var name = entry.Name.TrimStart('/');
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    zip.CreateEntry(name.TrimEnd('/') + "/");
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    var zipEntry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    // zip keeps times from 1980 on.
                    var modified = entry.ModificationTime;
                    zipEntry.LastWriteTime = modified.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : modified;
                    await using (var output = zipEntry.Open())
                    {
                        if (entry.DataStream is { } data)
                            await data.CopyToAsync(output, cancellationToken);
                    }
                    files++;
                    break;
                case TarEntryType.SymbolicLink or TarEntryType.HardLink:
                    links++;
                    break;
            }
        }
        return (files, links);
    }

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
