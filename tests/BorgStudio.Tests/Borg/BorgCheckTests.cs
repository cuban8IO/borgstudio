using BorgStudio.Core.Borg;
using BorgStudio.Core.Checks;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Borg;

/// <summary>borg check: arguments, progress phases, damage as a result and errors as failures – with fake borg.</summary>
public class BorgCheckTests
{
    private readonly FakeBorg _borg = new();

    private BorgClient Client => new(_borg);

    private static RepositoryLocation Location => new("/repo");

    private List<string> LastArguments => [.. _borg.Calls[^1].Arguments];

    [Fact]
    public async Task A_normal_check_reads_segments_and_archives()
    {
        var phases = new List<BorgCheckProgress>();

        var result = await Client.CheckAsync(FakeBorg.Installation, Location, CheckDepth.Normal, "pw",
            progress: new SynchronousProgress<BorgCheckProgress>(phases.Add));

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.ProblemsFound);
        Assert.Empty(result.Value.Messages);
        Assert.Equal(["check", "--log-json", "--progress", "/repo"], LastArguments);
        Assert.Equal("pw", _borg.Calls[^1].Passphrase);
        Assert.Equal([CheckPhase.Segments, CheckPhase.Archives], phases.Select(update => update.Phase).Distinct());
        Assert.Equal([0.5, 1, 0.5, 1], phases.Select(update => update.Fraction));
        Assert.False(_borg.Environments[^1].ContainsKey("BORG_CHECK_I_KNOW_WHAT_I_AM_DOING"));
    }

    [Fact]
    public async Task A_full_check_also_verifies_the_data()
    {
        var phases = new List<CheckPhase>();

        var result = await Client.CheckAsync(FakeBorg.Installation, Location, CheckDepth.Full, "pw",
            progress: new SynchronousProgress<BorgCheckProgress>(update => phases.Add(update.Phase)));

        Assert.True(result.Succeeded);
        Assert.Contains("--verify-data", LastArguments);
        Assert.Equal([CheckPhase.Segments, CheckPhase.Data, CheckPhase.Archives], phases.Distinct());
    }

    [Fact]
    public async Task Damage_is_a_result_with_borgs_messages()
    {
        _borg.CheckProblems.AddRange(["Data integrity error: Segment entry checksum mismatch [segment 2, offset 8]",
            "Finished full repository check, errors found."]);

        var result = await Client.CheckAsync(FakeBorg.Installation, Location, CheckDepth.Normal, "pw");

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.ProblemsFound);
        Assert.Equal(_borg.CheckProblems, result.Value.Messages);
    }

    [Fact]
    public async Task A_repair_answers_borgs_question_itself()
    {
        _borg.CheckProblems.Add("Index object count mismatch.");

        var result = await Client.CheckAsync(FakeBorg.Installation, Location, CheckDepth.Repair, "pw");

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Contains("--repair", LastArguments);
        Assert.Equal("YES", _borg.Environments[^1]["BORG_CHECK_I_KNOW_WHAT_I_AM_DOING"]);
        Assert.Empty(_borg.CheckProblems);
    }

    [Fact]
    public async Task A_check_that_cannot_run_fails()
    {
        _borg.CheckError = "LockTimeout";

        var result = await Client.CheckAsync(FakeBorg.Installation, Location, CheckDepth.Normal, "pw");

        Assert.Equal(BorgErrorKind.Locked, result.Error?.Kind);
    }

    [Fact]
    public async Task Many_messages_are_cut_off()
    {
        _borg.CheckProblems.AddRange(Enumerable.Range(1, BorgClient.MaximumCheckMessages + 50).Select(number => $"ID: {number} missing"));

        var result = await Client.CheckAsync(FakeBorg.Installation, Location, CheckDepth.Normal, "pw");

        Assert.True(result.Value!.ProblemsFound);
        Assert.Equal(BorgClient.MaximumCheckMessages, result.Value.Messages.Count);
    }

    [Theory]
    [InlineData("""{"type": "log_message", "message": "Index object count mismatch.", "levelname": "ERROR", "name": "borg.repository"}""",
        "Index object count mismatch.", true)]
    [InlineData("""{"type": "log_message", "message": "a: New missing file chunk detected", "levelname": "WARNING", "name": "borg.archive"}""",
        "a: New missing file chunk detected", false)]
    [InlineData("""{"type": "log_message", "message": "Starting repository check", "levelname": "INFO", "name": "borg.repository"}""", null, false)]
    [InlineData("""{"message": "Checking segments 10.0%", "msgid": "repository.check", "type": "progress_percent", "finished": false}""", null, false)]
    [InlineData("not json \"log_message\"", null, false)]
    public void Reads_errors_and_warnings_only(string line, string? text, bool isError)
    {
        var message = BorgClient.ParseCheckMessage(line);

        Assert.Equal(text, message?.Text);
        if (message is { } parsed)
            Assert.Equal(isError, parsed.IsError);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
