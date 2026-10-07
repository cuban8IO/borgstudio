using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Ssh;
using BorgStudio.Plugins;
using BorgStudio.Providers.Hetzner;
using BorgStudio.Providers.Local;
using BorgStudio.Providers.Ssh;

namespace BorgStudio.Tests.TestSupport;

/// <summary>Plays borg: answers by sub command and records every call.</summary>
public sealed class FakeBorg : IProcessRunner
{
    public const string BorgPath = "/usr/bin/borg";

    public static readonly BorgInstallation Installation = new(BorgPath, new BorgVersion(1, 4, 1), BorgRuntime.Native);

    public List<(IReadOnlyList<string> Arguments, string? Passphrase)> Calls { get; } = [];

    public List<IReadOnlyDictionary<string, string?>> Environments { get; } = [];

    /// <summary>Encryption mode reported by "info"; null makes "info" fail with <see cref="InfoError"/>.</summary>
    public string? EncryptionMode { get; set; } = "repokey-blake2";

    public string InfoError { get; set; } = "Repository.DoesNotExist";

    /// <summary>The passphrase "info" accepts (for encrypted repositories).</summary>
    public string? CorrectPassphrase { get; set; }

    public IEnumerable<string> Commands => Calls.Select(call => call.Arguments[0]);

    public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        var passphrase = environment?.GetValueOrDefault("BORG_PASSPHRASE");
        Environments.Add(environment ?? new Dictionary<string, string?>());
        Calls.Add((arguments, passphrase));

        if (fileName == BorgPath && arguments is ["--version"])
            return Result(0, "borg 1.4.1\n");

        return arguments[0] switch
        {
            "init" => Result(0, ""),
            "key" => Result(0, ""),
            "info" when EncryptionMode is null => Error(InfoError),
            "info" when EncryptionMode != "none" && CorrectPassphrase is not null && passphrase != CorrectPassphrase
                => Error("PassphraseWrong"),
            "info" => Result(0, $$$"""{"encryption": {"mode": "{{{EncryptionMode}}}"}, "repository": {"id": "repo-id-1"}}"""),
            _ => Result(2, ""),
        };
    }

    public BorgDetector Detector() =>
        new(this, new BorgSearchEnvironment(HostPlatform.Linux, "/usr/bin", "/home/me", null, path => path == BorgPath));

    private static Task<ProcessResult?> Result(int exitCode, string output) =>
        Task.FromResult<ProcessResult?>(new ProcessResult(exitCode, output, ""));

    private static Task<ProcessResult?> Error(string msgid) =>
        Task.FromResult<ProcessResult?>(new ProcessResult(2, "",
            $$"""{"type": "log_message", "levelname": "ERROR", "message": "borg says no", "msgid": "{{msgid}}"}"""));
}

/// <summary>Plays an SSH server: presents a host key and records password logins.</summary>
public sealed class FakeSsh : ISshService
{
    public static readonly SshHostKey ServerKey = new(HostKeyBlob("ssh-ed25519", 1));

    public SshHostKey PresentedKey { get; set; } = ServerKey;

    public SshOperationException? LoginFailure { get; set; }

    /// <summary>Commands run after a password login.</summary>
    public List<(SshEndpoint Endpoint, string Password, string Command, string? StandardInput)> Logins { get; } = [];

    /// <summary>Lines appended to authorized_keys over SFTP after a password login.</summary>
    public List<(SshEndpoint Endpoint, string Password, string Line)> SftpAppends { get; } = [];

    public int HostKeyRequests { get; private set; }

    public Task<SshHostKey> GetHostKeyAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        HostKeyRequests++;
        return Task.FromResult(PresentedKey);
    }

    public Task RunWithPasswordAsync(SshEndpoint endpoint, string password, SshHostKey trustedHostKey, string command,
        string? standardInput = null, CancellationToken cancellationToken = default)
    {
        if (LoginFailure is not null)
            throw LoginFailure;
        Logins.Add((endpoint, password, command, standardInput));
        return Task.CompletedTask;
    }

    public Task AppendAuthorizedKeyAsync(SshEndpoint endpoint, string password, SshHostKey trustedHostKey, string line,
        CancellationToken cancellationToken = default)
    {
        if (LoginFailure is not null)
            throw LoginFailure;
        SftpAppends.Add((endpoint, password, line));
        return Task.CompletedTask;
    }

    /// <summary>A syntactically valid host key blob ("type" + 32 bytes of key material).</summary>
    public static byte[] HostKeyBlob(string type, byte fill)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        return [0, 0, 0, (byte)typeBytes.Length, .. typeBytes, 0, 0, 0, 32, .. Enumerable.Repeat(fill, 32)];
    }
}

/// <summary>All services of the app wired to fakes, in a temporary directory.</summary>
public sealed class TestServices : IDisposable
{
    public TemporaryDirectory Directory { get; } = new();
    public FakeBorg Borg { get; } = new();
    public FakeSecretStore Secrets { get; } = new();
    public FakeSsh Ssh { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public RepositoryStore Store { get; }
    public SshKeyStore SshKeys { get; }
    public AppServices Services { get; }

    public TestServices(bool keychainAvailable = true)
    {
        if (!keychainAvailable)
            Secrets = new FakeSecretStore(available: false);
        Store = new RepositoryStore(Directory.Combine("repositories.json"));
        SshKeys = new SshKeyStore(Directory.Combine("ssh"));
        var plugins = PluginCatalog.Load([new LocalProviderPlugin(), new SshProviderPlugin(), new HetznerProviderPlugin()], "no-plugins-here");
        Services = new AppServices(Borg.Detector(), plugins, Store, Secrets, new BorgClient(Borg), Ssh, SshKeys, Dialogs);
    }

    public KnownHostsFile KnownHosts => new(SshKeys.KnownHostsFile);

    public void Dispose() => Directory.Dispose();
}

public sealed class FakeSecretStore(bool available = true) : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = [];

    public bool IsAvailable => available;

    public string? Get(string key) => Secrets.GetValueOrDefault(key);

    public void Set(string key, string label, string secret) => Secrets[key] = secret;

    public void Delete(string key) => Secrets.Remove(key);
}

/// <summary>Answers dialogs as configured; the editor is "filled in" by <see cref="Editor"/>.</summary>
public sealed class FakeDialogs : IDialogService
{
    public Func<RepositoryEditorViewModel, Task>? Editor { get; set; }

    public Queue<bool> ConfirmAnswers { get; } = [];

    public Queue<string?> Passphrases { get; } = [];

    public string? SaveFile { get; set; }

    /// <summary>Titles of the dialogs shown.</summary>
    public List<string> Shown { get; } = [];

    /// <summary>Messages of the dialogs shown.</summary>
    public List<string> Messages { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null)
    {
        Shown.Add(title);
        Messages.Add(message);
        return Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Shown.Add(title);
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<string?> AskPassphraseAsync(string repositoryName) =>
        Task.FromResult(Passphrases.Count > 0 ? Passphrases.Dequeue() : null);

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName) => Task.FromResult(SaveFile);

    public Task ShowRepositoryEditorAsync(RepositoryEditorViewModel editor) => Editor?.Invoke(editor) ?? Task.CompletedTask;
}
