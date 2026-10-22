using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hako.Core.Instances;

namespace Hako.Core.Minecraft;

public sealed record FabricLoaderVersion(string Version, bool Stable);

public sealed record FabricLoaderEntry(FabricLoaderVersion Loader);

public sealed record FabricProfileLibrary
{
    public string Name { get; init; } = "";
    public Uri Url { get; init; } = new("https://maven.fabricmc.net/");
    public string? Sha1 { get; init; }
    public long? Size { get; init; }

    public string MavenPath => FabricMavenLibrary.Parse(Name).Path;
}

public sealed record FabricProfile
{
    public string Id { get; init; } = "";
    public string InheritsFrom { get; init; } = "";
    public string MainClass { get; init; } = "";
    public Dictionary<string, List<MojangArgument>>? Arguments { get; init; }
    public List<FabricProfileLibrary> Libraries { get; init; } = [];

    public void Validate(string minecraft, string loader)
    {
        if (!string.Equals(InheritsFrom, minecraft, StringComparison.Ordinal)
            || !string.Equals(Id, $"fabric-loader-{loader}-{minecraft}", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(MainClass))
        {
            throw new MojangException(MojangError.Invalid, "Fabric returned a profile that does not match this build.");
        }

        _ = InstanceStorage.ContainedPath(Id, Path.GetTempPath());
    }
}

public sealed record FabricMavenLibrary(string Group, string Artifact, string Version, string? Classifier)
{
    private static readonly Regex Segment = new("^[A-Za-z0-9_.+\\-]+$", RegexOptions.CultureInvariant);

    public string Path
    {
        get
        {
            var groupPath = string.Join('/', Group.Split('.'));
            var suffix = Classifier is null ? "" : "-" + Classifier;
            return $"{groupPath}/{Artifact}/{Version}/{Artifact}-{Version}{suffix}.jar";
        }
    }

    public string Key => $"{Group}:{Artifact}" + (Classifier is null ? "" : $":{Classifier}");

    public static FabricMavenLibrary Parse(string value)
    {
        var parts = value.Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(part => part.Length == 0 || !Segment.IsMatch(part)))
        {
            throw new MojangException(MojangError.Invalid, "Fabric returned an invalid Maven library name.");
        }

        return new FabricMavenLibrary(parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : null);
    }

    public static string? KeyFromPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Length < 4 || !parts[^1].EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var artifact = parts[^3];
        var filename = parts[^1][..^4];
        var prefix = artifact + "-" + parts[^2];
        if (!filename.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var classifier = filename.Length == prefix.Length ? null : filename[prefix.Length..].TrimStart('-');
        if (classifier is { Length: 0 })
        {
            return null;
        }

        var group = string.Join('.', parts[..^3]);
        return group.Length == 0 ? null : $"{group}:{artifact}" + (classifier is null ? "" : $":{classifier}");
    }
}

[JsonConverter(typeof(FabricVersionPredicateJsonConverter))]
public sealed record FabricVersionPredicate(IReadOnlyList<string> Alternatives)
{
    public FabricVersionPredicate(string value) : this([value]) { }

