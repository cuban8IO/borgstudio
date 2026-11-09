namespace BorgStudio.Core.Borg;

public enum BorgSupport
{
    Supported,

    /// <summary>Older than <see cref="BorgCompatibility.MinimumVersion"/>.</summary>
    TooOld,

    /// <summary>borg 2.x: still in beta and its CLI differs from 1.x; not supported yet.</summary>
    NotYetSupported,
}

/// <summary>Which borg versions BorgStudio works with: 1.x from 1.2 on.</summary>
public static class BorgCompatibility
{
    public static BorgVersion MinimumVersion { get; } = new(1, 2, 0);

    public static BorgSupport Evaluate(BorgVersion version)
    {
        if (version.IsBorg2)
            return BorgSupport.NotYetSupported;

        var tooOld = version.Major < MinimumVersion.Major
            || (version.Major == MinimumVersion.Major && version.Minor < MinimumVersion.Minor);
        return tooOld ? BorgSupport.TooOld : BorgSupport.Supported;
    }
}
