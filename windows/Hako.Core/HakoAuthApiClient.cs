using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Hako.Core;

public sealed class HakoAuthApiClient : IHakoAuthService, IDisposable
{
    public static readonly Uri ServiceBaseAddress = new("https://hako.liis17.com:443/");

    private const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly bool _ownsClient;

    public HakoAuthApiClient()
        : this(CreateDefaultHttpClient(), ServiceBaseAddress, ownsClient: true)
    {
    }

    public HakoAuthApiClient(HttpClient httpClient, Uri? baseAddress = null)
        : this(httpClient, baseAddress ?? ServiceBaseAddress, ownsClient: false)
    {
    }

    private HakoAuthApiClient(HttpClient httpClient, Uri baseAddress, bool ownsClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseAddress.UserInfo))
        {
            throw new ArgumentException("The Hako Auth API requires an absolute HTTPS address.", nameof(baseAddress));
        }

        _baseAddress = baseAddress;
        _ownsClient = ownsClient;
    }

    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = CreateDefaultHandler();
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    internal static HttpClientHandler CreateDefaultHandler() => new() { AllowAutoRedirect = false };

    public async Task<HakoPendingAuthRequest> RegisterAsync(string deviceName, CancellationToken cancellationToken)
    {
        var normalizedDeviceName = ValidateDeviceName(deviceName);
        using var request = CreateJsonRequest(HttpMethod.Post, "v1/auth/register",
            new HakoRegisterRequestDto(normalizedDeviceName), HakoJsonContext.Default.HakoRegisterRequestDto);
        var response = await SendJsonAsync(request, HakoJsonContext.Default.HakoCreateRequestResponseDto, cancellationToken);
        return ParsePendingRequest(response, requiresBotLink: true);
    }

    public async Task<HakoPendingAuthRequest> LoginAsync(
        string username,
        string deviceName,
        CancellationToken cancellationToken)
    {
        if (!HakoUsername.TryNormalize(username, out var normalizedUsername))
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidUsername);
        }

        var normalizedDeviceName = ValidateDeviceName(deviceName);
        using var request = CreateJsonRequest(HttpMethod.Post, "v1/auth/login",
            new HakoLoginRequestDto(normalizedUsername, normalizedDeviceName), HakoJsonContext.Default.HakoLoginRequestDto);
        var response = await SendJsonAsync(request, HakoJsonContext.Default.HakoCreateRequestResponseDto, cancellationToken);
        return ParsePendingRequest(response, requiresBotLink: false);
    }

    public async Task<HakoAuthRequestStatus> GetRequestStatusAsync(
        HakoPendingAuthRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseAddress,
            $"v1/auth/requests/{request.RequestId:D}"));
        SetBearer(message, request.ExchangeSecret);
        var response = await SendJsonAsync(message, HakoJsonContext.Default.HakoRequestStatusResponseDto, cancellationToken);
        if (!TryParseRequestStatus(response.Status, out var status))
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
        }

        if (response.ExpiresAt == default)
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
        }

        return new HakoAuthRequestStatus(status, response.ExpiresAt);
    }

    public async Task<HakoExchangeResult> ExchangeAsync(HakoPendingAuthRequest request, CancellationToken cancellationToken)
    {
        using var message = CreateJsonRequest(HttpMethod.Post, "v1/auth/exchange",
            new HakoExchangeRequestDto(request.RequestId), HakoJsonContext.Default.HakoExchangeRequestDto);
        SetBearer(message, request.ExchangeSecret);
        var response = await SendJsonAsync(message, HakoJsonContext.Default.HakoExchangeResponseDto, cancellationToken);
        if (string.IsNullOrWhiteSpace(response.Token)
            || response.TokenExpiresAt == default
            || response.Account is null
            || !TryParseAccount(response.Account, out var account))
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
        }

        return new HakoExchangeResult(response.Token, response.TokenExpiresAt, account);
    }

    public async Task<HakoMeResult> GetMeAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseAddress, "v1/auth/me"));
        SetBearer(request, token);
        var response = await SendJsonAsync(request, HakoJsonContext.Default.HakoMeResponseDto, cancellationToken);
        if (response.Account is null
            || response.Session is null
            || string.IsNullOrWhiteSpace(response.Session.DeviceName)
            || response.Session.ExpiresAt == default
            || !TryParseAccount(response.Account, out var account))
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
        }

        return new HakoMeResult(account, response.Session.DeviceName, response.Session.ExpiresAt);
    }

    public async Task LogoutAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseAddress, "v1/auth/logout"));
        SetBearer(request, token);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return;
        }

        throw await ReadErrorAsync(response, cancellationToken);
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadErrorAsync(response, cancellationToken);
        }

        if (!IsJson(response.Content.Headers.ContentType?.MediaType))
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse, (int)response.StatusCode, isKnownServerError: false);
        }

        var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize(bytes, typeInfo)
                ?? throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse, (int)response.StatusCode);
        }
        catch (JsonException exception)
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse, (int)response.StatusCode, innerException: exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new HakoAuthException(HakoAuthErrorKind.Timeout, innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new HakoAuthException(HakoAuthErrorKind.Network, innerException: exception);
        }
    }

    private static async Task<HakoAuthException> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        HakoApiErrorResponseDto? envelope = null;
        if (IsJson(response.Content.Headers.ContentType?.MediaType))
        {
            try
            {
                var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
                envelope = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.HakoApiErrorResponseDto);
            }
            catch (JsonException)
            {
            }
            catch (HakoAuthException)
            {
            }
        }

        var code = envelope?.Error?.Code;
        var kind = code switch
        {
            "rate_limited" => HakoAuthErrorKind.RateLimited,
            "bot_unavailable" => HakoAuthErrorKind.BotUnavailable,
            "account_not_found" => HakoAuthErrorKind.AccountNotFound,
            "request_denied" => HakoAuthErrorKind.AuthorizationDenied,
            "request_expired" => HakoAuthErrorKind.RequestExpired,
            "account_exists" => HakoAuthErrorKind.AccountExists,
            "request_consumed" => HakoAuthErrorKind.RequestConsumed,
            "request_pending" => HakoAuthErrorKind.InvalidResponse,
            "unauthorized" => HakoAuthErrorKind.Unauthorized,
            "invalid_request" => HakoAuthErrorKind.InvalidRequest,
            _ => StatusError(status)
        };

        return new HakoAuthException(kind, status, isKnownServerError: envelope?.Error is not null);
    }

    private static HakoAuthErrorKind StatusError(int status) => status switch
    {
        300 or 301 or 302 or 303 or 307 or 308 => HakoAuthErrorKind.RedirectRejected,
        401 => HakoAuthErrorKind.Unauthorized,
        403 => HakoAuthErrorKind.InvalidResponse,
        404 or 409 or 410 => HakoAuthErrorKind.InvalidResponse,
        429 => HakoAuthErrorKind.RateLimited,
        >= 500 => HakoAuthErrorKind.ServiceUnavailable,
        _ => HakoAuthErrorKind.InvalidResponse
    };

    private HttpRequestMessage CreateJsonRequest<T>(HttpMethod method, string path, T body, JsonTypeInfo<T> typeInfo)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseAddress, path));
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, typeInfo));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    private static void SetBearer(HttpRequestMessage request, string token) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static HakoPendingAuthRequest ParsePendingRequest(HakoCreateRequestResponseDto response, bool requiresBotLink)
    {
        if (!Guid.TryParse(response.RequestId, out var requestId)
            || string.IsNullOrWhiteSpace(response.ExchangeSecret)
            || response.ExpiresAt == default
            || (requiresBotLink && !TryValidateBotLink(response.BotLink, out _)))
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
        }

        Uri? botLink = null;
        if (response.BotLink is not null && TryValidateBotLink(response.BotLink, out var validatedLink))
        {
            botLink = validatedLink;
        }

        return new HakoPendingAuthRequest(requestId, response.ExchangeSecret, response.ExpiresAt, botLink);
    }

    private static bool TryValidateBotLink(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !string.Equals(parsed.Host, "t.me", StringComparison.OrdinalIgnoreCase)
            || parsed.Port != 443
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || string.IsNullOrWhiteSpace(parsed.AbsolutePath.Trim('/'))
            || parsed.Segments.Length != 2
            || string.IsNullOrWhiteSpace(parsed.Query)
            || !parsed.Query.TrimStart('?').Split('&').Any(part => part.StartsWith("start=", StringComparison.Ordinal) && part.Length > 6))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string ValidateDeviceName(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        var runeCount = normalized.EnumerateRunes().Count();
        if (runeCount is 0 or > 128)
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidRequest);
        }

        return normalized;
    }

    private static bool TryParseAccount(HakoApiAccountDto value, out HakoAccount account)
    {
        account = null!;
        if (string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.Username)
            || value.CreatedAt == default
            || value.Username.Length > 32 || value.Username.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
        {
            return false;
        }

        account = new HakoAccount(value.Id, value.Username, value.CreatedAt);
        return true;
    }

    private static bool TryParseRequestStatus(string value, out HakoRequestStatus status)
    {
        status = value switch
        {
            "pending" => HakoRequestStatus.Pending,
            "approved" => HakoRequestStatus.Approved,
            "denied" => HakoRequestStatus.Denied,
            "expired" => HakoRequestStatus.Expired,
            "account_exists" => HakoRequestStatus.AccountExists,
            "consumed" => HakoRequestStatus.Consumed,
            _ => (HakoRequestStatus)(-1)
        };
        return Enum.IsDefined(status);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                throw new HakoAuthException(HakoAuthErrorKind.InvalidResponse);
            }

            output.Write(buffer, 0, read);
        }
    }

    private static bool IsJson(string? mediaType) => mediaType is not null
        && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}

