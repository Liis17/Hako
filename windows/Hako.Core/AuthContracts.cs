using System.Text;

namespace Hako.Core;

public enum SignInPhase
{
    RequestingCode,
    WaitingForCode,
    SigningIn,
    Failed
}

public enum AuthErrorKind
{
    Network,
    InvalidResponse,
    CodeExpired,
    AuthorizationDeclined,
    MicrosoftRejected,
    XboxProfileMissing,
    XboxUnavailable,
    XboxAgeVerification,
    XboxChildAccount,
    XboxRejected,
    Persistence
}

public sealed class AuthServiceException(AuthErrorKind kind, int? httpStatus = null) : Exception
{
    public AuthErrorKind Kind { get; } = kind;
    public int? HttpStatus { get; } = httpStatus;
}

public interface IMicrosoftAuthService
{
    Task<DeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken);
    Task<MicrosoftToken> WaitForTokenAsync(DeviceCode deviceCode, CancellationToken cancellationToken);
    Task<AccountSession> SignInAsync(MicrosoftToken token, CancellationToken cancellationToken);
}

public sealed record MicrosoftToken(
    string AccessToken,
    string RefreshToken,
    string? Email)
{
    // Tokens are left out of ToString() so logs and debugger output never show them.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Email = ").Append(Email);
        return true;
    }
}

public static class AuthErrorKindExtensions
{
    public static string ResourceKey(this AuthErrorKind error) => error switch
    {
        AuthErrorKind.Network => "AuthNetworkError",
        AuthErrorKind.InvalidResponse => "AuthInvalidResponse",
        AuthErrorKind.CodeExpired => "AuthCodeExpired",
        AuthErrorKind.AuthorizationDeclined => "AuthDeclined",
        AuthErrorKind.MicrosoftRejected => "AuthMicrosoftRejected",
        AuthErrorKind.XboxProfileMissing => "XboxProfileMissing",
        AuthErrorKind.XboxUnavailable => "XboxUnavailable",
        AuthErrorKind.XboxAgeVerification => "XboxAgeVerification",
        AuthErrorKind.XboxChildAccount => "XboxChildAccount",
        AuthErrorKind.XboxRejected => "XboxRejected",
        AuthErrorKind.Persistence => "AccountSaveFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null)
    };
}
