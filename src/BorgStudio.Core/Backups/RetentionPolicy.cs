using System.Globalization;

namespace BorgStudio.Core.Backups;

/// <summary>
/// Which archives of a job <c>borg prune</c> keeps; all others are deleted. Each rule keeps the newest archive of that
/// many hours/days/weeks/…; 0 means the rule is not used.
/// </summary>
public sealed record RetentionPolicy
{
    /// <summary>Keeps every archive of the last N days (<c>--keep-within Nd</c>).</summary>
    public int WithinDays { get; init; }

    /// <summary>Keeps the newest N archives (<c>--keep-last</c>).</summary>
    public int Last { get; init; }

    public int Hourly { get; init; }

    public int Daily { get; init; }

    public int Weekly { get; init; }

    public int Monthly { get; init; }

    public int Yearly { get; init; }

    /// <summary>The rule filled in when clean-up is switched on: 7 daily, 4 weekly, 6 monthly.</summary>
    public static RetentionPolicy Suggested { get; } = new() { Daily = 7, Weekly = 4, Monthly = 6 };

    /// <summary>borg refuses to prune without at least one rule (it would delete everything).</summary>
    public bool HasRules => WithinDays > 0 || Last > 0 || Hourly > 0 || Daily > 0 || Weekly > 0 || Monthly > 0 || Yearly > 0;

    /// <summary>The <c>--keep-…</c> options for borg prune.</summary>
    public IReadOnlyList<string> Arguments()
    {
        var arguments = new List<string>();
        void Add(string option, int value, string suffix = "")
        {
            if (value > 0)
                arguments.AddRange([option, value.ToString(CultureInfo.InvariantCulture) + suffix]);
        }

        Add("--keep-within", WithinDays, "d");
        Add("--keep-last", Last);
        Add("--keep-hourly", Hourly);
        Add("--keep-daily", Daily);
        Add("--keep-weekly", Weekly);
        Add("--keep-monthly", Monthly);
        Add("--keep-yearly", Yearly);
        return arguments;
    }
}
