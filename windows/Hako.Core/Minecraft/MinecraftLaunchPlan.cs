using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hako.Core.Instances;

namespace Hako.Core.Minecraft;

public sealed record MinecraftLaunchIdentity(
    string Name,
    string Uuid,
    string AccessToken,
    string Xuid = "",
    string UserType = "msa")
{
    public static MinecraftLaunchIdentity Offline(string name)
    {
        if (!OfflineUsername.IsValid(name))
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.InvalidOfflineName);
        }

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new MinecraftLaunchIdentity(name, Convert.ToHexString(bytes).ToLowerInvariant(), "0", UserType: "legacy");
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Name = ").Append(Name)
            .Append(", Uuid = ").Append(Uuid)
            .Append(", Xuid = ").Append(Xuid)
            .Append(", UserType = ").Append(UserType);
        return true;
    }
}

public sealed record MinecraftLaunchProfile(
    string? MainClass = null,
    IReadOnlyList<LibraryInstallation>? Libraries = null,
    IReadOnlyList<MojangArgument>? JvmArguments = null,
    IReadOnlyList<MojangArgument>? GameArguments = null);

public sealed class MinecraftLaunchPlan
{
    private static readonly HashSet<string> ReservedGameArguments = new(StringComparer.Ordinal)
    {
        "--username", "--uuid", "--accessToken", "--session", "--clientId", "--xuid", "--userType",
        "--userProperties", "--profileProperties", "--version", "--versionType", "--gameDir", "--assetsDir",
        "--assetIndex", "--width", "--height", "--fullscreen"
    };

    internal MinecraftLaunchPlan(string executable, IReadOnlyList<string> arguments, string workingDirectory, int javaMajorVersion)
    {
        Executable = executable;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        JavaMajorVersion = javaMajorVersion;
    }

    public string Executable { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string WorkingDirectory { get; }
    public int JavaMajorVersion { get; }

    public static MinecraftLaunchPlan Build(
        MojangVersionMetadata manifest,
        string instanceRoot,
        string executable,
        MinecraftLaunchIdentity identity,
        LaunchArgumentSource source,
        InstanceParameters parameters,
        GameArchitecture architecture,
        MojangAssetIndex? assetIndex = null,
        IReadOnlyList<LibraryInstallation>? libraries = null,
        int javaMajorVersion = 0,
        string clientId = MicrosoftDeviceAuth.ClientId,
        string launcherVersion = "1.0",
        MinecraftLaunchProfile? profile = null,
        JavaMemoryPolicy? memoryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        var mainClass = profile?.MainClass ?? manifest.MainClass;
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new MinecraftLaunchException(MinecraftLaunchError.InvalidManifest);
        }

        var gameDirectory = InstanceStorage.ContainedPath("minecraft", instanceRoot, allowBaseReparsePoint: true);
        var assetsDirectory = InstanceStorage.ContainedPath("minecraft/assets", instanceRoot, allowBaseReparsePoint: true);
        var indexId = manifest.AssetIndex?.Id ?? manifest.Assets ?? "legacy";
        var gameAssets = assetIndex?.Virtual == true
            ? InstanceStorage.ContainedPath($"minecraft/assets/virtual/{indexId}", instanceRoot, allowBaseReparsePoint: true)
            : assetIndex?.MapToResources == true
                ? InstanceStorage.ContainedPath("minecraft/resources", instanceRoot, allowBaseReparsePoint: true)
                : assetsDirectory;

        var features = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["has_custom_resolution"] = !parameters.Fullscreen,
            ["is_demo_user"] = false
        };
        var resolvedLibraries = libraries ?? MojangCompatibility.Libraries(manifest, architecture, features);
        resolvedLibraries = resolvedLibraries.Concat(profile?.Libraries ?? []).ToArray();
        var classpath = new List<string>(resolvedLibraries.Count + 1);
        foreach (var library in resolvedLibraries)
        {
            if (library.IsNative || library.ExtractNatives)
            {
                continue;
            }

            classpath.Add(InstanceStorage.ContainedPath("minecraft/libraries/" + library.Path, instanceRoot, allowBaseReparsePoint: true));
        }

