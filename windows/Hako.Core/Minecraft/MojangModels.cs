using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hako.Core.Minecraft;

public sealed record MojangCatalog
{
    public MojangLatestVersions Latest { get; init; } = new();
    public List<MojangVersionEntry> Versions { get; init; } = [];
}

public sealed record MojangLatestVersions
{
    public string? Release { get; init; }
    public string? Snapshot { get; init; }
}

public sealed record MojangVersionEntry
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public long? ComplianceLevel { get; init; }
    public string? Time { get; init; }
    public string? ReleaseTime { get; init; }
}

public sealed record MojangDownload
{
    public string Url { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public long? Size { get; init; }
    public string? Path { get; init; }
    public string? Id { get; init; }
}

public sealed record MojangVersionMetadata
{
    public string Id { get; init; } = "";
    public string? Type { get; init; }
    public string? Time { get; init; }
    public string? ReleaseTime { get; init; }
    public long? ComplianceLevel { get; init; }
    public string? ClientVersion { get; init; }
    public string? MainClass { get; init; }
    public int? MinimumLauncherVersion { get; init; }
    public string? MinecraftArguments { get; init; }
    public Dictionary<string, MojangDownload> Downloads { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MojangLibrary> Libraries { get; init; } = [];
    public MojangJavaRequirement? JavaVersion { get; init; }
    public MojangAssetIndexReference? AssetIndex { get; init; }
    public string? Assets { get; init; }
    public Dictionary<string, List<MojangArgument>>? Arguments { get; init; }
    public Dictionary<string, MojangLoggingConfiguration>? Logging { get; init; }

    public MojangJavaRequirement Java => JavaVersion ?? new MojangJavaRequirement { Component = "jre-legacy", MajorVersion = 8 };
    public bool LegacyTexturepacks => Assets == "pre-1.6" || AssetIndex?.Id == "pre-1.6";
}

public sealed record MojangJavaRequirement
{
    public string Component { get; init; } = "";
    public int MajorVersion { get; init; }
}

[JsonConverter(typeof(MojangArgumentConverter))]
public sealed record MojangArgument
{
    public List<MojangRule>? Rules { get; init; }
    public JsonElement Value { get; init; }

    public IReadOnlyList<string> Values => Value.ValueKind switch
    {
        JsonValueKind.String => [Value.GetString() ?? ""],
        JsonValueKind.Array => Value.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? "")
            .ToArray(),
        _ => []
    };

    public bool IsAllowed(GameArchitecture architecture, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (Rules is not { Count: > 0 })
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in Rules)
        {
            if (rule.Matches(architecture, features))
            {
                allowed = rule.Action == "allow";
            }
        }

        return allowed;
    }
}

public sealed class MojangArgumentConverter : JsonConverter<MojangArgument>
{
    public override MojangArgument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
        {
            return new MojangArgument { Value = root.Clone() };
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var value))
        {
            throw new JsonException("Mojang arguments must be strings or objects with a value.");
        }

        MojangRule[]? rules = null;
        if (root.TryGetProperty("rules", out var rulesJson))
        {
            rules = JsonSerializer.Deserialize(rulesJson, MojangJsonContext.Default.MojangRuleArray);
        }

        return new MojangArgument { Rules = rules?.ToList(), Value = value.Clone() };
    }

    public override void Write(Utf8JsonWriter writer, MojangArgument value, JsonSerializerOptions options)
    {
        if (value.Rules is not { Count: > 0 })
        {
            value.Value.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("rules");
        JsonSerializer.Serialize(writer, value.Rules.ToArray(), MojangJsonContext.Default.MojangRuleArray);
        writer.WritePropertyName("value");
        value.Value.WriteTo(writer);
        writer.WriteEndObject();
    }
}

public sealed record MojangRule
{
    public string Action { get; init; } = "";
    public MojangOperatingSystem? Os { get; init; }
    public Dictionary<string, bool>? Features { get; init; }

