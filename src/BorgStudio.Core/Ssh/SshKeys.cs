using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Security;

namespace BorgStudio.Core.Ssh;

/// <summary>A new Ed25519 key pair in the formats OpenSSH reads.</summary>
/// <param name="PrivateKeyPem">"-----BEGIN OPENSSH PRIVATE KEY-----" file content, not encrypted.</param>
/// <param name="PublicKeyLine">"ssh-ed25519 AAAA… comment", as in authorized_keys.</param>
public sealed record SshKeyPair(string PrivateKeyPem, string PublicKeyLine)
{
    public static SshKeyPair GenerateEd25519(string comment)
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();

        var privateBlob = OpenSshPrivateKeyUtilities.EncodePrivateKey(pair.Private);
        var publicBlob = OpenSshPublicKeyUtilities.EncodePublicKey(pair.Public);

        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        var base64 = Convert.ToBase64String(privateBlob);
        for (var offset = 0; offset < base64.Length; offset += 70)
            pem.Append(base64, offset, Math.Min(70, base64.Length - offset)).Append('\n');
        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");

        return new SshKeyPair(pem.ToString(), $"ssh-ed25519 {Convert.ToBase64String(publicBlob)} {comment}");
    }
}

/// <summary>A key created by <see cref="SshKeyStore"/>.</summary>
public sealed record SshGeneratedKey(string PrivateKeyFile, string PublicKeyLine);

/// <summary>
/// The SSH keys BorgStudio creates (one per repository) and its own known_hosts file,
/// kept in the user data folder with permissions OpenSSH accepts.
/// </summary>
public sealed class SshKeyStore(string directory)
{
    public static SshKeyStore CreateDefault() => new(Path.Combine(AppPaths.DataDirectory, "ssh"));

    public string Directory { get; } = directory;

    public string KnownHostsFile => Path.Combine(Directory, "known_hosts");

    /// <summary>Creates a new key pair named <paramref name="name"/> (private key) and <c>name.pub</c>.</summary>
    public SshGeneratedKey Create(string name, string comment)
    {
        EnsureDirectory();
        var pair = SshKeyPair.GenerateEd25519(comment);
        var path = Path.Combine(Directory, name);
        WritePrivate(path, pair.PrivateKeyPem);
        File.WriteAllText(path + ".pub", pair.PublicKeyLine + "\n");
        return new SshGeneratedKey(path, pair.PublicKeyLine);
    }

    /// <summary>Deletes a key created here (and its .pub); keys elsewhere are never touched.</summary>
    public void Delete(string privateKeyFile)
    {
        var full = Path.GetFullPath(privateKeyFile);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Directory), StringComparison.Ordinal))
            return;
        File.Delete(full);
        File.Delete(full + ".pub");
    }

    public void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
            System.IO.Directory.CreateDirectory(Directory);
        else
            System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WritePrivate(string path, string content)
    {
        // OpenSSH refuses private keys others can read. On Windows the user data folder is already private.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}

/// <summary>A server's host key, as received during the SSH handshake.</summary>
/// <param name="Blob">The key in SSH wire format (what known_hosts stores base64-encoded).</param>
public sealed class SshHostKey(byte[] blob)
{
    public byte[] Blob { get; } = blob;

    /// <summary>Key type from the blob, e.g. "ssh-ed25519" or "ssh-rsa".</summary>
    public string Type
    {
        get
        {
            var length = (Blob[0] << 24) | (Blob[1] << 16) | (Blob[2] << 8) | Blob[3];
            return Encoding.ASCII.GetString(Blob, 4, length);
        }
    }

    public string Base64 => Convert.ToBase64String(Blob);

    /// <summary>"SHA256:…" exactly like <c>ssh-keygen -l</c> and the ssh client show it.</summary>
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');

    public bool SameAs(SshHostKey other) => Blob.AsSpan().SequenceEqual(other.Blob);
}

/// <summary>BorgStudio's own known_hosts file (OpenSSH format, unhashed host names).</summary>
public sealed class KnownHostsFile(string path)
{
    public string Path { get; } = path;

    /// <summary>"host" for port 22, "[host]:port" otherwise – the way OpenSSH looks entries up.</summary>
    public static string HostPattern(string host, int port) =>
        port == 22 ? host.ToLowerInvariant() : $"[{host.ToLowerInvariant()}]:{port}";

    public IReadOnlyList<SshHostKey> Find(string host, int port)
    {
        if (!File.Exists(Path))
            return [];

        var pattern = HostPattern(host, port);
        var keys = new List<SshHostKey>();
        foreach (var line in File.ReadLines(Path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || line.TrimStart().StartsWith('#'))
                continue;
            if (!parts[0].Split(',').Contains(pattern, StringComparer.OrdinalIgnoreCase))
                continue;
            try
            {
                keys.Add(new SshHostKey(Convert.FromBase64String(parts[2])));
            }
            catch (FormatException)
            {
                // Not ours to fix; ssh will complain about it itself.
            }
        }
        return keys;
    }

    public void Add(string host, int port, SshHostKey key)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.AppendAllText(Path, $"{HostPattern(host, port)} {key.Type} {key.Base64}\n");
    }
}

/// <summary>Entries for ~/.ssh/authorized_keys on the backup server.</summary>
public static class AuthorizedKeys
{
    /// <summary>
    /// The line to add. With <paramref name="restrictToRepository"/>, the key can only run
    /// <c>borg serve</c> for exactly that repository – no shell, no port forwarding, no other paths.
    /// </summary>
    public static string Line(string publicKeyLine, string? restrictToRepository, string remoteBorg = "borg")
    {
        if (restrictToRepository is null)
            return publicKeyLine;

        return $"command=\"{remoteBorg} serve --restrict-to-repository \\\"{restrictToRepository}\\\"\",restrict {publicKeyLine}";
    }

    /// <summary>Shell command (POSIX sh on the server) appending <paramref name="line"/> to authorized_keys.</summary>
    public static string AppendCommand(string line)
    {
        if (line.Contains('\'') || line.Contains('\n'))
            throw new ArgumentException("authorized_keys line must not contain single quotes or line breaks.", nameof(line));
        return $"umask 077 && mkdir -p ~/.ssh && printf '%s\\n' '{line}' >> ~/.ssh/authorized_keys";
    }
}
