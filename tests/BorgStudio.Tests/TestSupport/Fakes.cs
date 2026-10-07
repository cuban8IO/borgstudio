using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Secrets;

namespace BorgStudio.Tests.TestSupport;

/// <summary>Plays borg: answers by sub command and records every call.</summary>
public sealed class FakeBorg : IProcessRunner
{
    public const string BorgPath = "/usr/bin/borg";

    public static readonly BorgInstallation Installation = new(BorgPath, new BorgVersion(1, 4, 1), BorgRuntime.Native);

    public List<(IReadOnlyList<string> Arguments, string? Passphrase)> Calls { get; } = [];

    /// <summary>Encryption mode reported by "info"; null makes "info" fail with <see cref="InfoError"/>.</summary>
    public string? EncryptionMode { get; set; } = "repokey-blake2";

    public string InfoError { get; set; } = "Repository.DoesNotExist";

    /// <summary>The passphrase "info" accepts (for encrypted repositories).</summary>
    public string? CorrectPassphrase { get; set; }

    public IEnumerable<string> Commands => Calls.Select(call => call.Arguments[0]);

    public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null, CancellationToken cancellationToken = default)
    {
        var passphrase = environment?.GetValueOrDefault("BORG_PASSPHRASE");
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

    public List<string> Shown { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null)
    {
        Shown.Add(title);
        return Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Shown.Add(title);
        return Task.CompletedTask;
    }

    public Task<string?> AskPassphraseAsync(string repositoryName) =>
        Task.FromResult(Passphrases.Count > 0 ? Passphrases.Dequeue() : null);

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName) => Task.FromResult(SaveFile);

    public Task ShowRepositoryEditorAsync(RepositoryEditorViewModel editor) => Editor?.Invoke(editor) ?? Task.CompletedTask;
}
