using BorgStudio.Core.Borg;
using BorgStudio.Core.Processes;
using BorgStudio.Plugins;

namespace BorgStudio.Tests.Borg;

public class BorgClientTests
{
    private const string Wsl = @"C:\Windows\system32\wsl.exe";

    private static readonly BorgInstallation NativeBorg = new("/usr/bin/borg", new BorgVersion(1, 4, 1), BorgRuntime.Native);
    private static readonly BorgInstallation WslBorg = new("/usr/bin/borg", new BorgVersion(1, 4, 1), BorgRuntime.Wsl);

    private const string InfoJson = """
        {
          "cache": { "path": "/home/me/.cache/borg/abc" },
          "encryption": { "mode": "repokey-blake2" },
          "repository": { "id": "0123abcd", "last_modified": "2026-10-07T14:03:12.000000", "location": "/backups/repo" }
        }
        """;

    /// <summary>Records the calls and answers every one with a fixed result.</summary>
    private sealed class RecordingRunner(ProcessResult? result) : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments, string? Input)> Calls { get; } = [];
        public string? FileName { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; } = [];
        public IReadOnlyDictionary<string, string?> Environment { get; private set; } = new Dictionary<string, string?>();

        public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments, standardInput));
            FileName = fileName;
            Arguments = arguments;
            Environment = environment ?? new Dictionary<string, string?>();
            return Task.FromResult(result);
        }
    }

    private static readonly RepositoryLocation SshLocation = new("ssh://backup@nas.local:2222/./borg/laptop")
    {
        Ssh = new SshEndpoint("nas.local", 2222, "backup", "borg/laptop"),
    };

    [Fact]
    public async Task SSH_repositories_get_a_strict_batch_mode_ssh_command()
    {
        var runner = new RecordingRunner(new ProcessResult(0, InfoJson, ""));
        var ssh = new SshAccess("/home/me/.local/share/BorgStudio/ssh/key 1", "/home/me/.local/share/BorgStudio/ssh/known_hosts");

        await new BorgClient(runner).InfoAsync(NativeBorg, SshLocation, "secret", ssh);

        Assert.Equal(
            "ssh -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes"
            + " -o UserKnownHostsFile='/home/me/.local/share/BorgStudio/ssh/known_hosts'"
            + " -i '/home/me/.local/share/BorgStudio/ssh/key 1'",
            runner.Environment["BORG_RSH"]);
        Assert.Equal("ssh://backup@nas.local:2222/./borg/laptop", runner.Arguments[^1]);
    }

    [Fact]
    public async Task Local_repositories_get_no_ssh_command()
    {
        var runner = new RecordingRunner(new ProcessResult(0, InfoJson, ""));

        await new BorgClient(runner).InfoAsync(NativeBorg, new RepositoryLocation("/backups/repo"), "secret",
            new SshAccess("/key", "/known_hosts"));

        Assert.False(runner.Environment.ContainsKey("BORG_RSH"));
    }

    [Fact]
    public async Task WSL_gets_private_copies_of_key_and_known_hosts()
    {
        using var directory = new TestSupport.TemporaryDirectory();
        var key = directory.Combine("0123-key");
        var knownHosts = directory.Combine("known_hosts");
        File.WriteAllText(key, "PRIVATE KEY");
        File.WriteAllText(knownHosts, "[nas.local]:2222 ssh-ed25519 AAAA\n");
        var runner = new RecordingRunner(new ProcessResult(0, InfoJson, ""));

        await new BorgClient(runner, Wsl).InfoAsync(WslBorg, SshLocation, "secret", new SshAccess(key, knownHosts));

        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal("PRIVATE KEY", runner.Calls[0].Input);
        Assert.Contains("umask 077", runner.Calls[0].Arguments[^1]);
        Assert.EndsWith("cat > ~/.borgstudio/ssh/key-0123-key", runner.Calls[0].Arguments[^1]);
        Assert.Equal("[nas.local]:2222 ssh-ed25519 AAAA\n", runner.Calls[1].Input);
        Assert.Equal(
            "ssh -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes"
            + " -o UserKnownHostsFile='~/.borgstudio/ssh/known_hosts' -i '~/.borgstudio/ssh/key-0123-key'",
            runner.Environment["BORG_RSH"]);
        Assert.Contains("BORG_RSH", runner.Environment["WSLENV"]!.Split(':'));
    }

    [Theory]
    [InlineData("Remote: Host key verification failed.", BorgErrorKind.SshHostKeyFailed)]
    [InlineData("Remote: backup@nas.local: Permission denied (publickey).", BorgErrorKind.SshAuthenticationFailed)]
    public void SSH_problems_reported_by_borg_are_recognized(string remoteLine, BorgErrorKind expected)
    {
        var standardError = $$"""{"type": "log_message", "levelname": "WARNING", "message": "{{remoteLine}}"}""" + "\n"
            + ErrorLine("ConnectionClosedWithHint", "Connection closed by remote host");

        Assert.Equal(expected, BorgClient.ParseError(standardError).Kind);
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData(@"C:\Users\me\key", @"'C:\Users\me\key'")]
    [InlineData("it's", "'it'\"'\"'s'")]
    public void ShellQuote_keeps_backslashes_and_escapes_quotes(string value, string expected)
    {
        Assert.Equal(expected, BorgClient.ShellQuote(value));
    }

    private static string ErrorLine(string msgid, string message) =>
        $$"""{"type": "log_message", "time": 1.0, "levelname": "ERROR", "name": "borg.archiver", "message": "{{message}}", "msgid": "{{msgid}}"}""";

    [Fact]
    public async Task Info_runs_borg_with_json_output_and_passphrase_in_the_environment()
    {
        var runner = new RecordingRunner(new ProcessResult(0, InfoJson, ""));
        var location = new RepositoryLocation("/backups/repo") { BorgArguments = ["--remote-path=borg-1.4"] };

        var result = await new BorgClient(runner).InfoAsync(NativeBorg, location, "secret");

        Assert.True(result.Succeeded);
        Assert.Equal("0123abcd", result.Value!.Id);
        Assert.Equal("repokey-blake2", result.Value.EncryptionMode);
        Assert.Equal(new DateTime(2026, 10, 7, 14, 3, 12), result.Value.LastModified!.Value.DateTime);

        Assert.Equal("/usr/bin/borg", runner.FileName);
        Assert.Equal(["info", "--log-json", "--remote-path=borg-1.4", "--json", "/backups/repo"], runner.Arguments);
        Assert.Equal("secret", runner.Environment["BORG_PASSPHRASE"]);
        Assert.DoesNotContain("secret", runner.Arguments);
        Assert.Equal("yes", runner.Environment["BORG_RELOCATED_REPO_ACCESS_IS_OK"]);
    }

    [Fact]
    public async Task Missing_passphrase_is_passed_as_empty_so_borg_never_prompts()
    {
        var runner = new RecordingRunner(new ProcessResult(0, InfoJson, ""));

        await new BorgClient(runner).InfoAsync(NativeBorg, new RepositoryLocation("/backups/repo"), null);

        Assert.Equal("", runner.Environment["BORG_PASSPHRASE"]);
    }

    [Theory]
    [InlineData(BorgEncryption.RepokeyBlake2, "--encryption=repokey-blake2")]
    [InlineData(BorgEncryption.KeyfileBlake2, "--encryption=keyfile-blake2")]
    [InlineData(BorgEncryption.None, "--encryption=none")]
    public async Task Init_passes_the_encryption_mode(BorgEncryption encryption, string expectedOption)
    {
        var runner = new RecordingRunner(new ProcessResult(0, "", ""));

        var result = await new BorgClient(runner).InitAsync(NativeBorg, new RepositoryLocation("/backups/new"), encryption, "secret");

        Assert.True(result.Succeeded);
        Assert.Equal(["init", "--log-json", expectedOption, "--make-parent-dirs", "/backups/new"], runner.Arguments);
    }

    [Fact]
    public async Task WSL_translates_paths_and_forwards_the_borg_variables()
    {
        var runner = new RecordingRunner(new ProcessResult(0, "", ""));

        var result = await new BorgClient(runner, Wsl).ExportKeyAsync(
            WslBorg, new RepositoryLocation(@"E:\Backups\repo"), @"C:\Users\me\Documents\repo.key");

        Assert.True(result.Succeeded);
        Assert.Equal(Wsl, runner.FileName);
        Assert.Equal(
            ["-e", "/usr/bin/borg", "key", "export", "--log-json", "/mnt/e/Backups/repo", "/mnt/c/Users/me/Documents/repo.key"],
            runner.Arguments);
        Assert.Contains("BORG_PASSPHRASE", runner.Environment["WSLENV"]!.Split(':'));
    }

    [Fact]
    public async Task WSL_keeps_remote_urls_and_rejects_network_shares()
    {
        var runner = new RecordingRunner(new ProcessResult(0, InfoJson, ""));
        var client = new BorgClient(runner, Wsl);

        await client.InfoAsync(WslBorg, new RepositoryLocation("ssh://user@host:23/./repo"), "secret");
        Assert.Equal("ssh://user@host:23/./repo", runner.Arguments[^1]);

        var share = await client.InfoAsync(WslBorg, new RepositoryLocation(@"\\nas\backup\repo"), "secret");
        Assert.Equal(BorgErrorKind.UnsupportedPath, share.Error!.Kind);
    }

    [Theory]
    [InlineData(@"C:\", "/mnt/c")]
    [InlineData(@"d:\Backups\", "/mnt/d/Backups")]
    [InlineData(@"E:\a b\c", "/mnt/e/a b/c")]
    [InlineData(@"\\nas\share", null)]
    [InlineData("/already/unix", null)]
    public void ToWslPath(string windowsPath, string? expected)
    {
        Assert.Equal(expected, BorgClient.ToWslPath(windowsPath));
    }

    [Theory]
    [InlineData("PassphraseWrong", BorgErrorKind.PassphraseWrong)]
    [InlineData("Repository.DoesNotExist", BorgErrorKind.RepositoryNotFound)]
    [InlineData("Repository.InvalidRepository", BorgErrorKind.NotARepository)]
    [InlineData("Repository.AlreadyExists", BorgErrorKind.RepositoryExists)]
    [InlineData("Repository.PathAlreadyExists", BorgErrorKind.RepositoryExists)]
    [InlineData("LockTimeout", BorgErrorKind.Locked)]
    [InlineData("ConnectionClosedWithHint", BorgErrorKind.ConnectionFailed)]
    [InlineData("Something.Else", BorgErrorKind.Other)]
    public async Task Errors_are_classified_by_borgs_message_id(string msgid, BorgErrorKind expected)
    {
        var standardError = "Some plain text first\n" + ErrorLine(msgid, "details from borg") + "\n";
        var runner = new RecordingRunner(new ProcessResult(2, "", standardError));

        var result = await new BorgClient(runner).InfoAsync(NativeBorg, new RepositoryLocation("/backups/repo"), "x");

        Assert.Equal(new BorgError(expected, "details from borg"), result.Error);
    }

    [Fact]
    public async Task Warnings_count_as_success_and_plain_text_errors_are_kept()
    {
        var warning = await new BorgClient(new RecordingRunner(new ProcessResult(1, InfoJson, "warning"))).InfoAsync(
            NativeBorg, new RepositoryLocation("/r"), "x");
        Assert.True(warning.Succeeded);

        var crash = await new BorgClient(new RecordingRunner(new ProcessResult(2, "", "Traceback: boom\n"))).InfoAsync(
            NativeBorg, new RepositoryLocation("/r"), "x");
        Assert.Equal(new BorgError(BorgErrorKind.Other, "Traceback: boom"), crash.Error);
    }

    [Fact]
    public async Task Missing_borg_and_timeouts_are_reported()
    {
        var missing = await new BorgClient(new RecordingRunner(null)).InfoAsync(NativeBorg, new RepositoryLocation("/r"), "x");
        Assert.Equal(BorgErrorKind.NotStartable, missing.Error!.Kind);

        var noWsl = await new BorgClient(new RecordingRunner(null)).InfoAsync(WslBorg, new RepositoryLocation(@"C:\r"), "x");
        Assert.Equal(BorgErrorKind.NotStartable, noWsl.Error!.Kind);
    }
}
