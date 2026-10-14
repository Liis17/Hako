using System.Text;

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
    DateTimeOffset? MinecraftTokenExpiresAt)
{
    // Tokens are left out of ToString() so logs and debugger output never show them.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Xuid = ").Append(Xuid)
            .Append(", Gamertag = ").Append(Gamertag)
            .Append(", Email = ").Append(Email)
            .Append(", XboxAvatarUrl = ").Append(XboxAvatarUrl)
            .Append(", MinecraftProfileId = ").Append(MinecraftProfileId)
            .Append(", MinecraftName = ").Append(MinecraftName)
            .Append(", MinecraftTokenExpiresAt = ").Append(MinecraftTokenExpiresAt);
        return true;
    }
}
