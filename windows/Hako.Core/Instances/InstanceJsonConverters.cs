using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hako.Core.Instances;

internal sealed class LaunchArgumentSourceJsonConverter : JsonStringEnumConverter<LaunchArgumentSource>
{
    public LaunchArgumentSourceJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}

internal sealed class InstanceInstallationStateJsonConverter : JsonStringEnumConverter<InstanceInstallationState>
{
    public InstanceInstallationStateJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}

internal sealed class InstanceModLoaderJsonConverter : JsonStringEnumConverter<InstanceModLoader>
{
    public InstanceModLoaderJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}
