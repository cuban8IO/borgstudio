using System.Diagnostics;
using BorgStudio.Core.Ssh;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Ssh;

/// <summary>Reading key files with the real ssh-keygen.</summary>
public sealed class SshKeygenTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private string WorkDirectory => _directory.Combine("ssh");

    /// <summary>A key file made by ssh-keygen itself, optionally protected by <paramref name="password"/>.</summary>
    private string CreateKeyFile(string name, string password = "")
    {
        var file = _directory.Combine(name);
        using var process = Process.Start(new ProcessStartInfo(SshKeygen.Find()!)
        {
            ArgumentList = { "-q", "-t", "ed25519", "-N", password, "-C", "test@example", "-f", file },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return file;
    }

    [SshKeygenFact]
    public async Task An_unprotected_key_is_read_as_it_is()
    {
        var file = CreateKeyFile("id_plain");
        var original = File.ReadAllText(file);

        var result = await SshKeygen.CreateDefault().ReadAsync(file, null, WorkDirectory);

        Assert.Equal(SshKeyReadStatus.Success, result.Status);
        Assert.Equal(original, result.PrivateKey);
        Assert.Equal(File.ReadAllText(file + ".pub").Trim(), result.PublicKey);
        Assert.Empty(Directory.GetFiles(WorkDirectory));
    }

    [SshKeygenFact]
    public async Task A_protected_key_needs_its_password_and_is_stored_without_it()
    {
        var file = CreateKeyFile("id_protected", "key password");
        var original = File.ReadAllText(file);
        var keygen = SshKeygen.CreateDefault();

        Assert.Equal(SshKeyReadStatus.PasswordNeeded, (await keygen.ReadAsync(file, null, WorkDirectory)).Status);
        Assert.Equal(SshKeyReadStatus.WrongPassword, (await keygen.ReadAsync(file, "wrong", WorkDirectory)).Status);

        var result = await keygen.ReadAsync(file, "key password", WorkDirectory);
        Assert.Equal(SshKeyReadStatus.Success, result.Status);
        Assert.Equal(SshPublicKey.WithoutComment(File.ReadAllText(file + ".pub")), SshPublicKey.WithoutComment(result.PublicKey!));

        // The original stays protected; the result opens without password.
        Assert.Equal(original, File.ReadAllText(file));
        var copy = _directory.Combine("decrypted");
        File.WriteAllText(copy, result.PrivateKey);
        var reread = await keygen.ReadAsync(copy, null, WorkDirectory);
        Assert.Equal(SshKeyReadStatus.Success, reread.Status);
        Assert.Empty(Directory.GetFiles(WorkDirectory));
    }

    [SshKeygenFact]
    public async Task Other_files_are_refused()
    {
        var file = _directory.Combine("not-a-key");
        File.WriteAllText(file, "hello");

        var result = await SshKeygen.CreateDefault().ReadAsync(file, null, WorkDirectory);

        Assert.Equal(SshKeyReadStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Without_ssh_keygen_nothing_can_be_read()
    {
        var result = await new SshKeygen(new Core.Processes.ProcessRunner(), executable: null).ReadAsync("x", null, WorkDirectory);

        Assert.Equal(SshKeyReadStatus.Invalid, result.Status);
    }
}
