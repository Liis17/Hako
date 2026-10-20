using System.Text.Json.Serialization;

namespace Hako.Core;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(AccountSession))]
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
