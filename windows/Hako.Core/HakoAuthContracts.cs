using System.Text;

namespace Hako.Core;

public enum HakoAuthErrorKind
{
    Network,
    Timeout,
    InvalidResponse,
    RedirectRejected,
    RateLimited,
    ServiceUnavailable,
    BotUnavailable,
    InvalidUsername,
    InvalidRequest,
    AccountNotFound,
    AuthorizationDenied,
    RequestExpired,
    AccountExists,
    RequestConsumed,
    Unauthorized,
    Persistence
}

public sealed class HakoAuthException(
    HakoAuthErrorKind kind,
    int? httpStatus = null,
    bool isKnownServerError = false,
    Exception? innerException = null)
    : Exception("Hako authentication could not be completed.", innerException)
{
    public HakoAuthErrorKind Kind { get; } = kind;
    public int? HttpStatus { get; } = httpStatus;
    public bool IsKnownServerError { get; } = isKnownServerError;
}

public enum HakoAuthorizationMode
{
    Login,
    Register
}

public enum HakoAttemptPhase
{
    Idle,
    Requesting,
    Pending,
    Exchanging,
    Failed
}

public enum HakoRequestStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    AccountExists,
    Consumed
}

public enum HakoSessionStatus
{
    SignedOut,
    Unverified,
    Verifying,
    Verified,
    Expired,
    LogoutFailed
}

public enum HakoLogoutResult
{
    SignedOut,
    RemoteLogoutFailed,
    LocalClearFailed
}

public static class HakoSessionStatusExtensions
{
    public static string ResourceKey(this HakoSessionStatus status) => status switch
    {
        HakoSessionStatus.SignedOut => "HakoProfileNotConnected",
        HakoSessionStatus.Unverified => "HakoProfileUnverified",
        HakoSessionStatus.Verifying => "HakoProfileVerifying",
        HakoSessionStatus.Verified => "HakoProfileVerified",
        HakoSessionStatus.Expired => "HakoProfileExpired",
        HakoSessionStatus.LogoutFailed => "HakoProfileLogoutFailed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}

public sealed record HakoAccount(string Id, string Username, DateTimeOffset CreatedAt);

public sealed record HakoSession(
    string Token,
    HakoAccount Account,
    string DeviceName,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? VerifiedAt)
{
    // Session tokens must never appear in debugger or diagnostic output.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Account = ").Append(Account)
            .Append(", DeviceName = ").Append(DeviceName)
            .Append(", ExpiresAt = ").Append(ExpiresAt)
            .Append(", VerifiedAt = ").Append(VerifiedAt);
        return true;
    }
}

public sealed record HakoPendingAuthRequest(
    Guid RequestId,
    string ExchangeSecret,
    DateTimeOffset ExpiresAt,
    Uri? BotLink)
{
    // Exchange secrets stay in memory and are never included in logs or diagnostics.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("RequestId = ").Append(RequestId)
            .Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}

public sealed record HakoAuthRequestStatus(HakoRequestStatus Status, DateTimeOffset ExpiresAt);

public sealed record HakoExchangeResult(string Token, DateTimeOffset TokenExpiresAt, HakoAccount Account)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("TokenExpiresAt = ").Append(TokenExpiresAt)
            .Append(", Account = ").Append(Account);
        return true;
    }
}

public sealed record HakoMeResult(HakoAccount Account, string DeviceName, DateTimeOffset SessionExpiresAt);

public interface IHakoAuthService
{
    Task<HakoPendingAuthRequest> RegisterAsync(string deviceName, CancellationToken cancellationToken);
    Task<HakoPendingAuthRequest> LoginAsync(string username, string deviceName, CancellationToken cancellationToken);
    Task<HakoAuthRequestStatus> GetRequestStatusAsync(HakoPendingAuthRequest request, CancellationToken cancellationToken);
    Task<HakoExchangeResult> ExchangeAsync(HakoPendingAuthRequest request, CancellationToken cancellationToken);
    Task<HakoMeResult> GetMeAsync(string token, CancellationToken cancellationToken);
    Task LogoutAsync(string token, CancellationToken cancellationToken);
}

public interface IHakoSessionStore
{
    Task<HakoSession?> LoadAsync(CancellationToken cancellationToken = default);

    Task<bool> SaveAsync(
        HakoSession session,
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken = default);

    Task<bool> ClearAsync(
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken = default);
}

public sealed class HakoSessionStoreException : Exception
{
    public HakoSessionStoreException() : base("The saved Hako account could not be read or written.") { }
    public HakoSessionStoreException(Exception innerException)
        : base("The saved Hako account could not be read or written.", innerException) { }
}

public static class HakoUsername
{
    public static bool TryNormalize(string? value, out string username)
    {
        username = value?.Trim() ?? string.Empty;
        if (username.StartsWith('@'))
        {
            username = username[1..];
        }

        if (username.Length is < 5 or > 32)
        {
            return false;
        }

        foreach (var character in username)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

public static class HakoAuthErrorKindExtensions
{
    public static string ResourceKey(this HakoAuthErrorKind error) => error switch
    {
        HakoAuthErrorKind.Network => "HakoAuthNetworkError",
        HakoAuthErrorKind.Timeout => "HakoAuthTimeout",
        HakoAuthErrorKind.InvalidResponse => "HakoAuthInvalidResponse",
        HakoAuthErrorKind.RedirectRejected => "HakoAuthRedirectRejected",
        HakoAuthErrorKind.RateLimited => "HakoAuthRateLimited",
        HakoAuthErrorKind.ServiceUnavailable => "HakoAuthServiceUnavailable",
        HakoAuthErrorKind.BotUnavailable => "HakoAuthBotUnavailable",
        HakoAuthErrorKind.InvalidUsername => "HakoAuthInvalidUsername",
        HakoAuthErrorKind.InvalidRequest => "HakoAuthInvalidRequest",
        HakoAuthErrorKind.AccountNotFound => "HakoAuthAccountNotFound",
        HakoAuthErrorKind.AuthorizationDenied => "HakoAuthDenied",
        HakoAuthErrorKind.RequestExpired => "HakoAuthExpired",
        HakoAuthErrorKind.AccountExists => "HakoAuthAccountExists",
        HakoAuthErrorKind.RequestConsumed => "HakoAuthFreshLoginRequired",
        HakoAuthErrorKind.Unauthorized => "HakoAuthUnauthorized",
        HakoAuthErrorKind.Persistence => "HakoAuthPersistenceError",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null)
    };
}
