using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace BorgStudio.Core.Security;

/// <summary>Argon2id cost parameters.</summary>
public sealed record KdfParameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>64 MiB, 3 passes: roughly half a second per guess on a current PC.</summary>
    public static KdfParameters Default { get; } = new(64 * 1024, 3, 1);
}

/// <summary>
/// Data encrypted with a key derived from a PIN or password: Argon2id for the key, AES-256-GCM for the data.
/// The header (format, KDF parameters, salt) is authenticated too, so it cannot be changed unnoticed.
/// Stored as JSON; the same envelope serves the vault and (later) exports.
/// </summary>
public sealed record SecretEnvelope
{
    public required string Format { get; init; }

    public int Version { get; init; } = 1;

    public required string Kdf { get; init; }

    public required int MemoryKiB { get; init; }

    public required int Iterations { get; init; }

    public required int Parallelism { get; init; }

    public required string Salt { get; init; }

    public required string Nonce { get; init; }

    /// <summary>Encrypted data followed by the 16-byte authentication tag.</summary>
    public required string Ciphertext { get; init; }

    [JsonIgnore]
    public KdfParameters Parameters => new(MemoryKiB, Iterations, Parallelism);

    [JsonIgnore]
    internal byte[] AssociatedData =>
        Encoding.UTF8.GetBytes($"{Format}|{Version}|{Kdf}|{MemoryKiB}|{Iterations}|{Parallelism}|{Salt}");
}

/// <summary>The data could not be decrypted: wrong PIN/password, or the file was changed.</summary>
public sealed class SecretBoxException(string message) : Exception(message);

public static class SecretBox
{
    public const string Argon2id = "argon2id";

    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagBits = 128;

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    /// <summary>The AES key for <paramref name="secret"/> – deliberately slow (Argon2id).</summary>
    public static byte[] DeriveKey(string secret, byte[] salt, KdfParameters parameters)
    {
        var generator = new Argon2BytesGenerator();
        generator.Init(new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithMemoryAsKB(parameters.MemoryKiB)
            .WithIterations(parameters.Iterations)
            .WithParallelism(parameters.Parallelism)
            .WithSalt(salt)
            .Build());
        var key = new byte[KeySize];
        generator.GenerateBytes(secret.ToCharArray(), key);
        return key;
    }

    /// <summary>Encrypts with an already derived <paramref name="key"/> (fresh nonce every time).</summary>
    public static SecretEnvelope Seal(string format, byte[] plaintext, byte[] key, byte[] salt, KdfParameters parameters)
    {
        var envelope = new SecretEnvelope
        {
            Format = format,
            Kdf = Argon2id,
            MemoryKiB = parameters.MemoryKiB,
            Iterations = parameters.Iterations,
            Parallelism = parameters.Parallelism,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceSize)),
            Ciphertext = "",
        };
        var cipher = Cipher(forEncryption: true, key, envelope);
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var length = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        cipher.DoFinal(output, length);
        return envelope with { Ciphertext = Convert.ToBase64String(output) };
    }

    /// <summary>Encrypts with a key derived from <paramref name="secret"/> and a new salt.</summary>
    public static SecretEnvelope Seal(string format, byte[] plaintext, string secret, KdfParameters parameters)
    {
        var salt = NewSalt();
        return Seal(format, plaintext, DeriveKey(secret, salt, parameters), salt, parameters);
    }

    /// <summary>Decrypts with an already derived key.</summary>
    /// <exception cref="SecretBoxException">Wrong key or changed data.</exception>
    public static byte[] Open(SecretEnvelope envelope, byte[] key)
    {
        if (envelope.Kdf != Argon2id)
            throw new SecretBoxException($"Unknown key derivation '{envelope.Kdf}'.");

        try
        {
            var data = Convert.FromBase64String(envelope.Ciphertext);
            var cipher = Cipher(forEncryption: false, key, envelope);
            var output = new byte[cipher.GetOutputSize(data.Length)];
            var length = cipher.ProcessBytes(data, 0, data.Length, output, 0);
            length += cipher.DoFinal(output, length);
            return output[..length];
        }
        catch (Exception exception) when (exception is InvalidCipherTextException or FormatException)
        {
            throw new SecretBoxException("Wrong PIN or password, or the file was changed.");
        }
    }

    /// <summary>Derives the key from <paramref name="secret"/> and decrypts; returns the key for later re-encryption.</summary>
    /// <exception cref="SecretBoxException">Wrong secret or changed data.</exception>
    public static (byte[] Plaintext, byte[] Key) Open(SecretEnvelope envelope, string secret)
    {
        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(envelope.Salt);
        }
        catch (FormatException)
        {
            throw new SecretBoxException("The file is damaged.");
        }

        var key = DeriveKey(secret, salt, envelope.Parameters);
        return (Open(envelope, key), key);
    }

    private static GcmBlockCipher Cipher(bool forEncryption, byte[] key, SecretEnvelope envelope)
    {
        var cipher = new GcmBlockCipher(AesUtilities.CreateEngine());
        cipher.Init(forEncryption,
            new AeadParameters(new KeyParameter(key), TagBits, Convert.FromBase64String(envelope.Nonce), envelope.AssociatedData));
        return cipher;
    }
}
