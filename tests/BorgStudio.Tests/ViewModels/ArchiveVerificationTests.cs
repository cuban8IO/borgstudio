using BorgStudio.App.ViewModels;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Checks;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The marks on archives: covered by the last check, and restored on trial.</summary>
public sealed class ArchiveVerificationTests
{
    private static readonly DateTimeOffset CheckedAt = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);

    private static BorgArchive Archive(string name, int day) => new(name, "id-" + name, new DateTimeOffset(2026, 11, day, 20, 0, 0, TimeSpan.Zero));

    private static CheckRecord Check(CheckOutcome outcome, CheckDepth depth = CheckDepth.Normal) =>
        new(CheckedAt, TimeSpan.FromMinutes(5), depth, outcome);

    private static readonly IReadOnlyDictionary<string, RestoreTestRecord> NoTests = new Dictionary<string, RestoreTestRecord>();

    [Fact]
    public void Archives_there_at_the_last_good_check_are_marked_checked()
    {
        Assert.Equal(ArchiveCheckState.Checked, ArchiveVerification.For(Archive("a", 19), Check(CheckOutcome.Ok), NoTests).Check);
        Assert.Equal(ArchiveCheckState.CheckedThoroughly, ArchiveVerification.For(Archive("a", 19), Check(CheckOutcome.Ok, CheckDepth.Full), NoTests).Check);
        // Made after the check: not looked at.
        Assert.Equal(ArchiveCheckState.None, ArchiveVerification.For(Archive("b", 21), Check(CheckOutcome.Ok), NoTests).Check);
        // Damage found: every archive is suspect.
        Assert.Equal(ArchiveCheckState.Damaged, ArchiveVerification.For(Archive("b", 21), Check(CheckOutcome.ProblemsFound), NoTests).Check);
        Assert.Equal(ArchiveCheckState.None, ArchiveVerification.For(Archive("a", 19), null, NoTests).Check);
    }

    [Fact]
    public void A_judged_restore_test_of_the_archive_is_shown()
    {
        var tests = new Dictionary<string, RestoreTestRecord>
        {
            ["a"] = new(CheckedAt, TimeSpan.FromMinutes(1), RestoreTestMode.Read, RestoreTestOutcome.Ok) { Archive = "a" },
            ["b"] = new(CheckedAt, TimeSpan.FromMinutes(1), RestoreTestMode.Read, RestoreTestOutcome.NotRun) { Archive = "b" },
        };

        var tested = new ArchiveItemViewModel(Archive("a", 19), "Docs") { Verification = ArchiveVerification.For(Archive("a", 19), null, tests) };
        var notRun = new ArchiveItemViewModel(Archive("b", 19), "Docs") { Verification = ArchiveVerification.For(Archive("b", 19), null, tests) };

        Assert.True(tested.IsRestoreTested);
        Assert.False(tested.IsChecked);
        Assert.False(notRun.IsRestoreTested);
        Assert.False(notRun.IsRestoreTestFailed);
    }
}
