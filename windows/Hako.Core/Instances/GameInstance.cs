using System.Text.Json.Serialization;

namespace Hako.Core.Instances;

[JsonConverter(typeof(LaunchArgumentSourceJsonConverter))]
public enum LaunchArgumentSource
{
    Global,
    Mojang,
    Custom
}

[JsonConverter(typeof(InstanceInstallationStateJsonConverter))]
public enum InstanceInstallationState
{
    Queued,
    Installing,
    Paused,
    Ready,
    Failed
}

[JsonConverter(typeof(InstanceModLoaderJsonConverter))]
public enum InstanceModLoader
{
    Vanilla,
    Fabric
}

public sealed record InstanceParameters
{
    [JsonRequired]
    public string JavaArguments { get; init; } = "";
    [JsonRequired]
    public string MinecraftArguments { get; init; } = "";
    [JsonRequired]
    public bool Fullscreen { get; init; }
    [JsonRequired]
    public int WindowWidth { get; init; } = 1280;
    [JsonRequired]
    public int WindowHeight { get; init; } = 720;
    [JsonRequired]
    public int MaximumMemoryMiB { get; init; } = GameLaunchDefaults.Standard.MaximumMemoryMiB;
}

public sealed record FabricConfiguration([property: JsonRequired] string LoaderVersion, FabricApiDescriptor? Api = null);

public sealed record FabricApiDescriptor(
    [property: JsonRequired] string ProjectID,
    [property: JsonRequired] string VersionID,
    [property: JsonRequired] string Version,
    [property: JsonRequired] string Channel,
    [property: JsonRequired] string Filename,
    [property: JsonRequired] string Url,
    [property: JsonRequired] long Size,
    [property: JsonRequired] string Sha1,
    [property: JsonRequired] string Sha512);

public sealed record GameInstance
{
    [JsonRequired]
    public Guid Id { get; init; } = Guid.NewGuid();
    [JsonRequired]
    public string Name { get; init; } = "";
    [JsonRequired]
    public string FolderName { get; init; } = "";
    [JsonRequired]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonRequired]
    public string VersionID { get; init; } = "";
    [JsonRequired]
    public string MetadataURL { get; init; } = "";
    [JsonRequired]
    public string MetadataSHA1 { get; init; } = "";
    [JsonRequired]
    public string IconSymbol { get; init; } = "shippingbox.fill";
    public Guid IconRevision { get; init; } = Guid.NewGuid();
    [JsonRequired]
    public LaunchArgumentSource ArgumentSource { get; init; } = LaunchArgumentSource.Mojang;
    [JsonRequired]
    public bool OfflineMode { get; init; }
    [JsonRequired]
    public string OfflineUsername { get; init; } = "Player";
    [JsonRequired]
    public InstanceParameters Parameters { get; init; } = new();
    [JsonRequired]
    public InstanceInstallationState State { get; init; } = InstanceInstallationState.Queued;
    [JsonRequired]
    public bool PauseRequested { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    [JsonRequired]
    public string JavaExecutable { get; init; } = "bin/javaw.exe";
    [JsonRequired]
    public int JavaMajorVersion { get; init; }
    [JsonRequired]
    public bool LegacyTexturepacks { get; init; }
    [JsonRequired]
    public InstanceModLoader ModLoader { get; init; } = InstanceModLoader.Vanilla;
    public FabricConfiguration? FabricConfiguration { get; init; }
    public string? FabricProfileSHA1 { get; init; }
    public string? Architecture { get; init; }
}

public sealed record InstanceDraft
{
    public string Name { get; init; } = "";
    public string IconSymbol { get; init; } = "shippingbox.fill";
    [JsonIgnore]
    public byte[]? IconData { get; init; }
    public LaunchArgumentSource ArgumentSource { get; init; } = LaunchArgumentSource.Mojang;
    public bool OfflineMode { get; init; }
    public string OfflineUsername { get; init; } = "Player";
    public InstanceParameters Parameters { get; init; } = new();
    public InstanceModLoader ModLoader { get; init; } = InstanceModLoader.Vanilla;
    public FabricConfiguration? FabricConfiguration { get; init; }
    public int JavaMajorVersion { get; init; }
    public bool LegacyTexturepacks { get; init; }
    public string? Architecture { get; init; }
}

public static class OfflineUsername
{
    public static bool IsValid(string? value) => value is { Length: >= 3 and <= 16 }
        && value.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_');
}