    public bool Matches(string version)
    {
        foreach (var alternative in Alternatives)
        {
            var terms = alternative.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0)
            {
                throw Invalid("A Fabric version requirement is empty.");
            }

            if (terms.All(term => MatchesTerm(term, version)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesTerm(string term, string value)
    {
        if (term is "*" or "x" or "X")
        {
            return true;
        }

        var operation = new[] { ">=", "<=", ">", "<", "=", "~", "^" }
            .FirstOrDefault(candidate => term.StartsWith(candidate, StringComparison.Ordinal)) ?? "=";
        var text = operation == "=" && !term.StartsWith('=') ? term : term[operation.Length..];
        if (text.Length == 0)
        {
            throw Invalid($"Invalid Fabric version requirement: {term}.");
        }

        var pieces = text.Split('.');
        var wildcardIndex = Array.FindIndex(pieces, piece => piece is "*" or "x" or "X");
        if (wildcardIndex >= 0 && (wildcardIndex == 0 || pieces.Skip(wildcardIndex).Any(piece => piece is not ("*" or "x" or "X"))))
        {
            throw Invalid($"Invalid Fabric version requirement: {term}.");
        }

        var reference = FabricSemanticVersion.Parse(wildcardIndex < 0 ? text : string.Join('.', pieces.Take(wildcardIndex)));
        var current = FabricSemanticVersion.Parse(value);
        var comparison = current.CompareTo(reference);
        if (wildcardIndex >= 0)
        {
            var upper = reference.Increment(wildcardIndex - 1);
            return operation switch
            {
                ">" => current.CompareTo(upper) >= 0,
                ">=" => comparison >= 0,
                "<=" => current.CompareTo(upper) < 0,
                "<" => comparison < 0,
                _ => comparison >= 0 && current.CompareTo(upper) < 0
            };
        }

        if (operation is "~" or "^")
        {
            return comparison >= 0 && current.CompareTo(reference.Increment(operation == "~" && reference.Numbers.Count > 1 ? 1 : 0)) < 0;
        }

        return operation switch
        {
            ">=" => comparison >= 0,
            ">" => comparison > 0,
            "<=" => comparison <= 0,
            "<" => comparison < 0,
            _ => comparison == 0
        };
    }

    private static MojangException Invalid(string message) => new(MojangError.Invalid, message);
}

public sealed class FabricVersionPredicateJsonConverter : JsonConverter<FabricVersionPredicate>
{
    public override FabricVersionPredicate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new FabricVersionPredicate(reader.GetString() ?? "");
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var alternatives = JsonSerializer.Deserialize(ref reader, HakoJsonContext.Default.StringArray)
                ?? throw new JsonException("Fabric version alternatives are missing.");
            return new FabricVersionPredicate(alternatives);
        }

        throw new JsonException("Fabric version requirement must be a string or array.");
    }

    public override void Write(Utf8JsonWriter writer, FabricVersionPredicate value, JsonSerializerOptions options)
    {
        if (value.Alternatives.Count == 1)
        {
            writer.WriteStringValue(value.Alternatives[0]);
            return;
        }

        JsonSerializer.Serialize(writer, value.Alternatives.ToArray(), HakoJsonContext.Default.StringArray);
    }
}

public sealed record FabricModMetadata
{
    public string Id { get; init; } = "";
    public Dictionary<string, FabricVersionPredicate>? Depends { get; init; }

    public bool Supports(string loader, int java) =>
        (Depends?.GetValueOrDefault("fabricloader") ?? new FabricVersionPredicate("*")).Matches(loader)
        && (Depends?.GetValueOrDefault("java") ?? new FabricVersionPredicate("*")).Matches(java.ToString(CultureInfo.InvariantCulture));
}

public sealed record FabricPreparation(
    FabricApiDescriptor Api,
    FabricModMetadata Metadata,
    IReadOnlyList<FabricLoaderVersion> Loaders);

public sealed record ModrinthVersion
{
    public string Id { get; init; } = "";
    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = "";
    [JsonPropertyName("version_number")]
    public string VersionNumber { get; init; } = "";
    [JsonPropertyName("version_type")]
    public string VersionType { get; init; } = "";
    [JsonPropertyName("date_published")]
    public DateTimeOffset DatePublished { get; init; }
    [JsonPropertyName("game_versions")]
    public List<string> GameVersions { get; init; } = [];
    public List<string> Loaders { get; init; } = [];
    public List<ModrinthFile> Files { get; init; } = [];
    public List<ModrinthDependency> Dependencies { get; init; } = [];

    public ModrinthFile? InstallableFile(ModrinthContentKind kind)
    {
        var candidates = Files.Where(file => file.IsInstallable(kind)
            && (kind != ModrinthContentKind.Datapack || !file.IsResourcePack)).ToArray();
        return candidates.FirstOrDefault(file => file.Primary) ?? candidates.FirstOrDefault();
    }

