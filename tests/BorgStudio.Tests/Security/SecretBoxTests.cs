using System.Text;
using BorgStudio.Core.Security;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Security;

public sealed class SecretBoxTests
{
    private static readonly KdfParameters Kdf = TestServices.FastKdf;

    [Fact]
    public void Sealed_data_opens_with_the_same_secret()
    {
        var envelope = SecretBox.Seal("test", Encoding.UTF8.GetBytes("hello"), "correct horse", Kdf);

        var (plaintext, key) = SecretBox.Open(envelope, "correct horse");

        Assert.Equal("hello", Encoding.UTF8.GetString(plaintext));
        Assert.Equal(32, key.Length);
        Assert.Equal("argon2id", envelope.Kdf);
        Assert.DoesNotContain("hello", envelope.Ciphertext);
    }

    [Fact]
    public void A_wrong_secret_is_refused()
    {
        var envelope = SecretBox.Seal("test", Encoding.UTF8.GetBytes("hello"), "correct horse", Kdf);

        Assert.Throws<SecretBoxException>(() => SecretBox.Open(envelope, "wrong horse"));
    }

    [Fact]
    public void Changed_data_or_header_is_detected()
    {
        var envelope = SecretBox.Seal("test", Encoding.UTF8.GetBytes("hello"), "secret", Kdf);
        var (_, key) = SecretBox.Open(envelope, "secret");

        var bytes = Convert.FromBase64String(envelope.Ciphertext);
        bytes[0] ^= 1;
        Assert.Throws<SecretBoxException>(() => SecretBox.Open(envelope with { Ciphertext = Convert.ToBase64String(bytes) }, key));

        // The header is authenticated: another format name (or cost) with the same key fails.
        Assert.Throws<SecretBoxException>(() => SecretBox.Open(envelope with { Format = "other" }, key));
        Assert.Throws<SecretBoxException>(() => SecretBox.Open(envelope with { Iterations = 2 }, key));
    }

    [Fact]
    public void Every_seal_uses_a_new_nonce_and_salt()
    {
        var first = SecretBox.Seal("test", [1, 2, 3], "secret", Kdf);
        var second = SecretBox.Seal("test", [1, 2, 3], "secret", Kdf);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public void Key_derivation_depends_on_secret_salt_and_cost()
    {
        var salt = new byte[16];

        var key = SecretBox.DeriveKey("secret", salt, Kdf);

        Assert.Equal(key, SecretBox.DeriveKey("secret", salt, Kdf));
        Assert.NotEqual(key, SecretBox.DeriveKey("secret!", salt, Kdf));
        Assert.NotEqual(key, SecretBox.DeriveKey("secret", [.. Enumerable.Repeat((byte)1, 16)], Kdf));
        Assert.NotEqual(key, SecretBox.DeriveKey("secret", salt, Kdf with { Iterations = 2 }));
    }

    [Fact]
    public void The_default_cost_is_substantial()
    {
        Assert.True(KdfParameters.Default.MemoryKiB >= 64 * 1024);
        Assert.True(KdfParameters.Default.Iterations >= 3);
    }
}
