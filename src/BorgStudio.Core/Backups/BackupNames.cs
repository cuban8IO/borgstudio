namespace BorgStudio.Core.Backups;

/// <summary>How a job's archives are named, and which archives belong to a job.</summary>
public static class BackupNames
{
    /// <summary>"Dokumente &amp; Fotos" → "Dokumente-Fotos": safe in an archive name and for later per-job clean-ups.</summary>
    public static string ArchivePrefix(string jobName)
    {
        var safe = new string(jobName.Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '-').ToArray());
        while (safe.Contains("--", StringComparison.Ordinal))
            safe = safe.Replace("--", "-", StringComparison.Ordinal);
        safe = safe.Trim('-');
        return safe.Length > 0 ? safe : "backup";
    }

    /// <summary>The new archive's name; borg fills in the computer's name and the time.</summary>
    public static string ArchiveName(string jobName) => $"{ArchivePrefix(jobName)}-{{hostname}}-{{now:%Y-%m-%dT%H:%M:%S}}";

    /// <summary>
    /// The archives this computer created for the job – "Documents-{hostname}-*" – which is what its clean-up looks at.
    /// With the host name a job "Documents-Old" (or another computer backing up into the same repository) stays out.
    /// </summary>
    public static string ArchiveGlob(string jobName) => $"{ArchivePrefix(jobName)}-{{hostname}}-*";
}
