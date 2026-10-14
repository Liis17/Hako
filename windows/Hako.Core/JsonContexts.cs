using System.Text.Json.Serialization;

namespace Hako.Core;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UserSettings))]
internal partial class HakoJsonContext : JsonSerializerContext;
