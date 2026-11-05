using System.Text;
using BorgStudio.Core.Processes;

namespace BorgStudio.Core.Ssh;

public enum SshKeyReadStatus
{
    Success,

    /// <summary>The key is protected by a password: ask for it and try again.</summary>
    PasswordNeeded,

    WrongPassword,

    /// <summary>Not a private key ssh-keygen understands, or ssh-keygen is missing (see the message).</summary>
    Invalid,
}

/// <param name="PrivateKey">The private key without password protection.</param>
/// <param name="PublicKey">"type base64 comment".</param>
public sealed record SshKeyReadResult(SshKeyReadStatus Status, string? PrivateKey = null, string? PublicKey = null, string? Message = null);

/// <summary>
/// Reads private key files for importing them, with OpenSSH's own <c>ssh-keygen</c> (part of Windows 10/11, macOS and
/// Linux): it knows every key format, derives the public key and removes a password, so the key can be used without
/// prompts.
/// </summary>
public sealed class SshKeygen(IProcessRunner processRunner, string? executable)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static SshKeygen CreateDefault() => new(new ProcessRunner(), Find());

    /// <summary>Path of ssh-keygen, or <c>null</c> if it isn't there.</summary>
    public static string? Find()
    {
        if (OperatingSystem.IsWindows())
        {
            var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe");
            if (File.Exists(bundled))
                return bundled;
        }

        var name = OperatingSystem.IsWindows() ? "ssh-keygen.exe" : "ssh-keygen";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Reads <paramref name="privateKeyFile"/> (left untouched: the work happens on a private copy in
    /// <paramref name="workDirectory"/>, which is deleted afterwards).
    /// </summary>
    public async Task<SshKeyReadResult> ReadAsync(string privateKeyFile, string? password, string workDirectory,
        CancellationToken cancellationToken = default)
    {
        if (executable is null)
            return new SshKeyReadResult(SshKeyReadStatus.Invalid, Message: "ssh-keygen not found");

        string content;
        try
        {
            content = await File.ReadAllTextAsync(privateKeyFile, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SshKeyReadResult(SshKeyReadStatus.Invalid, Message: exception.Message);
        }

        var copy = TemporaryKeyFile.Write(workDirectory, "import-", content);
        try
        {
            var publicKey = await RunAsync(["-y", "-P", password ?? "", "-f", copy], cancellationToken);
            if (publicKey is not { ExitCode: 0 })
            {
                var error = publicKey?.StandardError.Trim() ?? "";
                if (error.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
                    return new SshKeyReadResult(password is null ? SshKeyReadStatus.PasswordNeeded : SshKeyReadStatus.WrongPassword);
                return new SshKeyReadResult(SshKeyReadStatus.Invalid, Message: error.Length > 0 ? error : "ssh-keygen failed");
            }

            var publicLine = publicKey.StandardOutput.Trim();
            if (publicLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3)
                publicLine += " " + Path.GetFileName(privateKeyFile);

            if (!string.IsNullOrEmpty(password))
            {
                // Rewrites the copy without password.
                var removed = await RunAsync(["-p", "-P", password, "-N", "", "-f", copy], cancellationToken);
                if (removed is not { ExitCode: 0 })
                    return new SshKeyReadResult(SshKeyReadStatus.Invalid, Message: removed?.StandardError.Trim());
                content = await File.ReadAllTextAsync(copy, cancellationToken);
            }

            return new SshKeyReadResult(SshKeyReadStatus.Success, content, publicLine);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    private async Task<ProcessResult?> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await processRunner.RunAsync(executable!, arguments, Timeout, cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}

/// <summary>Private key files that exist only while something uses them, with permissions OpenSSH accepts.</summary>
public static class TemporaryKeyFile
{
    /// <summary>Leftovers older than this (e.g. after a crash) are removed.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(60);

    /// <summary>Writes <paramref name="content"/> to a new file "prefix&lt;random&gt;" in <paramref name="directory"/>.</summary>
    public static string Write(string directory, string prefix, string content)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        RemoveLeftovers(directory, prefix);

        var path = Path.Combine(directory, prefix + Guid.NewGuid().ToString("N"));
        // OpenSSH refuses private keys others can read. On Windows the user data folder is already private.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(content.EndsWith('\n') ? content : content + "\n");
        return path;
    }

    private static void RemoveLeftovers(string directory, string prefix)
    {
        foreach (var file in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > MaximumAge)
                    File.Delete(file);
            }
            catch (IOException)
            {
                // In use or gone: next time.
            }
        }
    }
}
