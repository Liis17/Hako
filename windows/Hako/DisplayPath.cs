namespace Hako;

internal static class DisplayPath
{
    /// <summary>Shortens <paramref name="path"/> to <c>%USERPROFILE%\…</c> only when it lies inside the user profile.</summary>
    public static string Format(string path, string userProfile)
    {
        var profile = userProfile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (profile.Length == 0 || !path.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (path.Length == profile.Length)
        {
            return "%USERPROFILE%";
        }

        var separator = path[profile.Length];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar
            ? "%USERPROFILE%" + path[profile.Length..]
            : path;
    }

    public static string Format(string path) =>
        Format(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
