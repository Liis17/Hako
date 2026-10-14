using Hako.Core;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Hako.Tests;

[TestClass]
public sealed class MicrosoftDeviceAuthTests
{
    [TestMethod]
    public async Task DeviceCodeRequestUsesMicrosoftConsumerEndpointAndScopes()
    {
        var handler = new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode", request.RequestUri?.AbsoluteUri);
            var form = TestData.ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.AreEqual(MicrosoftDeviceAuth.ClientId, form["client_id"]);
            Assert.AreEqual("XboxLive.signin openid profile email offline_access", form["scope"]);
            return TestData.Json(HttpStatusCode.OK, """
                {"device_code":"device-secret","user_code":"ABCD-EFGH","verification_uri":"https://microsoft.com/link","expires_in":900,"interval":5}
                """);
        });
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, new ManualTimeProvider());

        var code = await auth.RequestDeviceCodeAsync(CancellationToken.None);

        Assert.AreEqual("ABCD-EFGH", code.UserCode);
        Assert.AreEqual("device-secret", code.DeviceCodeValue);
        Assert.AreEqual(900, code.ExpiresInSeconds);
        Assert.AreEqual(5, code.PollIntervalSeconds);
    }

    [TestMethod]
    public async Task PendingResponseWaitsAtIntervalThenReturnsTokenAndDisplayEmail()
    {
        var time = new ManualTimeProvider();
        var responseCount = 0;
        var handler = new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            Assert.AreEqual("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", request.RequestUri?.AbsoluteUri);
            var form = TestData.ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.AreEqual("urn:ietf:params:oauth:grant-type:device_code", form["grant_type"]);
            Assert.AreEqual(MicrosoftDeviceAuth.ClientId, form["client_id"]);
            Assert.AreEqual("device-secret", form["device_code"]);
            return TestData.Json(
                Interlocked.Increment(ref responseCount) == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.OK,
                responseCount == 1 ? "{\"error\":\"authorization_pending\"}" : TokenJson("user@example.com"));
        });
        var auth = new MicrosoftDeviceAuth(new HttpClient(handler), time);
        var task = auth.WaitForTokenAsync(TestData.DeviceCode(), CancellationToken.None);

        await TestData.WaitUntilAsync(() => time.PendingTimerCount > 0);
        time.Advance(TimeSpan.FromSeconds(5));
        await TestData.WaitUntilAsync(() => handler.RequestCount == 1);
        Assert.IsFalse(task.IsCompleted);
        await TestData.WaitUntilAsync(() => time.PendingTimerCount > 0);
        time.Advance(TimeSpan.FromSeconds(5));
        var token = await task;

        Assert.AreEqual("ms-access", token.AccessToken);
        Assert.AreEqual("ms-refresh", token.RefreshToken);
        Assert.AreEqual("user@example.com", token.Email);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task SlowDownAddsFiveSecondsToFollowingPolls()
    {
        var time = new ManualTimeProvider();
        var responseCount = 0;
        var handler = new CallbackHttpHandler((_, _) => Task.FromResult(
            Interlocked.Increment(ref responseCount) == 1
                ? TestData.Json(HttpStatusCode.BadRequest, "{\"error\":\"slow_down\"}")
                : TestData.Json(HttpStatusCode.OK, TokenJson(null))));
        var auth = new MicrosoftDeviceAuth(new HttpClient(handler), time);
        var task = auth.WaitForTokenAsync(TestData.DeviceCode(), CancellationToken.None);

        await TestData.WaitUntilAsync(() => time.PendingTimerCount > 0);
        time.Advance(TimeSpan.FromSeconds(5));
        await TestData.WaitUntilAsync(() => handler.RequestCount == 1);
        await TestData.WaitUntilAsync(() => time.PendingTimerCount > 0);
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.AreEqual(1, handler.RequestCount);
        time.Advance(TimeSpan.FromSeconds(1));
        var token = await task;

        Assert.AreEqual("ms-access", token.AccessToken);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task PollDeadlineExpiresWithoutSendingAnotherRequest()
    {
        var time = new ManualTimeProvider();
        var handler = new CallbackHttpHandler((_, _) => Task.FromResult(
            TestData.Json(HttpStatusCode.BadRequest, "{\"error\":\"authorization_pending\"}")));
        var auth = new MicrosoftDeviceAuth(new HttpClient(handler), time);
        var code = TestData.DeviceCode() with { ExpiresInSeconds = 5 };
        var task = auth.WaitForTokenAsync(code, CancellationToken.None);

        await TestData.WaitUntilAsync(() => time.PendingTimerCount > 0);
        time.Advance(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsExactlyAsync<AuthServiceException>(() => task);

        Assert.AreEqual(AuthErrorKind.CodeExpired, error.Kind);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task AuthorizationDeclinedIsReportedAsStructuredError()
    {
        var time = new ManualTimeProvider();
        var handler = new CallbackHttpHandler((_, _) => Task.FromResult(
            TestData.Json(HttpStatusCode.BadRequest, "{\"error\":\"authorization_declined\"}")));
        var auth = new MicrosoftDeviceAuth(new HttpClient(handler), time);
        var task = auth.WaitForTokenAsync(TestData.DeviceCode(), CancellationToken.None);
        await TestData.WaitUntilAsync(() => time.PendingTimerCount > 0);
        time.Advance(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsExactlyAsync<AuthServiceException>(() => task);

        Assert.AreEqual(AuthErrorKind.AuthorizationDeclined, error.Kind);
    }

    [TestMethod]
    public async Task GenericOAuthErrorDoesNotExposeServerBody()
    {
        var handler = new CallbackHttpHandler((_, _) => Task.FromResult(
            TestData.Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\",\"error_description\":\"private server detail\"}")));
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);

        var error = await Assert.ThrowsExactlyAsync<AuthServiceException>(() => auth.RequestDeviceCodeAsync(CancellationToken.None));

        Assert.AreEqual(AuthErrorKind.MicrosoftRejected, error.Kind);
        Assert.IsFalse(error.Message.Contains("private server detail", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LargeXboxXErrDoesNotOverflowAndStopsMicrosoftSignIn()
    {
        var handler = new CallbackHttpHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsoluteUri == "https://user.auth.xboxlive.com/user/authenticate"
                ? TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}")
                : TestData.Json(HttpStatusCode.Unauthorized, "{\"XErr\":9223372036854775807}")));
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);

        var error = await Assert.ThrowsExactlyAsync<AuthServiceException>(() =>
            auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", null), CancellationToken.None));

        Assert.AreEqual(AuthErrorKind.XboxRejected, error.Kind);
        Assert.AreEqual(401, error.HttpStatus);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task MissingXboxProfileHasSpecificError()
    {
        var handler = new CallbackHttpHandler((request, _) => Task.FromResult(
            request.RequestUri?.AbsoluteUri == "https://user.auth.xboxlive.com/user/authenticate"
                ? TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}")
                : TestData.Json(HttpStatusCode.Unauthorized, "{\"XErr\":2148916233}")));
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);

        var error = await Assert.ThrowsExactlyAsync<AuthServiceException>(() =>
            auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", null), CancellationToken.None));

        Assert.AreEqual(AuthErrorKind.XboxProfileMissing, error.Kind);
    }

    [TestMethod]
    public async Task AvatarFailureAndMinecraft403StillReturnXboxAccount()
    {
        var handler = Sequence(
            TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}"),
            TestData.Json(HttpStatusCode.OK, XboxProfileTokenJson()),
            TestData.Json(HttpStatusCode.ServiceUnavailable, "{}"),
            TestData.Json(HttpStatusCode.Forbidden, "{\"XErr\":2148916233}"));
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);

        var account = await auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", "display@example.com"), CancellationToken.None);

        Assert.AreEqual("123", account.Xuid);
        Assert.AreEqual("HakoPlayer", account.Gamertag);
        Assert.AreEqual("display@example.com", account.Email);
        Assert.IsNull(account.XboxAvatarUrl);
        Assert.IsNull(account.MinecraftName);
        Assert.AreEqual(4, handler.RequestCount);
    }

    [TestMethod]
    public async Task MinecraftProfile404IsOptionalAndPreservesXboxAvatar()
    {
        var handler = Sequence(
            TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}"),
            TestData.Json(HttpStatusCode.OK, XboxProfileTokenJson()),
            TestData.Json(HttpStatusCode.OK, "{\"profileUsers\":[{\"settings\":[{\"id\":\"GameDisplayPicRaw\",\"value\":\"https://example.com/avatar.png\"}]}]}"),
            TestData.Json(HttpStatusCode.OK, "{\"Token\":\"minecraft-xsts\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"user-hash\"}]}}"),
            TestData.Json(HttpStatusCode.OK, "{\"access_token\":\"minecraft-access\",\"expires_in\":3600}"),
            TestData.Json(HttpStatusCode.NotFound, "{}"));
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);

        var account = await auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", null), CancellationToken.None);

        Assert.AreEqual("https://example.com/avatar.png", account.XboxAvatarUrl);
        Assert.IsNull(account.MinecraftProfileId);
        Assert.IsNull(account.MinecraftName);
        Assert.IsNull(account.MinecraftAccessToken);
        Assert.AreEqual(6, handler.RequestCount);
    }

    [TestMethod]
    public async Task MinecraftNetworkFailureIsOptional()
    {
        var requestNumber = 0;
        var handler = new CallbackHttpHandler((request, _) =>
        {
            var currentRequest = Interlocked.Increment(ref requestNumber);
            if (request.RequestUri?.AbsoluteUri == "https://xsts.auth.xboxlive.com/xsts/authorize" && currentRequest == 4)
            {
                throw new HttpRequestException("simulated network failure");
            }

            return Task.FromResult(ResponseFor(request,
                TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}"),
                TestData.Json(HttpStatusCode.OK, XboxProfileTokenJson()),
                TestData.Json(HttpStatusCode.ServiceUnavailable, "{}"),
                TestData.Json(HttpStatusCode.OK, "{}"),
                TestData.Json(HttpStatusCode.Forbidden, "{\"XErr\":2148916233}")));
        });
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);

        var account = await auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", null), CancellationToken.None);

        Assert.AreEqual("123", account.Xuid);
        Assert.IsNull(account.MinecraftName);
    }

    [TestMethod]
    public async Task XboxAndMinecraftRequestsUseExpectedProtocolFields()
    {
        var time = new ManualTimeProvider();
        var xstsRequest = 0;
        var handler = new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            var uri = request.RequestUri?.AbsoluteUri;
            if (uri == "https://user.auth.xboxlive.com/user/authenticate")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var root = body.RootElement;
                Assert.AreEqual("RPS", root.GetProperty("Properties").GetProperty("AuthMethod").GetString());
                Assert.AreEqual("user.auth.xboxlive.com", root.GetProperty("Properties").GetProperty("SiteName").GetString());
                Assert.AreEqual("d=ms-access", root.GetProperty("Properties").GetProperty("RpsTicket").GetString());
                Assert.AreEqual("http://auth.xboxlive.com", root.GetProperty("RelyingParty").GetString());
                Assert.AreEqual("JWT", root.GetProperty("TokenType").GetString());
                return TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}");
            }

            if (uri == "https://xsts.auth.xboxlive.com/xsts/authorize")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var root = body.RootElement;
                Assert.AreEqual("RETAIL", root.GetProperty("Properties").GetProperty("SandboxId").GetString());
                Assert.AreEqual("user-token", root.GetProperty("Properties").GetProperty("UserTokens")[0].GetString());
                Assert.AreEqual(
                    Interlocked.Increment(ref xstsRequest) == 1 ? "http://xboxlive.com" : "rp://api.minecraftservices.com/",
                    root.GetProperty("RelyingParty").GetString());
                Assert.AreEqual("JWT", root.GetProperty("TokenType").GetString());
                return xstsRequest == 1
                    ? TestData.Json(HttpStatusCode.OK, XboxProfileTokenJson())
                    : TestData.Json(HttpStatusCode.OK, "{\"Token\":\"minecraft-xsts\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"user-hash\"}]}}");
            }

            if (uri?.StartsWith("https://profile.xboxlive.com/", StringComparison.Ordinal) == true)
            {
                Assert.AreEqual("XBL3.0", request.Headers.Authorization?.Scheme);
                Assert.AreEqual("x=user-hash;xbox-xsts", request.Headers.Authorization?.Parameter);
                Assert.AreEqual("3", request.Headers.GetValues("x-xbl-contract-version").Single());
                return TestData.Json(HttpStatusCode.OK, "{\"profileUsers\":[{\"settings\":[{\"id\":\"GameDisplayPicRaw\",\"value\":\"https://example.com/avatar.png\"}]}]}");
            }

            if (uri == "https://api.minecraftservices.com/authentication/login_with_xbox")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.AreEqual("XBL3.0 x=user-hash;minecraft-xsts", body.RootElement.GetProperty("identityToken").GetString());
                return TestData.Json(HttpStatusCode.OK, "{\"access_token\":\"minecraft-access\",\"expires_in\":3600}");
            }

            if (uri == "https://api.minecraftservices.com/minecraft/profile")
            {
                Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
                Assert.AreEqual("minecraft-access", request.Headers.Authorization?.Parameter);
                return TestData.Json(HttpStatusCode.OK, "{\"id\":\"profile-id\",\"name\":\"JavaPlayer\"}");
            }

            throw new AssertFailedException("Unexpected authentication endpoint.");
        });
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, time);

        var account = await auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", null), CancellationToken.None);

        Assert.AreEqual("123", account.Xuid);
        Assert.AreEqual("HakoPlayer", account.Gamertag);
        Assert.AreEqual("JavaPlayer", account.MinecraftName);
        Assert.AreEqual("profile-id", account.MinecraftProfileId);
        Assert.AreEqual("minecraft-access", account.MinecraftAccessToken);
        Assert.AreEqual("https://example.com/avatar.png", account.XboxAvatarUrl);
        Assert.AreEqual(time.GetUtcNow().AddHours(1), account.MinecraftTokenExpiresAt);
    }

    [TestMethod]
    public async Task CancellationStopsAnInFlightHttpRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackHttpHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TestData.Json(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var task = auth.RequestDeviceCodeAsync(cancellation.Token);

        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task CancellationStopsPollingDelayBeforeAnyHttpPoll()
    {
        var time = new ManualTimeProvider();
        var handler = new CallbackHttpHandler((_, _) => Task.FromResult(TestData.Json(HttpStatusCode.OK, "{}")));
        var auth = new MicrosoftDeviceAuth(new HttpClient(handler), time);
        using var cancellation = new CancellationTokenSource();
        var task = auth.WaitForTokenAsync(TestData.DeviceCode(), cancellation.Token);
        await TestData.WaitUntilAsync(() => time.PendingTimerCount > 0);

        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task CancellationDuringMinecraftProfileRequestIsNotSwallowed()
    {
        var profileRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestNumber = 0;
        var handler = new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            var currentRequest = Interlocked.Increment(ref requestNumber);
            var uri = request.RequestUri?.AbsoluteUri;
            if (uri == "https://user.auth.xboxlive.com/user/authenticate")
                return TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}");
            if (uri == "https://xsts.auth.xboxlive.com/xsts/authorize" && currentRequest == 2)
                return TestData.Json(HttpStatusCode.OK, XboxProfileTokenJson());
            if (uri == "https://profile.xboxlive.com/users/me/profile/settings?settings=GameDisplayPicRaw")
                return TestData.Json(HttpStatusCode.ServiceUnavailable, "{}");
            if (uri == "https://xsts.auth.xboxlive.com/xsts/authorize" && currentRequest == 4)
                return TestData.Json(HttpStatusCode.OK, "{\"Token\":\"minecraft-xsts\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"user-hash\"}]}}");
            if (uri == "https://api.minecraftservices.com/authentication/login_with_xbox")
                return TestData.Json(HttpStatusCode.OK, "{\"access_token\":\"minecraft-access\",\"expires_in\":3600}");
            profileRequest.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TestData.Json(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var task = auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", null), cancellation.Token);

        await profileRequest.Task;
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
    }

    [TestMethod]
    public async Task CancellationDuringOptionalAvatarRequestIsNotSwallowed()
    {
        var avatarRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            var uri = request.RequestUri?.AbsoluteUri;
            if (uri == "https://user.auth.xboxlive.com/user/authenticate")
                return TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}");
            if (uri == "https://xsts.auth.xboxlive.com/xsts/authorize")
                return TestData.Json(HttpStatusCode.OK, XboxProfileTokenJson());
            avatarRequest.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return TestData.Json(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var task = auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", null), cancellation.Token);

        await avatarRequest.Task;
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => task);
    }

    [TestMethod]
    public async Task AvatarTimeoutIsOptionalLikeOtherNetworkFailures()
    {
        var handler = new CallbackHttpHandler((request, _) =>
        {
            var uri = request.RequestUri?.AbsoluteUri;
            if (uri == "https://user.auth.xboxlive.com/user/authenticate")
                return Task.FromResult(TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}"));
            if (uri == "https://xsts.auth.xboxlive.com/xsts/authorize")
                return Task.FromResult(TestData.Json(HttpStatusCode.OK, XboxProfileTokenJson()));
            if (uri?.StartsWith("https://profile.xboxlive.com/", StringComparison.Ordinal) == true)
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout", new TimeoutException()));
            return Task.FromResult(TestData.Json(HttpStatusCode.Forbidden, "{}"));
        });
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);

        var account = await auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", null), CancellationToken.None);

        Assert.AreEqual("123", account.Xuid);
        Assert.IsNull(account.XboxAvatarUrl);
    }

    [TestMethod]
    public async Task MinecraftTimeoutIsOptionalLikeOtherNetworkFailures()
    {
        var handler = new CallbackHttpHandler((request, _) =>
        {
            var uri = request.RequestUri?.AbsoluteUri;
            if (uri == "https://user.auth.xboxlive.com/user/authenticate")
                return Task.FromResult(TestData.Json(HttpStatusCode.OK, "{\"Token\":\"user-token\",\"DisplayClaims\":{\"xui\":[]}}"));
            if (uri == "https://xsts.auth.xboxlive.com/xsts/authorize")
                return Task.FromResult(TestData.Json(HttpStatusCode.OK, XboxProfileTokenJson()));
            if (uri == "https://api.minecraftservices.com/authentication/login_with_xbox")
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout", new TimeoutException()));
            return Task.FromResult(TestData.Json(HttpStatusCode.ServiceUnavailable, "{}"));
        });
        using var http = new HttpClient(handler);
        var auth = new MicrosoftDeviceAuth(http, TimeProvider.System);

        var account = await auth.SignInAsync(new MicrosoftToken("ms-access", "ms-refresh", null), CancellationToken.None);

        Assert.AreEqual("123", account.Xuid);
        Assert.IsNull(account.MinecraftName);
    }

    private static CallbackHttpHandler Sequence(params HttpResponseMessage[] responses)
    {
        var index = -1;
        return new CallbackHttpHandler((_, _) => Task.FromResult(responses[Interlocked.Increment(ref index)]));
    }

    private static HttpResponseMessage ResponseFor(HttpRequestMessage request, params HttpResponseMessage[] responses)
    {
        var uri = request.RequestUri?.AbsoluteUri;
        var index = uri switch
        {
            "https://user.auth.xboxlive.com/user/authenticate" => 0,
            "https://xsts.auth.xboxlive.com/xsts/authorize" => 1,
            _ => 2
        };
        return responses[Math.Clamp(index, 0, responses.Length - 1)];
    }

    private static string XboxProfileTokenJson() =>
        "{\"Token\":\"xbox-xsts\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"user-hash\",\"xid\":\"123\",\"gtg\":\"HakoPlayer\"}]}}";

    private static string TokenJson(string? email)
    {
        var claims = email is null ? "{}" : "{\"email\":\"" + email + "\"}";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "{\"access_token\":\"ms-access\",\"refresh_token\":\"ms-refresh\",\"id_token\":\"header." + payload + ".signature\"}";
    }
}
