using System.Diagnostics;
using BorgStudio.Core.Processes;

namespace BorgStudio.Tests.Processes;

// Runs real processes: dotnet is available wherever the tests run.
public class ProcessRunnerTests
{
    [Fact]
    public async Task Captures_output_and_exit_code()
    {
        var result = await new ProcessRunner().RunAsync("dotnet", ["--version"], TimeSpan.FromSeconds(30));

        Assert.NotNull(result);
        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", result.StandardOutput);
    }

    [Fact]
    public async Task Returns_null_for_a_program_that_does_not_exist()
    {
        Assert.Null(await new ProcessRunner().RunAsync("borgstudio-no-such-program", [], TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Programs_reading_input_get_end_of_input_instead_of_waiting()
    {
        // Regression: wsl.exe started from the GUI waited for input until the timeout.
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("findstr", new[] { "x" })
            : ("cat", Array.Empty<string>());

        var result = await new ProcessRunner().RunAsync(program, arguments, TimeSpan.FromSeconds(10));

        Assert.NotNull(result);
    }

    [Fact]
    public async Task Kills_a_program_that_runs_too_long()
    {
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("ping", new[] { "-n", "30", "127.0.0.1" })
            : ("sleep", new[] { "30" });

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ProcessRunner().RunAsync(program, arguments, TimeSpan.FromMilliseconds(500)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }
}
