namespace Hako.Core.Instances;

public enum InstanceStorageUsageCategory
{
    Worlds,
    Minecraft,
    Screenshots,
    Java,
    Mods,
    Datapacks,
    ResourcePacks,
    BuildBackups,
    WorldBackups
}

public sealed class InstanceStorageUsage
{
    private readonly long[] _bytes = new long[9];

    public long Total
    {
        get
        {
            long total = 0;
            foreach (var bytes in _bytes)
            {
                total = SaturatingAdd(total, bytes);
            }

            return total;
        }
    }

    public long this[InstanceStorageUsageCategory category] => _bytes[(int)category];

    public static InstanceStorageUsageCategory Classify(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return InstanceStorageUsageCategory.Minecraft;
        if (EqualsSegment(segments[0], "backups")) return InstanceStorageUsageCategory.BuildBackups;
        if (EqualsSegment(segments[0], "worlds")) return InstanceStorageUsageCategory.WorldBackups;
        if (segments.Count < 2) return InstanceStorageUsageCategory.Minecraft;
        if (EqualsSegment(segments[1], "java")) return InstanceStorageUsageCategory.Java;
        if (!EqualsSegment(segments[1], "minecraft")) return InstanceStorageUsageCategory.Minecraft;
        if (segments.Count < 3) return InstanceStorageUsageCategory.Minecraft;

        if (EqualsSegment(segments[2], "mods")) return InstanceStorageUsageCategory.Mods;
        if (IsResourcePackSegment(segments[2])) return InstanceStorageUsageCategory.ResourcePacks;
        if (EqualsSegment(segments[2], "screenshots")) return InstanceStorageUsageCategory.Screenshots;
        if (EqualsSegment(segments[2], "saves"))
        {
            if (segments.Count > 4 && IsDatapackSegment(segments[4])) return InstanceStorageUsageCategory.Datapacks;
            return InstanceStorageUsageCategory.Worlds;
        }

        return InstanceStorageUsageCategory.Minecraft;
    }

    internal void Add(InstanceStorageUsageCategory category, long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        var index = (int)category;
        _bytes[index] = SaturatingAdd(_bytes[index], bytes);
    }

    private static bool EqualsSegment(string segment, string expected) =>
        string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsResourcePackSegment(string segment) =>
        EqualsSegment(segment, "resourcepacks")
        || EqualsSegment(segment, "texturepacks")
        || EqualsSegment(segment, ".hako-disabled-resourcepacks")
        || EqualsSegment(segment, ".hako-disabled-texturepacks");

    private static bool IsDatapackSegment(string segment) =>
        EqualsSegment(segment, "datapacks") || EqualsSegment(segment, ".hako-disabled-datapacks");

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;
}

public sealed record DiskSpaceInfo(string? VolumeName, long Total, long Free);
