using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hako.Core;

public sealed class MicrosoftDeviceAuth(HttpClient httpClient, TimeProvider timeProvider) : IMicrosoftAuthService
{
    public const string ClientId = "5ca0e2a1-52ce-4ba5-afab-da8048e7b124";

    private static readonly Uri OAuthBase = new("https://login.microsoftonline.com/consumers/oauth2/v2.0/");
    private static readonly Uri XboxUserAuth = new("https://user.auth.xboxlive.com/user/authenticate");
    private static readonly Uri XboxXstsAuth = new("https://xsts.auth.xboxlive.com/xsts/authorize");
    private static readonly Uri XboxProfile = new("https://profile.xboxlive.com/users/me/profile/settings?settings=GameDisplayPicRaw");
    private static readonly Uri MinecraftLogin = new("https://api.minecraftservices.com/authentication/login_with_xbox");
    private static readonly Uri MinecraftProfile = new("https://api.minecraftservices.com/minecraft/profile");

    public async Task<DeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(
            new Uri(OAuthBase, "devicecode"),
            new FormUrlEncodedContent(
            [
                new("client_id", ClientId),
                new("scope", "XboxLive.signin openid profile email offline_access")
            ]),
            cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw OAuthFailure(bytes, response.StatusCode);
        }

        var result = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.DeviceCodeResponse);
        if (result?.DeviceCode is not { Length: > 0 } deviceCode
            || result.UserCode is not { Length: > 0 } userCode
            || !Uri.TryCreate(result.VerificationUri, UriKind.Absolute, out var verificationUri)
            || verificationUri.Scheme != Uri.UriSchemeHttps
            || result.ExpiresIn <= 0)
        {
            throw new AuthServiceException(AuthErrorKind.InvalidResponse);
        }

        return new DeviceCode(deviceCode, userCode, verificationUri, result.ExpiresIn, Math.Max(1, result.Interval));
    }

    public async Task<MicrosoftToken> WaitForTokenAsync(DeviceCode deviceCode, CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow().AddSeconds(deviceCode.ExpiresInSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(1, deviceCode.PollIntervalSeconds));

        while (true)
        {
            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                throw new AuthServiceException(AuthErrorKind.CodeExpired);
            }

            await Task.Delay(interval < remaining ? interval : remaining, timeProvider, cancellationToken);
            if (timeProvider.GetUtcNow() >= deadline)
            {
                throw new AuthServiceException(AuthErrorKind.CodeExpired);
            }

            using var response = await httpClient.PostAsync(
                new Uri(OAuthBase, "token"),
                new FormUrlEncodedContent(
                [
                    new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    new("client_id", ClientId),
                    new("device_code", deviceCode.DeviceCodeValue)
                ]),
                cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var token = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.MicrosoftTokenResponse);
                if (token?.AccessToken is not { Length: > 0 } accessToken
                    || token.RefreshToken is not { Length: > 0 } refreshToken)
                {
                    throw new AuthServiceException(AuthErrorKind.InvalidResponse);
                }

                return new MicrosoftToken(accessToken, refreshToken, ReadDisplayEmail(token.IdToken));
            }

            var error = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.OAuthErrorResponse)?.Error;
            switch (error)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case "authorization_declined":
                case "access_denied":
                    throw new AuthServiceException(AuthErrorKind.AuthorizationDeclined);
                case "expired_token":
                    throw new AuthServiceException(AuthErrorKind.CodeExpired);
                default:
                    throw new AuthServiceException(AuthErrorKind.MicrosoftRejected, (int)response.StatusCode);
            }
        }
    }

    public async Task<AccountSession> SignInAsync(MicrosoftToken token, CancellationToken cancellationToken)
    {
        var userToken = await XboxRequestAsync(XboxUserAuth, new XboxAuthRequest(
            new XboxAuthProperties("RPS", "user.auth.xboxlive.com", "d=" + token.AccessToken, null, null)),
            "http://auth.xboxlive.com", cancellationToken);
        var xboxUserToken = userToken.Token ?? throw new AuthServiceException(AuthErrorKind.InvalidResponse);
        var xboxProfileToken = await AuthorizeXboxAsync(xboxUserToken, "http://xboxlive.com", cancellationToken);
        var xboxClaims = xboxProfileToken.DisplayClaims?.Xui?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(xboxClaims?.Xuid) || string.IsNullOrWhiteSpace(xboxClaims.Gamertag)
            || string.IsNullOrWhiteSpace(xboxClaims.UserHash))
        {
            throw new AuthServiceException(AuthErrorKind.InvalidResponse);
        }

        var avatarUrl = await TryGetAvatarAsync(xboxProfileToken, cancellationToken);
        var minecraft = await TryGetMinecraftProfileAsync(xboxUserToken, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        return new AccountSession(
            xboxClaims.Xuid,
            xboxClaims.Gamertag,
            token.Email,
            avatarUrl,
            minecraft?.Profile.Id,
            minecraft?.Profile.Name,
            token.RefreshToken,
            minecraft?.AccessToken,
            minecraft?.ExpiresAt);
    }

    public async Task<MinecraftConnection> ConnectMinecraftAsync(
        string refreshToken,
        Func<string, CancellationToken, Task> persistRefreshToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentNullException.ThrowIfNull(persistRefreshToken);

        using var refreshResponse = await httpClient.PostAsync(
            new Uri(OAuthBase, "token"),
            new FormUrlEncodedContent(
            [
                new("grant_type", "refresh_token"),
                new("client_id", ClientId),
                new("refresh_token", refreshToken)
            ]),
            cancellationToken);
        var refreshBytes = await refreshResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!refreshResponse.IsSuccessStatusCode)
        {
            var error = TryDeserialize(refreshBytes, HakoJsonContext.Default.OAuthErrorResponse)?.Error;
            throw error == "invalid_grant"
                ? new AuthServiceException(AuthErrorKind.RefreshTokenRejected, (int)refreshResponse.StatusCode)
                : OAuthFailure(refreshBytes, refreshResponse.StatusCode);
        }

        var token = JsonSerializer.Deserialize(refreshBytes, HakoJsonContext.Default.MicrosoftTokenResponse);
        if (token?.AccessToken is not { Length: > 0 } accessToken
            || token.RefreshToken is not { Length: > 0 } rotatedRefreshToken)
        {
            throw new AuthServiceException(AuthErrorKind.InvalidResponse);
        }

        // Persist the rotation before making any Xbox or Minecraft request. If those later fail,
        // the next attempt must use the newest Microsoft refresh token.
        await persistRefreshToken(rotatedRefreshToken, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var userToken = await XboxRequestAsync(XboxUserAuth, new XboxAuthRequest(
            new XboxAuthProperties("RPS", "user.auth.xboxlive.com", "d=" + accessToken, null, null)),
            "http://auth.xboxlive.com", cancellationToken);
        var xboxUserToken = userToken.Token ?? throw new AuthServiceException(AuthErrorKind.InvalidResponse);
        var xsts = await AuthorizeXboxAsync(xboxUserToken, "rp://api.minecraftservices.com/", cancellationToken);
        var claims = xsts.DisplayClaims?.Xui?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(claims?.UserHash) || string.IsNullOrWhiteSpace(xsts.Token))
        {
            throw new AuthServiceException(AuthErrorKind.InvalidResponse);
        }

        var loginRequest = new MinecraftLoginRequest($"XBL3.0 x={claims.UserHash};{xsts.Token}");
        using var loginMessage = CreateJsonRequest(MinecraftLogin, loginRequest, HakoJsonContext.Default.MinecraftLoginRequest);
        using var loginResponse = await httpClient.SendAsync(loginMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (loginResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new AuthServiceException(AuthErrorKind.MinecraftLicenseMissing, (int)loginResponse.StatusCode);
        }

        if (!loginResponse.IsSuccessStatusCode)
        {
            throw new AuthServiceException(AuthErrorKind.InvalidResponse, (int)loginResponse.StatusCode);
        }

        var loginBytes = await loginResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var login = JsonSerializer.Deserialize(loginBytes, HakoJsonContext.Default.MinecraftLoginResponse);
        if (login?.AccessToken is not { Length: > 0 } minecraftAccessToken || login.ExpiresIn <= 0)
        {
            throw new AuthServiceException(AuthErrorKind.InvalidResponse);
        }

        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, MinecraftProfile);
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minecraftAccessToken);
        using var profileResponse = await httpClient.SendAsync(profileRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
        {
            throw new AuthServiceException(AuthErrorKind.MinecraftProfileMissing, (int)profileResponse.StatusCode);
        }

        if (profileResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new AuthServiceException(AuthErrorKind.MinecraftLicenseMissing, (int)profileResponse.StatusCode);
        }

        if (!profileResponse.IsSuccessStatusCode)
        {
            throw new AuthServiceException(AuthErrorKind.InvalidResponse, (int)profileResponse.StatusCode);
        }

        var profileBytes = await profileResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        var profile = JsonSerializer.Deserialize(profileBytes, HakoJsonContext.Default.MinecraftProfileResponse);
        if (string.IsNullOrWhiteSpace(profile?.Id) || string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new AuthServiceException(AuthErrorKind.MinecraftProfileMissing);
        }

        return new MinecraftConnection(profile.Id!, profile.Name!, minecraftAccessToken, timeProvider.GetUtcNow().AddSeconds(login.ExpiresIn));
    }

    private async Task<XboxAuthResponse> AuthorizeXboxAsync(string userToken, string relyingParty, CancellationToken cancellationToken)
    {
        return await XboxRequestAsync(XboxXstsAuth, new XboxAuthRequest(
            new XboxAuthProperties(null, null, null, "RETAIL", [userToken])), relyingParty, cancellationToken);
    }

    private async Task<XboxAuthResponse> XboxRequestAsync(
        Uri uri,
        XboxAuthRequest requestBody,
        string relyingParty,
        CancellationToken cancellationToken)
    {
        var body = new XboxAuthRequest(requestBody.Properties, relyingParty, "JWT");
        using var request = CreateJsonRequest(uri, body, HakoJsonContext.Default.XboxAuthRequest);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = TryDeserialize(bytes, HakoJsonContext.Default.XboxAuthResponse);
            if (error?.XErr is { } xerr)
            {
                throw new AuthServiceException(ClassifyXboxError(xerr), (int)response.StatusCode);
            }

            throw new AuthServiceException(AuthErrorKind.InvalidResponse, (int)response.StatusCode);
        }

        var result = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.XboxAuthResponse);
        if (result?.Token is not { Length: > 0 })
        {
            throw new AuthServiceException(AuthErrorKind.InvalidResponse, (int)response.StatusCode);
        }

        return result;
    }

    private async Task<string?> TryGetAvatarAsync(XboxAuthResponse xboxToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, XboxProfile);
            var userHash = xboxToken.DisplayClaims?.Xui?.FirstOrDefault()?.UserHash;
            if (string.IsNullOrWhiteSpace(userHash))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("XBL3.0", $"x={userHash};{xboxToken.Token}");
            request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "3");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var profile = JsonSerializer.Deserialize(bytes, HakoJsonContext.Default.XboxProfileResponse);
            var image = profile?.ProfileUsers?.FirstOrDefault()?.Settings?.FirstOrDefault(setting => setting.Id == "GameDisplayPicRaw");
            cancellationToken.ThrowIfCancellationRequested();
            return Uri.TryCreate(image?.Value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task<MinecraftSessionData?> TryGetMinecraftProfileAsync(string userToken, CancellationToken cancellationToken)
    {
        try
        {
            var xsts = await AuthorizeXboxAsync(userToken, "rp://api.minecraftservices.com/", cancellationToken);
            var claims = xsts.DisplayClaims?.Xui?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(claims?.UserHash))
            {
                throw new AuthServiceException(AuthErrorKind.InvalidResponse);
            }

            var loginRequest = new MinecraftLoginRequest($"XBL3.0 x={claims.UserHash};{xsts.Token}");
            using var loginMessage = CreateJsonRequest(MinecraftLogin, loginRequest, HakoJsonContext.Default.MinecraftLoginRequest);
            using var loginResponse = await httpClient.SendAsync(loginMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (loginResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            if (!loginResponse.IsSuccessStatusCode)
            {
                throw new AuthServiceException(AuthErrorKind.InvalidResponse, (int)loginResponse.StatusCode);
            }

            var loginBytes = await loginResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            var login = JsonSerializer.Deserialize(loginBytes, HakoJsonContext.Default.MinecraftLoginResponse);
            if (login?.AccessToken is not { Length: > 0 } accessToken || login.ExpiresIn <= 0)
            {
                throw new AuthServiceException(AuthErrorKind.InvalidResponse);
            }

            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, MinecraftProfile);
            profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var profileResponse = await httpClient.SendAsync(profileRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (profileResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            if (!profileResponse.IsSuccessStatusCode)
            {
                throw new AuthServiceException(AuthErrorKind.InvalidResponse, (int)profileResponse.StatusCode);
            }

            var profileBytes = await profileResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            var profile = JsonSerializer.Deserialize(profileBytes, HakoJsonContext.Default.MinecraftProfileResponse);
            if (string.IsNullOrWhiteSpace(profile?.Id) || string.IsNullOrWhiteSpace(profile.Name))
            {
                throw new AuthServiceException(AuthErrorKind.InvalidResponse);
            }

            return new MinecraftSessionData(profile, accessToken, timeProvider.GetUtcNow().AddSeconds(login.ExpiresIn));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static AuthServiceException OAuthFailure(byte[] bytes, HttpStatusCode status)
    {
        var error = TryDeserialize(bytes, HakoJsonContext.Default.OAuthErrorResponse)?.Error;
        return error switch
        {
            "authorization_declined" or "access_denied" => new AuthServiceException(AuthErrorKind.AuthorizationDeclined, (int)status),
            "expired_token" => new AuthServiceException(AuthErrorKind.CodeExpired, (int)status),
            _ => new AuthServiceException(AuthErrorKind.MicrosoftRejected, (int)status)
        };
    }

    private static AuthErrorKind ClassifyXboxError(JsonElement error)
    {
        var code = error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText();
        return code switch
        {
            "2148916233" => AuthErrorKind.XboxProfileMissing,
            "2148916235" => AuthErrorKind.XboxUnavailable,
            "2148916236" or "2148916237" => AuthErrorKind.XboxAgeVerification,
            "2148916238" => AuthErrorKind.XboxChildAccount,
            _ => AuthErrorKind.XboxRejected
        };
    }

    private static T? TryDeserialize<T>(byte[] bytes, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static HttpRequestMessage CreateJsonRequest<T>(
        Uri uri,
        T body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, typeInfo))
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string? ReadDisplayEmail(string? idToken)
    {
        try
        {
            var payload = idToken?.Split('.')[1];
            if (string.IsNullOrEmpty(payload))
            {
                return null;
            }

            var base64 = payload.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var claims = JsonSerializer.Deserialize(Convert.FromBase64String(base64), HakoJsonContext.Default.IdTokenClaims);
            return claims?.Email ?? claims?.PreferredUsername;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private sealed record MinecraftSessionData(MinecraftProfileResponse Profile, string AccessToken, DateTimeOffset ExpiresAt);
}

internal sealed record DeviceCodeResponse(
    [property: JsonPropertyName("device_code")] string? DeviceCode,
    [property: JsonPropertyName("user_code")] string? UserCode,
    [property: JsonPropertyName("verification_uri")] string? VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

internal sealed record MicrosoftTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("id_token")] string? IdToken);

internal sealed record OAuthErrorResponse(
    [property: JsonPropertyName("error")] string? Error);

internal sealed record XboxAuthRequest(
    [property: JsonPropertyName("Properties")] XboxAuthProperties Properties,
    [property: JsonPropertyName("RelyingParty")] string? RelyingParty = null,
    [property: JsonPropertyName("TokenType")] string? TokenType = null);

internal sealed record XboxAuthProperties(
    [property: JsonPropertyName("AuthMethod")] string? AuthMethod,
    [property: JsonPropertyName("SiteName")] string? SiteName,
    [property: JsonPropertyName("RpsTicket")] string? RpsTicket,
    [property: JsonPropertyName("SandboxId")] string? SandboxId,
    [property: JsonPropertyName("UserTokens")] string[]? UserTokens);

internal sealed record XboxAuthResponse(
    [property: JsonPropertyName("Token")] string? Token,
    [property: JsonPropertyName("DisplayClaims")] XboxDisplayClaims? DisplayClaims,
    [property: JsonPropertyName("XErr")] JsonElement? XErr);

internal sealed record XboxDisplayClaims([property: JsonPropertyName("xui")] XboxUserClaim[]? Xui);

internal sealed record XboxUserClaim(
    [property: JsonPropertyName("uhs")] string? UserHash,
    [property: JsonPropertyName("xid")] string? Xuid,
    [property: JsonPropertyName("gtg")] string? Gamertag);

internal sealed record XboxProfileResponse([property: JsonPropertyName("profileUsers")] XboxProfileUser[]? ProfileUsers);

internal sealed record XboxProfileUser([property: JsonPropertyName("settings")] XboxProfileSetting[]? Settings);

internal sealed record XboxProfileSetting(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("value")] string Value);

internal sealed record MinecraftLoginRequest([property: JsonPropertyName("identityToken")] string IdentityToken);

internal sealed record MinecraftLoginResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

internal sealed record MinecraftProfileResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record IdTokenClaims(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("preferred_username")] string? PreferredUsername);
