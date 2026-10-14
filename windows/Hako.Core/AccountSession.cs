namespace Hako.Core;

public sealed record AccountSession(
    string Xuid,
    string Gamertag,
    string? Email,
    string? XboxAvatarUrl,
    string? MinecraftProfileId,
    string? MinecraftName,
    string MicrosoftRefreshToken,
    string? MinecraftAccessToken,
    DateTimeOffset? MinecraftTokenExpiresAt);
