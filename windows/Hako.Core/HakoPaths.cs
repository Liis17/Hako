using System.Security.Cryptography;
using System.Text;

namespace Hako.Core;

public sealed class HakoPaths
{
    public HakoPaths(string gameRoot, string dataRoot, string cacheRoot, bool isolated = false)
    {
        GameRoot = Normalize(gameRoot, nameof(gameRoot));
        DataRoot = Normalize(dataRoot, nameof(dataRoot));
        CacheRoot = Normalize(cacheRoot, nameof(cacheRoot));
        if (ContainsPath(GameRoot, DataRoot) || ContainsPath(DataRoot, GameRoot))
        {
            throw new ArgumentException("The game and data roots cannot contain one another.");
        }

        Isolated = isolated;
    }

    public string GameRoot { get; }
    public string DataRoot { get; }
    public string CacheRoot { get; }
    public bool Isolated { get; }

    public static HakoPaths CreateProduction(string dataRoot)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new HakoPaths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hako"),
            dataRoot,
            Path.Combine(localAppData, "Hako", "Cache"));
    }

    public static HakoPaths CreateIsolated(string gameRoot, string dataRoot) =>
        new(gameRoot, dataRoot, Path.Combine(Normalize(dataRoot, nameof(dataRoot)), "Cache"), isolated: true);

    public static HakoPaths CreateDevelopment(string localAppDataRoot)
    {
        var localRoot = Normalize(localAppDataRoot, nameof(localAppDataRoot));
        var root = Path.Combine(localRoot, "Hako-dev");
        return new HakoPaths(
            Path.Combine(root, "Game"),
            Path.Combine(root, "Data"),
            Path.Combine(root, "Cache"),
            isolated: true);
    }

    public static HakoPaths CreatePreview(string temporaryRoot)
    {
        var root = Normalize(temporaryRoot, nameof(temporaryRoot));
        var gameRoot = Path.Combine(root, "Game");
        var dataRoot = Path.Combine(root, "Data");
        return new HakoPaths(gameRoot, dataRoot, Path.Combine(dataRoot, "Cache"), isolated: true);
    }

    public string GetInstanceKey()
    {
        if (!Isolated)
        {
            return "Hako.Main";
        }

        var identity = GameRoot.ToUpperInvariant() + "\0" + DataRoot.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return "Hako.Isolated." + hash;
    }

    public static HakoPathOverrides ParseOverrides(string[] args)
    {
        string? gameRoot = null;
        string? dataRoot = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is not ("--hako-root" or "--data-root"))
            {
                if (argument.StartsWith("--hako-root", StringComparison.Ordinal)
                    || argument.StartsWith("--data-root", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Use {argument.Split('=')[0]} followed by an absolute path.");
                }

                continue;
            }

            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException($"{argument} requires an absolute path.");
            }

            var path = Normalize(args[++index], argument);
            if (argument == "--hako-root")
            {
                if (gameRoot is not null)
                {
                    throw new ArgumentException("--hako-root may be specified only once.");
                }

                gameRoot = path;
            }
            else
            {
                if (dataRoot is not null)
                {
                    throw new ArgumentException("--data-root may be specified only once.");
                }

                dataRoot = path;
            }
        }

        if ((gameRoot is null) != (dataRoot is null))
        {
            throw new ArgumentException("Pass both --hako-root and --data-root to use isolated storage.");
        }

        if (gameRoot is not null && (ContainsPath(gameRoot, dataRoot!) || ContainsPath(dataRoot!, gameRoot)))
        {
            throw new ArgumentException("--hako-root and --data-root cannot contain one another.");
        }

        return new HakoPathOverrides(gameRoot, dataRoot);
    }

    private static string Normalize(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute path is required.", parameterName);
        }

        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            throw new ArgumentException("The path points to a file, not a directory.", parameterName);
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool ContainsPath(string parent, string child)
    {
        if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record HakoPathOverrides(string? GameRoot, string? DataRoot)
{
    public bool Isolated => GameRoot is not null;
}
