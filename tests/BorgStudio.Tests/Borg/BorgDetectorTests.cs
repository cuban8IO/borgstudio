using BorgStudio.Core.Borg;
using BorgStudio.Core.Processes;

namespace BorgStudio.Tests.Borg;

public class BorgDetectorTests
{
    private const string Wsl = @"C:\Windows\system32\wsl.exe";
    private const string WslCall = Wsl + " -e sh -lc command -v borg && borg --version";

    /// <summary>Answers calls from a table ("file arg1 arg2" -> result); unknown programs cannot be started.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Dictionary<string, Func<ProcessResult?>> Programs { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<ProcessResult?> RunAsync(
            string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null,
            CancellationToken cancellationToken = default)
        {
            var call = string.Join(' ', [fileName, .. arguments]);
            Calls.Add(call);
            return Task.FromResult(Programs.TryGetValue(call, out var program) ? program() : null);
        }
    }

    private static ProcessResult Output(string standardOutput, int exitCode = 0) => new(exitCode, standardOutput, "");

    private static BorgSearchEnvironment Linux(params string[] existingFiles) =>
        new(HostPlatform.Linux, "/usr/local/bin:/usr/bin", "/home/me", null, existingFiles.Contains);

    private static BorgSearchEnvironment Windows(params string[] existingFiles) =>
        new(HostPlatform.Windows, @"C:\Tools", @"C:\Users\me", @"C:\Windows\system32", existingFiles.Contains);

    [Fact]
    public async Task Finds_native_borg()
    {
        var runner = new FakeProcessRunner();
        runner.Programs["/usr/bin/borg --version"] = () => Output("borg 1.4.5\n");

        var installation = await new BorgDetector(runner, Linux("/usr/bin/borg")).DetectAsync();

        Assert.Equal(new BorgInstallation("/usr/bin/borg", new BorgVersion(1, 4, 5), BorgRuntime.Native), installation);
        Assert.Equal(BorgSupport.Supported, installation!.Support);
    }

    [Fact]
    public async Task Skips_broken_and_hanging_executables()
    {
        var runner = new FakeProcessRunner();
        runner.Programs["/usr/local/bin/borg --version"] = () => Output("Traceback ...", exitCode: 1);
        runner.Programs["/usr/bin/borg --version"] = () => throw new TimeoutException();
        runner.Programs["/home/me/.local/bin/borg --version"] = () => Output("borg 1.2.8\n");

        var installation = await new BorgDetector(runner,
            Linux("/usr/local/bin/borg", "/usr/bin/borg", "/home/me/.local/bin/borg")).DetectAsync();

        Assert.Equal("/home/me/.local/bin/borg", installation?.Path);
    }

    [Fact]
    public async Task Returns_null_when_nothing_is_installed_and_never_tries_WSL_outside_Windows()
    {
        var runner = new FakeProcessRunner();

        Assert.Null(await new BorgDetector(runner, Linux(Wsl)).DetectAsync());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Prefers_native_borg_on_Windows()
    {
        var runner = new FakeProcessRunner();
        runner.Programs[@"C:\Tools\borg.exe --version"] = () => Output("borg 2.0.0b14\r\n");

        var installation = await new BorgDetector(runner, Windows(@"C:\Tools\borg.exe", Wsl)).DetectAsync();

        Assert.Equal(BorgRuntime.Native, installation?.Runtime);
        Assert.Equal(BorgSupport.NotYetSupported, installation!.Support);
        Assert.DoesNotContain(runner.Calls, call => call.StartsWith(Wsl, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Falls_back_to_borg_in_WSL()
    {
        var runner = new FakeProcessRunner();
        runner.Programs[WslCall] = () => Output("/usr/bin/borg\nborg 1.4.5\n");

        var installation = await new BorgDetector(runner, Windows(Wsl)).DetectAsync();

        Assert.Equal(new BorgInstallation("/usr/bin/borg", new BorgVersion(1, 4, 5), BorgRuntime.Wsl), installation);
    }

    [Theory]
    [InlineData("", 1)] // borg not installed inside WSL
    [InlineData("Es wurde keine Verteilung gefunden.", 1)] // WSL without any distribution
    [InlineData("/usr/bin/borg\n", 0)] // no version line
    public async Task WSL_without_working_borg_means_not_found(string output, int exitCode)
    {
        var runner = new FakeProcessRunner();
        runner.Programs[WslCall] = () => Output(output, exitCode);

        Assert.Null(await new BorgDetector(runner, Windows(Wsl)).DetectAsync());
    }
}
