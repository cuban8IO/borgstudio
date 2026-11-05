using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace BorgStudio.Core.Ssh;

/// <summary>
/// An SSH login key kept in the vault. Repositories refer to it by <see cref="Id"/>; one key can serve several.
/// The private key is stored unencrypted inside the (encrypted) vault and only written to disk for a single borg call.
/// </summary>
public sealed record SshKeyEntry
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>OpenSSH or PEM private key file content, without passphrase.</summary>
    public required string PrivateKey { get; init; }

    /// <summary>"ssh-ed25519 AAAA… comment", as in authorized_keys.</summary>
    public required string PublicKey { get; init; }

    public DateTimeOffset Created { get; init; }

    /// <summary>Created by BorgStudio (as opposed to imported from a file).</summary>
    public bool Generated { get; init; }

    /// <summary>Key type, e.g. "ssh-ed25519".</summary>
    [JsonIgnore]
    public string Type => PublicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    [JsonIgnore]
    public string Fingerprint => SshPublicKey.Fingerprint(PublicKey);

    /// <summary>A new Ed25519 key.</summary>
    public static SshKeyEntry Generate(string name, string comment, DateTimeOffset now)
    {
        var pair = SshKeyPair.GenerateEd25519(comment);
        return new SshKeyEntry
        {
            Id = Guid.NewGuid(),
            Name = name,
            PrivateKey = pair.PrivateKeyPem,
            PublicKey = pair.PublicKeyLine,
            Created = now,
            Generated = true,
        };
    }
}

public static class SshPublicKey
{
    /// <summary>"SHA256:…" of an authorized_keys line, as <c>ssh-keygen -l</c> shows it; "" if it can't be read.</summary>
    public static string Fingerprint(string publicKeyLine)
    {
        var parts = publicKeyLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return "";
        try
        {
            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(parts[1]))).TrimEnd('=');
        }
        catch (FormatException)
        {
            return "";
        }
    }

    /// <summary>The key without its comment ("type base64"), to compare keys.</summary>
    public static string WithoutComment(string publicKeyLine) =>
        string.Join(' ', publicKeyLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
}
