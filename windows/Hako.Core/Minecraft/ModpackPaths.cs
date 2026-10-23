using System.Text;

namespace Hako.Core.Minecraft;

public static class ModpackPaths
{
    private static readonly HashSet<string> ReservedDevices = CreateReservedDevices();
    private static readonly HashSet<string> ReservedGameRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "java", "libraries", "minecraft", "natives", "resources", "versions"
    };
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com",
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com"
    };

    public static void Validate(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 32_760
            || path.StartsWith('/') || path.StartsWith('\\')
            || Path.IsPathRooted(path)
            || path.Contains('\\') || path.Contains(':')
            || path.Any(character => char.IsControl(character) || "<>\"|?*[]".Contains(character)))
        {
            throw new ModpackException(ModpackError.UnsafePath);
        }

        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."
                || segment.Length > 255
                || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.StartsWith(".hako", StringComparison.OrdinalIgnoreCase)
                || ReservedDevices.Contains(DeviceBaseName(segment))))
        {
            throw new ModpackException(ModpackError.UnsafePath);
        }

        if (ReservedGameRoots.Contains(segments[0]))
        {
            throw new ModpackException(ModpackError.UnsafePath);
        }
    }

    public static bool IsAllowedDownload(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Port == 443
        && AllowedDownloadHosts.Contains(uri.IdnHost);

    internal static string Key(string path) => path.Normalize(NormalizationForm.FormC);

    internal static string? OverrideDestination(string entryPath, bool directory)
    {
        var layers = new[] { "overrides/", "client-overrides/", "server-overrides/" };
        var layer = layers.FirstOrDefault(entryPath.StartsWith);
        if (layer is null)
        {
            return null;
        }

        var destination = entryPath[layer.Length..];
        if (destination.Length == 0 && directory)
        {
            return null;
        }

        Validate(destination);
        return destination;
    }

    private static string DeviceBaseName(string segment)
    {
        var dot = segment.IndexOf('.');
        return (dot < 0 ? segment : segment[..dot]).ToUpperInvariant();
    }

    private static HashSet<string> CreateReservedDevices()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        foreach (var number in Enumerable.Range(1, 9)) values.Add($"COM{number}");
        foreach (var number in Enumerable.Range(1, 9)) values.Add($"LPT{number}");
        foreach (var number in new[] { "¹", "²", "³" })
        {
            values.Add("COM" + number);
            values.Add("LPT" + number);
        }
        return values;
    }
}

internal enum ModpackPathKind
{
    File,
    Directory
}

internal sealed class ModpackPathRegistry
{
    private sealed record Entry(string Path, ModpackPathKind Kind, bool ExplicitDirectory = false);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void AddFile(string path, bool replacing = false)
    {
        ModpackPaths.Validate(path);
        var parts = path.Split('/');
        AddParents(parts);
        var key = ModpackPaths.Key(path);
        if (_entries.TryGetValue(key, out var previous))
        {
            if (previous.Kind == ModpackPathKind.Directory
                || !replacing
                || !string.Equals(previous.Path, path, StringComparison.Ordinal))
            {
                throw new ModpackException(ModpackError.UnsafePath);
            }

            _entries[key] = previous;
            return;
        }

        _entries.Add(key, new Entry(path, ModpackPathKind.File));
    }

    public void AddDirectory(string path, bool explicitEntry = false)
    {
        ModpackPaths.Validate(path);
        var parts = path.Split('/');
        AddParents(parts);
        var key = ModpackPaths.Key(path);
        if (_entries.TryGetValue(key, out var previous))
        {
            if (previous.Kind != ModpackPathKind.Directory
                || !string.Equals(previous.Path, path, StringComparison.Ordinal)
                || explicitEntry && previous.ExplicitDirectory)
            {
                throw new ModpackException(ModpackError.UnsafePath);
            }

            if (explicitEntry) _entries[key] = previous with { ExplicitDirectory = true };
            return;
        }

        _entries.Add(key, new Entry(path, ModpackPathKind.Directory, explicitEntry));
    }

    private void AddParents(string[] parts)
    {
        for (var index = 1; index < parts.Length; index++)
        {
            var parent = string.Join('/', parts.Take(index));
            var key = ModpackPaths.Key(parent);
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.Kind != ModpackPathKind.Directory
                    || !string.Equals(existing.Path, parent, StringComparison.Ordinal))
                {
                    throw new ModpackException(ModpackError.UnsafePath);
                }
            }
            else
            {
                _entries.Add(key, new Entry(parent, ModpackPathKind.Directory));
            }
        }
    }
}