    public ModrinthFile? UpdateFile(ModrinthContentKind kind, string? fileType, string filename)
    {
        if (kind == ModrinthContentKind.Mod) return InstallableFile(kind);
        var candidates = Files.Where(file => file.IsInstallable(kind)
            && (kind != ModrinthContentKind.Datapack || !file.IsResourcePack)
            && string.Equals(file.FileType, fileType, StringComparison.Ordinal)).ToArray();
        return candidates.FirstOrDefault(file => string.Equals(file.Filename, filename, StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Length == 1 ? candidates[0] : null);
    }
}

public sealed record ModrinthDependency
{
    [JsonPropertyName("version_id")]
    public string? VersionId { get; init; }
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }
    [JsonPropertyName("dependency_type")]
    public string Type { get; init; } = "";
}

public sealed record ModrinthFile
{
    public string Filename { get; init; } = "";
    public Uri Url { get; init; } = new("https://cdn.modrinth.com/");
    public long Size { get; init; }
    public Dictionary<string, string> Hashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Primary { get; init; }
    [JsonPropertyName("file_type")]
    public string? FileType { get; init; }

    public string Sha1 => Hashes.GetValueOrDefault("sha1", "").ToLowerInvariant();
    public string Sha512 => Hashes.GetValueOrDefault("sha512", "").ToLowerInvariant();
    public bool IsResourcePack => FileType is "required-resource-pack" or "optional-resource-pack";

    public bool IsInstallable(ModrinthContentKind kind) => IsInstallable(kind switch
    {
        ModrinthContentKind.Mod => ".jar",
        ModrinthContentKind.ResourcePack or ModrinthContentKind.Datapack => ".zip",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    });

    public bool IsInstallable(string fileExtension)
    {
        if (!InstanceStorage.IsSinglePathSegment(Filename) || Filename.StartsWith('.')
            || !Filename.EndsWith(fileExtension, StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(Url.ToString(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || Size <= 0 || !IsHash(Sha1, 40) || !IsHash(Sha512, 128)
            || FileType is "sources-jar" or "dev-jar" or "javadoc-jar")
        {
            return false;
        }

        var suffix = Path.GetFileNameWithoutExtension(Filename).Split('-', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return suffix is not ("sources" or "source" or "dev" or "javadoc");
    }

    private static bool IsHash(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
}

internal readonly record struct FabricSemanticVersion(IReadOnlyList<int> Numbers, IReadOnlyList<string> Prerelease) : IComparable<FabricSemanticVersion>
{
    public static FabricSemanticVersion Parse(string value)
    {
        var withoutBuild = value.Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        var numberText = parts[0].Split('.');
        if (numberText.Length == 0 || numberText.Any(part => !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0))
        {
            throw new MojangException(MojangError.Invalid, $"Could not parse Fabric version: {value}.");
        }

        var numbers = numberText.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        var prerelease = parts.Length == 1 ? [] : parts[1].Split('.').ToArray();
        if (prerelease.Any(part => part.Length == 0))
        {
            throw new MojangException(MojangError.Invalid, $"Could not parse Fabric version: {value}.");
        }

        return new FabricSemanticVersion(numbers, prerelease);
    }

    public FabricSemanticVersion Increment(int index)
    {
        var numbers = Numbers.ToList();
        while (numbers.Count <= index) numbers.Add(0);
        numbers[index]++;
        for (var next = index + 1; next < numbers.Count; next++) numbers[next] = 0;
        return new FabricSemanticVersion(numbers, []);
    }

    public int CompareTo(FabricSemanticVersion other)
    {
        for (var index = 0; index < Math.Max(Numbers.Count, other.Numbers.Count); index++)
        {
            var left = index < Numbers.Count ? Numbers[index] : 0;
            var right = index < other.Numbers.Count ? other.Numbers[index] : 0;
            if (left != right) return left.CompareTo(right);
        }

        if (Prerelease.Count == 0 || other.Prerelease.Count == 0)
        {
            return Prerelease.Count == other.Prerelease.Count ? 0 : Prerelease.Count == 0 ? 1 : -1;
        }

        for (var index = 0; index < Math.Min(Prerelease.Count, other.Prerelease.Count); index++)
        {
            var left = Prerelease[index];
            var right = other.Prerelease[index];
            if (left == right) continue;
            var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            if (leftNumeric && rightNumeric) return leftNumber.CompareTo(rightNumber);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            return string.CompareOrdinal(left, right);
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(FabricLoaderEntry[]))]
[JsonSerializable(typeof(FabricProfile))]
[JsonSerializable(typeof(FabricModMetadata))]
[JsonSerializable(typeof(ModrinthVersion[]))]
internal partial class FabricJsonContext : JsonSerializerContext;