internal sealed record HakoRegisterRequestDto(
    [property: JsonPropertyName("device_name")] string DeviceName);

internal sealed record HakoLoginRequestDto(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("device_name")] string DeviceName);

internal sealed record HakoExchangeRequestDto(
    [property: JsonPropertyName("request_id")] Guid RequestId);

internal sealed record HakoCreateRequestResponseDto(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("bot_link")] string? BotLink,
    [property: JsonPropertyName("exchange_secret")] string ExchangeSecret,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("RequestId = ").Append(RequestId)
            .Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}

internal sealed record HakoRequestStatusResponseDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

internal sealed record HakoExchangeResponseDto(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("token_expires_at")] DateTimeOffset TokenExpiresAt,
    [property: JsonPropertyName("account")] HakoApiAccountDto? Account)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("TokenExpiresAt = ").Append(TokenExpiresAt)
            .Append(", Account = ").Append(Account);
        return true;
    }
}

internal sealed record HakoMeResponseDto(
    [property: JsonPropertyName("account")] HakoApiAccountDto? Account,
    [property: JsonPropertyName("session")] HakoApiSessionDto? Session);

internal sealed record HakoApiAccountDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

internal sealed record HakoApiSessionDto(
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

internal sealed record HakoApiErrorResponseDto([property: JsonPropertyName("error")] HakoApiErrorDto? Error);

internal sealed record HakoApiErrorDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string? Message);
