using Hako.Core;
using System.Net;

namespace Hako.Tests;

[TestClass]
public sealed class HakoAuthApiTests
{
    [TestMethod]
    public async Task RegistrationUsesSnakeCaseAndKeepsExchangeSecretOutOfDiagnostics()
    {
        HttpRequestMessage? observedRequest = null;
        string? observedBody = null;
        using var http = new HttpClient(new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            observedRequest = request;
            observedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(HttpStatusCode.Created, """
                {"request_id":"8aabf9c0-a4b4-4b4d-a0fc-a9e8e753bdf1","bot_link":"https://t.me/hako_bot?start=abc","exchange_secret":"private-exchange-value","expires_at":"2025-01-01T00:05:00Z"}
                """);
        }));
        var client = new HakoAuthApiClient(http, new Uri("https://hako.liis17.com:443/"));

        var request = await client.RegisterAsync("Gaming PC", CancellationToken.None);

        Assert.AreEqual(HttpMethod.Post, observedRequest?.Method);
        Assert.AreEqual("https://hako.liis17.com/v1/auth/register", observedRequest?.RequestUri?.AbsoluteUri);
        using (var body = System.Text.Json.JsonDocument.Parse(observedBody!))
        {
            Assert.AreEqual("Gaming PC", body.RootElement.GetProperty("device_name").GetString());
            Assert.AreEqual(1, body.RootElement.EnumerateObject().Count());
        }
        Assert.AreEqual(new Guid("8aabf9c0-a4b4-4b4d-a0fc-a9e8e753bdf1"), request.RequestId);
        Assert.AreEqual("https://t.me/hako_bot?start=abc", request.BotLink?.AbsoluteUri);
        Assert.IsFalse(request.ToString().Contains("private-exchange-value", StringComparison.Ordinal));
        Assert.IsFalse(request.ToString().Contains("start=abc", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LoginSendsNormalizedUsernameAndDeviceName()
    {
        string? observedBody = null;
        using var http = new HttpClient(new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            observedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(HttpStatusCode.Created, """
                {"request_id":"8aabf9c0-a4b4-4b4d-a0fc-a9e8e753bdf1","exchange_secret":"private-exchange-value","expires_at":"2025-01-01T00:05:00Z"}
                """);
        }));
        var client = new HakoAuthApiClient(http, new Uri("https://hako.liis17.com/"));

        await client.LoginAsync("@Player_123", "Gaming PC", CancellationToken.None);

        using var body = System.Text.Json.JsonDocument.Parse(observedBody!);
        Assert.AreEqual("Player_123", body.RootElement.GetProperty("username").GetString());
        Assert.AreEqual("Gaming PC", body.RootElement.GetProperty("device_name").GetString());
    }

    [TestMethod]
    public async Task RateLimitAndCloudflareHtmlHaveLocalizedProtocolErrors()
    {
        using var limitedHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            JsonResponse((HttpStatusCode)429, "{\"error\":{\"code\":\"rate_limited\",\"message\":\"slow down\"}}"))));
        var limited = new HakoAuthApiClient(limitedHttp, new Uri("https://hako.liis17.com/"));
        var limitedError = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => limited.RegisterAsync("Gaming PC", CancellationToken.None));
        Assert.AreEqual(HakoAuthErrorKind.RateLimited, limitedError.Kind);

        using var cloudflareHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("<html>cloudflare</html>", System.Text.Encoding.UTF8, "text/html")
            })));
        var cloudflare = new HakoAuthApiClient(cloudflareHttp, new Uri("https://hako.liis17.com/"));
        var cloudflareError = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => cloudflare.RegisterAsync("Gaming PC", CancellationToken.None));
        Assert.AreEqual(HakoAuthErrorKind.ServiceUnavailable, cloudflareError.Kind);

        using var forbiddenHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html>cloudflare</html>", System.Text.Encoding.UTF8, "text/html")
            })));
        var forbidden = new HakoAuthApiClient(forbiddenHttp, new Uri("https://hako.liis17.com/"));
        var forbiddenError = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => forbidden.RegisterAsync("Gaming PC", CancellationToken.None));
        Assert.AreEqual(HakoAuthErrorKind.InvalidResponse, forbiddenError.Kind);

        using var deniedHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"request_denied\",\"message\":\"denied\"}}"))));
        var denied = new HakoAuthApiClient(deniedHttp, new Uri("https://hako.liis17.com/"));
        var deniedError = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => denied.RegisterAsync("Gaming PC", CancellationToken.None));
        Assert.AreEqual(HakoAuthErrorKind.AuthorizationDenied, deniedError.Kind);
    }

    [TestMethod]
    public async Task UnknownUsernameUsesServerErrorCodeWhileUnclassifiedNotFoundIsInvalidResponse()
    {
        using var accountNotFoundHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.NotFound, "{\"error\":{\"code\":\"account_not_found\",\"message\":\"missing\"}}"))));
        var accountNotFoundClient = new HakoAuthApiClient(accountNotFoundHttp, new Uri("https://hako.liis17.com/"));
        var accountNotFound = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => accountNotFoundClient.LoginAsync("Player_123", "Gaming PC", CancellationToken.None));
        Assert.AreEqual(HakoAuthErrorKind.AccountNotFound, accountNotFound.Kind);

        using var missingRouteHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found", System.Text.Encoding.UTF8, "text/plain")
            })));
        var missingRouteClient = new HakoAuthApiClient(missingRouteHttp, new Uri("https://hako.liis17.com/"));
        var missingRoute = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => missingRouteClient.RegisterAsync("Gaming PC", CancellationToken.None));
        Assert.AreEqual(HakoAuthErrorKind.InvalidResponse, missingRoute.Kind);
    }

    [TestMethod]
    public async Task PollAndExchangeUseBearerSecretAndExplicitSnakeCaseRequestId()
    {
        var id = Guid.Parse("8aabf9c0-a4b4-4b4d-a0fc-a9e8e753bdf1");
        var calls = new List<(HttpMethod Method, string Path, string? Scheme, string? Credential, string? Body)>();
        using var http = new HttpClient(new CallbackHttpHandler(async (request, cancellationToken) =>
        {
            var requestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            calls.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter, requestBody));
            var body = request.Method == HttpMethod.Get
                ? """{"status":"approved","expires_at":"2025-01-01T00:05:00Z"}"""
                : """{"token":"private-session-value","token_expires_at":"2025-01-15T00:00:00Z","account":{"id":"account-id","username":"Player_123","created_at":"2024-11-01T00:00:00Z"}}""";
            return JsonResponse(HttpStatusCode.OK, body);
        }));
        var client = new HakoAuthApiClient(http, new Uri("https://hako.liis17.com/"));
        var pending = new HakoPendingAuthRequest(id, "private-exchange-value", DateTimeOffset.Parse("2025-01-01T00:05:00Z"), null);

        var status = await client.GetRequestStatusAsync(pending, CancellationToken.None);
        var exchange = await client.ExchangeAsync(pending, CancellationToken.None);

        Assert.AreEqual(HakoRequestStatus.Approved, status.Status);
        Assert.AreEqual("GET", calls[0].Method.Method);
        Assert.AreEqual("/v1/auth/requests/8aabf9c0-a4b4-4b4d-a0fc-a9e8e753bdf1", calls[0].Path);
        Assert.AreEqual("Bearer", calls[0].Scheme);
        Assert.AreEqual("private-exchange-value", calls[0].Credential);
        Assert.AreEqual("POST", calls[1].Method.Method);
        Assert.AreEqual("Bearer", calls[1].Scheme);
        using var body = System.Text.Json.JsonDocument.Parse(calls[1].Body!);
        Assert.AreEqual(id.ToString(), body.RootElement.GetProperty("request_id").GetString());
        Assert.AreEqual("Player_123", exchange.Account.Username);
        Assert.IsFalse(exchange.ToString().Contains("private-session-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task MeRequiresCompleteAccountAndSessionExpiry()
    {
        using var validHttp = new HttpClient(new CallbackHttpHandler((request, _) =>
        {
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("private-session-value", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK,
                """{"account":{"id":"account-id","username":"Player_123","created_at":"2024-11-01T00:00:00Z"},"session":{"device_name":"Gaming PC","expires_at":"2027-01-15T00:00:00Z"}}"""));
        }));
        var validClient = new HakoAuthApiClient(validHttp, new Uri("https://hako.liis17.com/"));
        var me = await validClient.GetMeAsync("private-session-value", CancellationToken.None);
        Assert.AreEqual("Player_123", me.Account.Username);
        Assert.AreEqual("Gaming PC", me.DeviceName);
        Assert.AreEqual(DateTimeOffset.Parse("2027-01-15T00:00:00Z"), me.SessionExpiresAt);

        using var missingExpiryHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.OK,
            """{"account":{"id":"account-id","username":"Player_123","created_at":"2024-11-01T00:00:00Z"},"session":{"device_name":"Gaming PC"}}"""))));
        var missingExpiryClient = new HakoAuthApiClient(missingExpiryHttp, new Uri("https://hako.liis17.com/"));
        var error = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => missingExpiryClient.GetMeAsync("private-session-value", CancellationToken.None));
        Assert.AreEqual(HakoAuthErrorKind.InvalidResponse, error.Kind);
    }

    [TestMethod]
    public void HakoSessionTokensStayOutOfRecordDiagnostics()
    {
        var account = new HakoAccount("account-id", "Player_123", DateTimeOffset.UtcNow);
        var session = new HakoSession("private-session-value", account, "Gaming PC", DateTimeOffset.UtcNow.AddDays(1), null);

        Assert.IsFalse(session.ToString().Contains("private-session-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RegistrationRejectsUntrustedTelegramLinksAndRedirects()
    {
        using var invalidLinkHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(JsonResponse(
            HttpStatusCode.Created,
            """{"request_id":"8aabf9c0-a4b4-4b4d-a0fc-a9e8e753bdf1","bot_link":"https://evil.example/start?start=abc","exchange_secret":"private","expires_at":"2025-01-01T00:05:00Z"}"""))));
        var invalidLink = new HakoAuthApiClient(invalidLinkHttp, new Uri("https://hako.liis17.com/"));
        var invalidLinkError = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => invalidLink.RegisterAsync("Gaming PC", CancellationToken.None));
        Assert.AreEqual(HakoAuthErrorKind.InvalidResponse, invalidLinkError.Kind);

        using var redirectHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Content = new StringContent("", System.Text.Encoding.UTF8, "text/plain")
            })));
        var redirectClient = new HakoAuthApiClient(redirectHttp, new Uri("https://hako.liis17.com/"));
        var redirectError = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => redirectClient.RegisterAsync("Gaming PC", CancellationToken.None));
        Assert.AreEqual(HakoAuthErrorKind.RedirectRejected, redirectError.Kind);

        using var defaultHandler = HakoAuthApiClient.CreateDefaultHandler();
        Assert.IsFalse(defaultHandler.AllowAutoRedirect);
        Assert.IsNull(defaultHandler.ServerCertificateCustomValidationCallback);
        Assert.AreEqual(443, HakoAuthApiClient.ServiceBaseAddress.Port);
    }

    [TestMethod]
    public async Task HttpTimeoutBecomesLocalizedTimeoutError()
    {
        using var http = new HttpClient(new CallbackHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }))
        {
            Timeout = TimeSpan.FromMilliseconds(30)
        };
        var client = new HakoAuthApiClient(http, new Uri("https://hako.liis17.com/"));

        var error = await Assert.ThrowsExactlyAsync<HakoAuthException>(
            () => client.RegisterAsync("Gaming PC", CancellationToken.None));

        Assert.AreEqual(HakoAuthErrorKind.Timeout, error.Kind);
    }

    [TestMethod]
    public async Task OperationDeadlineCoversResponseBodyAndCallerCancellationIsPreserved()
    {
        var timeoutStream = new StallingReadStream();
        using var timeoutHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            StallingResponse(HttpStatusCode.Created, timeoutStream))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var timeoutClient = new HakoAuthApiClient(
            timeoutHttp,
            new Uri("https://hako.liis17.com/"),
            TimeSpan.FromMilliseconds(50));

        var timedRequest = timeoutClient.RegisterAsync("Gaming PC", CancellationToken.None);
        await timeoutStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var timeoutError = await Assert.ThrowsExactlyAsync<HakoAuthException>(() => timedRequest);
        Assert.AreEqual(HakoAuthErrorKind.Timeout, timeoutError.Kind);

        var cancelledStream = new StallingReadStream();
        using var cancelledHttp = new HttpClient(new CallbackHttpHandler((_, _) => Task.FromResult(
            StallingResponse(HttpStatusCode.Created, cancelledStream))))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var cancelledClient = new HakoAuthApiClient(
            cancelledHttp,
            new Uri("https://hako.liis17.com/"),
            TimeSpan.FromSeconds(5));
        using var callerCancellation = new CancellationTokenSource();
        var cancelledRequest = cancelledClient.RegisterAsync("Gaming PC", callerCancellation.Token);
        await cancelledStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();
        try
        {
            await cancelledRequest;
            Assert.Fail("Caller cancellation should stop the response body read.");
        }
        catch (OperationCanceledException exception)
        {
            Assert.IsNotInstanceOfType(exception, typeof(HakoAuthException));
        }
    }

    [TestMethod]
    public void TelegramUsernameAcceptsOptionalAtButRequiresAsciiProtocolLength()
    {
        Assert.IsTrue(HakoUsername.TryNormalize(" @Player_123 ", out var normalized));
        Assert.AreEqual("Player_123", normalized);
        Assert.IsFalse(HakoUsername.TryNormalize("abcd", out _));
        Assert.IsFalse(HakoUsername.TryNormalize("PlayerÄ", out _));
        Assert.IsFalse(HakoUsername.TryNormalize(new string('a', 33), out _));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage StallingResponse(HttpStatusCode status, Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return new HttpResponseMessage(status) { Content = content };
    }
}
