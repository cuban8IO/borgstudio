using BorgStudio.Plugins;

namespace BorgStudio.Core.Repositories;

/// <summary>Works out which borg major version a repository is used with.</summary>
public static class RepositoryFormat
{
    /// <summary>
    /// borg 1 repositories have a "config" file with a [repository] section; borg 2 (borgstore) keeps its
    /// configuration in a "config" directory. <c>null</c> if the folder holds no repository (yet).
    /// </summary>
    public static int? DetectLocal(string folder)
    {
        var config = Path.Combine(folder, "config");
        if (Directory.Exists(config))
            return 2;
        if (!File.Exists(config))
            return null;

        try
        {
            return File.ReadLines(config).Take(20).Any(line => line.Trim() == "[repository]") ? 1 : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The borg major version to use: the user's choice, or for <see cref="BorgVersionPreference.Auto"/> the format of
    /// a local repository; borg 1 if nothing can be detected (new or remote repositories).
    /// </summary>
    public static int Resolve(BorgVersionPreference preference, RepositoryLocation location) => preference switch
    {
        BorgVersionPreference.Borg1 => 1,
        BorgVersionPreference.Borg2 => 2,
        _ when location.Url.Contains("://", StringComparison.Ordinal) => 1,
        _ => DetectLocal(location.Url) ?? 1,
    };
}
