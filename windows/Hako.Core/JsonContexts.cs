using System.Text.Json.Serialization;
using Hako.Core.Instances;
using Hako.Core.Minecraft;

namespace Hako.Core;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(InstanceRegistryFile))]
[JsonSerializable(typeof(ModRegistry))]
[JsonSerializable(typeof(ModOrigin))]
[JsonSerializable(typeof(FabricApiDescriptor))]
[JsonSerializable(typeof(FabricConfiguration))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(FabricModMetadata))]
[JsonSerializable(typeof(FabricProfile))]
[JsonSerializable(typeof(FabricLoaderEntry[]))]
[JsonSerializable(typeof(ModrinthVersion[]))]
[JsonSerializable(typeof(AccountSession))]
[JsonSerializable(typeof(GameProcessRecord))]
[JsonSerializable(typeof(DeviceCodeResponse))]
[JsonSerializable(typeof(MicrosoftTokenResponse))]
[JsonSerializable(typeof(OAuthErrorResponse))]
[JsonSerializable(typeof(XboxAuthRequest))]
[JsonSerializable(typeof(XboxAuthProperties))]
[JsonSerializable(typeof(XboxAuthResponse))]
[JsonSerializable(typeof(XboxDisplayClaims))]
[JsonSerializable(typeof(XboxUserClaim))]
[JsonSerializable(typeof(XboxProfileResponse))]
[JsonSerializable(typeof(XboxProfileUser))]
[JsonSerializable(typeof(XboxProfileSetting))]
[JsonSerializable(typeof(MinecraftLoginRequest))]
[JsonSerializable(typeof(MinecraftLoginResponse))]
[JsonSerializable(typeof(MinecraftProfileResponse))]
[JsonSerializable(typeof(MinecraftSessionProfileResponse))]
[JsonSerializable(typeof(MinecraftTexturePayload))]
[JsonSerializable(typeof(IdTokenClaims))]
internal partial class HakoJsonContext : JsonSerializerContext;
