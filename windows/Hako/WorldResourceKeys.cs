using System.Globalization;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako;

internal static class WorldResourceKeys
{
    public static string Failure(InstanceWorldFailureReason reason) => reason switch
    {
        InstanceWorldFailureReason.BuildUnavailable => "WorldFailureBuildUnavailable",
        InstanceWorldFailureReason.BuildNotReady => "WorldFailureBuildNotReady",
        InstanceWorldFailureReason.BuildInstalling => "WorldFailureBuildInstalling",
        InstanceWorldFailureReason.BuildRunning => "WorldFailureBuildRunning",
        InstanceWorldFailureReason.BuildFilesBusy => "WorldFailureBuildFilesBusy",
        InstanceWorldFailureReason.InvalidWorldId => "WorldFailureInvalidId",
        InstanceWorldFailureReason.WorldMissing => "WorldFailureMissing",
        InstanceWorldFailureReason.ReparsePointNotAllowed => "WorldFailureReparsePoint",
        InstanceWorldFailureReason.NoAvailableName => "WorldFailureNoAvailableName",
        InstanceWorldFailureReason.MetadataRenameFailed => "WorldFailureMetadataRename",
        InstanceWorldFailureReason.CopyFailed => "WorldFailureCopy",
        InstanceWorldFailureReason.RecycleBinFailed => "WorldFailureRecycleBin",
        InstanceWorldFailureReason.NotRecyclable => "WorldFailureNotRecyclable",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    public static string MetadataError(WorldMetadataError error) => error switch
    {
        WorldMetadataError.InvalidGzip => "WorldMetadataInvalidGzip",
        WorldMetadataError.InvalidNbt => "WorldMetadataInvalidNbt",
        WorldMetadataError.InvalidText => "WorldMetadataInvalidText",
        WorldMetadataError.CompressedDataTooLarge => "WorldMetadataCompressedTooLarge",
        WorldMetadataError.DecompressedDataTooLarge => "WorldMetadataDecompressedTooLarge",
        WorldMetadataError.NestingTooDeep => "WorldMetadataTooDeep",
        WorldMetadataError.ReadFailure => "WorldMetadataReadFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(error))
    };

    public static string GameType(int? gameType) => gameType switch
    {
        0 => "WorldGameTypeSurvival",
        1 => "WorldGameTypeCreative",
        2 => "WorldGameTypeAdventure",
        3 => "WorldGameTypeSpectator",
        _ => "WorldGameTypeUnknown"
    };

    public static string FormatMessage(string key, Func<string, string> getString, params string[] arguments)
    {
        var template = getString(key);
        if (arguments.Length == 0) return template;
        try { return string.Format(CultureInfo.CurrentCulture, template, arguments); }
        catch (FormatException) { return template; }
    }

    public static string FormatSize(long? bytes, CultureInfo culture, Func<string, string> getString)
    {
        if (bytes is not >= 0) return getString("WorldSizeUnknown");
        var value = (double)bytes.Value;
        var unit = 0;
        string[] unitKeys = ["WorldSizeBytes", "WorldSizeKilobytes", "WorldSizeMegabytes", "WorldSizeGigabytes", "WorldSizeTerabytes"];
        while (value >= 1024 && unit < unitKeys.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var number = unit == 0 ? value.ToString("N0", culture) : value.ToString("N1", culture);
        return FormatMessage("WorldSizeFormat", getString, number, getString(unitKeys[unit]));
    }

    public static string FormatLastPlayed(DateTimeOffset? lastPlayed, CultureInfo culture, Func<string, string> getString) =>
        lastPlayed is { } value
            ? FormatMessage("WorldLastPlayedFormat", getString, value.ToLocalTime().ToString("g", culture))
            : getString("WorldLastPlayedUnknown");
}