        classpath.Add(InstanceStorage.ContainedPath(
            $"minecraft/versions/{manifest.Id}/{manifest.Id}.jar", instanceRoot, allowBaseReparsePoint: true));
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = identity.Name,
            ["auth_uuid"] = identity.Uuid,
            ["auth_access_token"] = identity.AccessToken,
            ["auth_session"] = identity.AccessToken == "0" ? "0" : $"token:{identity.AccessToken}:{identity.Uuid}",
            ["auth_xuid"] = identity.Xuid,
            ["user_type"] = identity.UserType,
            ["user_properties"] = "{}",
            ["profile_properties"] = "{}",
            ["clientid"] = clientId,
            ["version_name"] = manifest.Id,
            ["version_type"] = manifest.Type ?? "release",
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetsDirectory,
            ["assets_index_name"] = indexId,
            ["game_assets"] = gameAssets,
            ["natives_directory"] = InstanceStorage.ContainedPath("minecraft/natives", instanceRoot, allowBaseReparsePoint: true),
            ["library_directory"] = InstanceStorage.ContainedPath("minecraft/libraries", instanceRoot, allowBaseReparsePoint: true),
            ["classpath"] = string.Join(';', classpath),
            ["classpath_separator"] = ";",
            ["launcher_name"] = "Hako",
            ["launcher_version"] = launcherVersion,
            ["resolution_width"] = parameters.WindowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["resolution_height"] = parameters.WindowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        if (manifest.Logging?.GetValueOrDefault("client") is { Argument: not null } logging)
        {
            var fileName = logging.File.Id ?? Path.GetFileName(new Uri(logging.File.Url).AbsolutePath);
            substitutions["logging_configuration"] = InstanceStorage.ContainedPath(
                "minecraft/assets/log_configs/" + fileName, instanceRoot, allowBaseReparsePoint: true);
        }