    public bool Matches(GameArchitecture architecture, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (Os?.Name is not null && !string.Equals(Os.Name, "windows", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Os?.Arch is { Length: > 0 } pattern && !MatchesArchitecture(pattern, architecture))
        {
            return false;
        }

        var currentVersion = Environment.OSVersion.Version;
        var currentVersionText = currentVersion.ToString();
        if (Os?.Version is { Length: > 0 } versionPattern && !RegexMatches(versionPattern, currentVersionText))
        {
            return false;
        }

        if (Os?.VersionRange is { } range)
        {
            if (range.Min is { Length: > 0 } minimum && CompareVersions(currentVersionText, minimum) < 0)
            {
                return false;
            }

            if (range.Max is { Length: > 0 } maximum && CompareVersions(currentVersionText, maximum) > 0)
            {
                return false;
            }
        }

        return FeaturesMatch(features);
    }

    internal bool CouldApplyToWindows(IReadOnlyDictionary<string, bool>? features = null)
    {
        if (Os?.Name is not null && !string.Equals(Os.Name, "windows", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Os?.Version is { Length: > 0 } versionPattern
            && !RegexMatches(versionPattern, Environment.OSVersion.Version.ToString()))
        {
            return false;
        }

        return FeaturesMatch(features);
    }

    private bool FeaturesMatch(IReadOnlyDictionary<string, bool>? features) => (Features ?? []).All(feature =>
        (features is not null && features.TryGetValue(feature.Key, out var enabled) ? enabled : false) == feature.Value);

    private static bool MatchesArchitecture(string pattern, GameArchitecture architecture)
    {
        var aliases = architecture switch
        {
            GameArchitecture.Arm64 => new[] { "aarch64", "arm64" },
            GameArchitecture.X64 => ["amd64"],
            GameArchitecture.X86 => ["x86"],
            _ => []
        };
        var canonical = architecture.RuleArchitecture();

        return aliases.Any(alias => RegexMatchesArchitecture(pattern, alias))
            || RegexMatchesArchitecture(pattern, canonical);
    }

    private static bool RegexMatchesArchitecture(string pattern, string value) =>
        RegexMatches($"^(?:{pattern})$", value);

    private static bool RegexMatches(string pattern, string value)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(value, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record MojangOperatingSystem
{
    public string? Name { get; init; }
    public string? Arch { get; init; }
    public string? Version { get; init; }
    public MojangVersionRange? VersionRange { get; init; }
}

public sealed record MojangVersionRange
{
    public string? Min { get; init; }
    public string? Max { get; init; }
}

public sealed record MojangLibrary
{
    public string Name { get; init; } = "";
    public MojangLibraryDownloads? Downloads { get; init; }
    public Dictionary<string, string>? Natives { get; init; }
    public MojangLibraryExtraction? Extract { get; init; }
    public List<MojangRule>? Rules { get; init; }

    public string NativeGroup => string.Join(':', Name.Split(':').Take(3));
    public string? Classifier => Name.Split(':').ElementAtOrDefault(3);

    public bool IsAllowed(GameArchitecture architecture, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (Rules is not { Count: > 0 })
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in Rules)
        {
            if (rule.Matches(architecture, features))
            {
                allowed = rule.Action == "allow";
            }
        }

        return allowed;
    }

    public bool CouldApplyToWindows(IReadOnlyDictionary<string, bool>? features = null) =>
        Rules is not { Count: > 0 } || Rules.Any(rule => rule.CouldApplyToWindows(features));
}

public sealed record MojangLibraryDownloads
{
    public MojangDownload? Artifact { get; init; }
    public Dictionary<string, MojangDownload>? Classifiers { get; init; }
}

public sealed record MojangLibraryExtraction
{
    public List<string>? Exclude { get; init; }
}

public sealed record MojangLoggingConfiguration
{
    public MojangDownload File { get; init; } = new();
    public string? Argument { get; init; }
    public string? Type { get; init; }
}

public sealed record MojangAssetIndexReference
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public long Size { get; init; }
    public long TotalSize { get; init; }

    public MojangDownload Download => new() { Url = Url, Sha1 = Sha1, Size = Size };
}

public sealed record MojangAssetIndex
{
    public Dictionary<string, MojangAssetObject> Objects { get; init; } = new(StringComparer.Ordinal);
    public bool Virtual { get; init; }

    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; init; }
}

public sealed record MojangAssetObject
{
    public string Hash { get; init; } = "";
    public long Size { get; init; }
}

public sealed record MojangJavaRuntimePackage
{
    public MojangDownload Manifest { get; init; } = new();
    public MojangRuntimeVersion Version { get; init; } = new();

    public int? MajorVersion
    {
        get
        {
            var name = Version.Name;
            var start = name.StartsWith("1.", StringComparison.Ordinal) ? 2 : 0;
            var end = start;
            while (end < name.Length && char.IsAsciiDigit(name[end]))
            {
                end++;
            }

            return int.TryParse(name.AsSpan(start, end - start), out var parsed) ? parsed : null;
        }
    }
}

public sealed record MojangRuntimeVersion
{
    public string Name { get; init; } = "";
    public string Released { get; init; } = "";
}

public sealed record MojangJavaRuntimeManifest
{
    public Dictionary<string, MojangJavaRuntimeEntry> Files { get; init; } = new(StringComparer.Ordinal);
}

public sealed record MojangJavaRuntimeEntry
{
    public string Type { get; init; } = "";
    public Dictionary<string, MojangDownload>? Downloads { get; init; }
    public bool Executable { get; init; }
    public string? Target { get; init; }
}

public sealed record LibraryInstallation(
    MojangDownload Download,
    string Path,
    bool IsNative = false,
    bool ExtractNatives = false,
    IReadOnlyList<string>? ExtractionExcludes = null);

public enum MojangError
{
    Unsupported,
    Invalid,
    Http,
    Integrity
}

public sealed class MojangException(MojangError error, string message, int? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public MojangError Error { get; } = error;
    public int? StatusCode { get; } = statusCode;
}

public sealed record PreparedInstallation(
    MojangVersionEntry Version,
    GameArchitecture Architecture,
    MojangVersionMetadata Manifest,
    byte[] ManifestData,
    MojangJavaRuntimePackage Runtime,
    MojangJavaRuntimeManifest RuntimeManifest,
    byte[] RuntimeManifestData,
    IReadOnlyList<LibraryInstallation> Libraries);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MojangCatalog))]
[JsonSerializable(typeof(MojangVersionMetadata))]
[JsonSerializable(typeof(MojangAssetIndex))]
[JsonSerializable(typeof(MojangJavaRuntimeManifest))]
[JsonSerializable(typeof(MojangRule[]))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, List<MojangJavaRuntimePackage>>>))]
internal partial class MojangJsonContext : JsonSerializerContext;