        var (jvm, game) = ArgumentTemplates(manifest, source, parameters, architecture, memoryPolicy, profile);
        var allArguments = jvm.Concat([mainClass]).Concat(game)
            .Select(argument => Substitute(argument, substitutions)).ToArray();
        return new MinecraftLaunchPlan(executable, allArguments, gameDirectory,
            javaMajorVersion > 0 ? javaMajorVersion : manifest.Java.MajorVersion);
    }

    public static (IReadOnlyList<string> Java, IReadOnlyList<string> Minecraft) ArgumentTemplates(
        MojangVersionMetadata manifest,
        LaunchArgumentSource source,
        InstanceParameters parameters,
        GameArchitecture architecture,
        JavaMemoryPolicy? memoryPolicy = null,
        MinecraftLaunchProfile? profile = null)
    {
        var features = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["has_custom_resolution"] = !parameters.Fullscreen,
            ["is_demo_user"] = false
        };
        IReadOnlyList<string> Group(string name) => (manifest.Arguments?.GetValueOrDefault(name) ?? [])
            .Where(argument => argument.IsAllowed(architecture, features))
            .SelectMany(argument => argument.Values)
            .ToArray();

        var java = source == LaunchArgumentSource.Mojang
            ? Group("default-user-jvm").ToList()
            : LaunchArguments.Parse(parameters.JavaArguments).ToList();
        if (manifest.Arguments is not null)
        {
            java.AddRange(Group("jvm"));
        }
        else
        {
            java.Add("-Djava.library.path=${natives_directory}");
            java.Add("-cp");
            java.Add("${classpath}");
        }

        java.AddRange((profile?.JvmArguments ?? []).Where(argument => argument.IsAllowed(architecture, features))
            .SelectMany(argument => argument.Values));
        if (manifest.Logging?.GetValueOrDefault("client")?.Argument is { } loggingArgument)
        {
            java.Add(loggingArgument.Replace("${path}", "${logging_configuration}", StringComparison.Ordinal));
        }

        java = LaunchArguments.ApplyMemory(java, parameters.MaximumMemoryMiB, architecture, memoryPolicy).ToList();

        var game = manifest.Arguments is not null
            ? Group("game").ToList()
            : LaunchArguments.Parse(manifest.MinecraftArguments ?? "").ToList();
        game.AddRange((profile?.GameArguments ?? []).Where(argument => argument.IsAllowed(architecture, features))
            .SelectMany(argument => argument.Values));
        if (parameters.Fullscreen)
        {
            game.Add("--fullscreen");
        }
        else if (!game.Contains("--width", StringComparer.Ordinal))
        {
            game.Add("--width");
            game.Add(parameters.WindowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture));
            game.Add("--height");
            game.Add(parameters.WindowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var extra = source == LaunchArgumentSource.Mojang
            ? Array.Empty<string>()
            : LaunchArguments.Parse(parameters.MinecraftArguments).ToArray();
        foreach (var argument in extra)
        {
            var option = argument.Split('=', 2)[0];
            if (ReservedGameArguments.Contains(option))
            {
                throw new MinecraftLaunchException(MinecraftLaunchError.ReservedGameArgument, option);
            }
        }

        game.AddRange(extra);
        for (var index = 0; index < game.Count; index++)
        {
            game[index] = game[index]
                .Replace("${resolution_width}", parameters.WindowWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("${resolution_height}", parameters.WindowHeight.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        return (java, game);
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string> values) => Regex.Replace(
        text,
        "\\$\\{([^}]+)\\}",
        match => values.TryGetValue(match.Groups[1].Value, out var value)
            ? value
            : throw new MinecraftLaunchException(MinecraftLaunchError.InvalidManifest, match.Groups[1].Value));
}

public enum MinecraftLaunchError
{
    InvalidManifest,
    InvalidOfflineName,
    ReservedGameArgument,
    BuildNotReady,
    BuildAlreadyRunning,
    JavaMissing,
    MetadataInvalid,
    AssetIndexInvalid,
    ClientMissing,
    SessionChanged,
    ProcessStartFailed,
    CommandLineTooLong,
    ArgumentFileEncoding,
    ProcessRecordInvalid
}

public sealed class MinecraftLaunchException(MinecraftLaunchError error, string? detail = null, Exception? innerException = null)
    : Exception(detail ?? error.ToString(), innerException)
{
    public MinecraftLaunchError Error { get; } = error;
    public string ResourceKey => Error switch
    {
        MinecraftLaunchError.InvalidManifest => "LaunchInvalidManifest",
        MinecraftLaunchError.InvalidOfflineName => "LaunchInvalidOfflineName",
        MinecraftLaunchError.ReservedGameArgument => "LaunchReservedGameArgument",
        MinecraftLaunchError.BuildNotReady => "LaunchBuildNotReady",
        MinecraftLaunchError.BuildAlreadyRunning => "LaunchBuildAlreadyRunning",
        MinecraftLaunchError.JavaMissing => "LaunchJavaMissing",
        MinecraftLaunchError.MetadataInvalid => "LaunchMetadataInvalid",
        MinecraftLaunchError.AssetIndexInvalid => "LaunchAssetIndexInvalid",
        MinecraftLaunchError.ClientMissing => "LaunchClientMissing",
        MinecraftLaunchError.SessionChanged => "LaunchSessionChanged",
        MinecraftLaunchError.ProcessStartFailed => "LaunchProcessStartFailed",
        MinecraftLaunchError.CommandLineTooLong => "LaunchCommandLineTooLong",
        MinecraftLaunchError.ArgumentFileEncoding => "LaunchArgumentFileEncoding",
        MinecraftLaunchError.ProcessRecordInvalid => "LaunchProcessRecordInvalid",
        _ => throw new ArgumentOutOfRangeException(nameof(Error), Error, null)
    };
}
