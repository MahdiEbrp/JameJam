using System.Net;
using System.Net.Http.Headers;
using System.Text;
using JameJam.Soroush;

namespace JameJam.Tests.Soroush;

/// <summary>Tests for <see cref="SoroushClient"/> against a stubbed HTTP pipeline (no real network).</summary>
public sealed class SoroushClientTests
{
    [Fact]
    public async Task OpenAICompatible_BuildsAuthRequest_AndParsesContent()
    {
        var handler = new StubHandler(_ => JsonResponse(
            HttpStatusCode.OK, """{"choices":[{"message":{"content":"  hi from ai  "}}]}"""));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions());

        var result = await client.CompleteAsync("  Hello\u0007AI  ");

        Assert.Equal("hi from ai", result.Content);
        Assert.Equal("openai", result.Provider);
        Assert.Equal("test-model", result.Model);
        Assert.Equal(1, result.Attempts);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ai.test/v1/chat/completions", request.RequestUri?.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key-1234", request.Headers.Authorization?.Parameter);
        Assert.Contains("test-model", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("HelloAI", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_SendsAnthropicHeaders_AndParsesContentBlocks()
    {
        var handler = new StubHandler(_ => JsonResponse(
            HttpStatusCode.OK, """{"content":[{"type":"text","text":"claude says hi"}]}"""));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions(provider: "anthropic"));

        var result = await client.CompleteAsync("hi");

        Assert.Equal("claude says hi", result.Content);
        Assert.Equal("anthropic", result.Provider);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("test-key-1234", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task TransientFailure_IsRetriedThenSucceeds()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls == 1 ? RateLimited() : JsonResponse(
                HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}""");
        });
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions());

        var result = await client.CompleteAsync("hi");

        Assert.Equal("ok", result.Content);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PerAttemptTimeout_IsRetried_ThenFailsWithTimeoutError()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            // Wait until the attempt budget cancels us — no race with a fixed delay.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(HttpStatusCode.OK, "{}");
        });
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions() with
        {
            RequestTimeout = TimeSpan.FromMilliseconds(10),
            MaxRetries = 1,
        });

        var exception = await Assert.ThrowsAsync<SoroushException>(() => client.CompleteAsync("hi"));

        Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task NonRetryableStatus_FailsImmediately()
    {
        var handler = new StubHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions());

        var exception = await Assert.ThrowsAsync<SoroushException>(() => client.CompleteAsync("hi"));

        Assert.Equal(401, exception.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ApiErrorBody_IsScrubbed_WhenItEchoesTheApiKey()
    {
        var handler = new StubHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized, """{"error":"invalid key test-key-1234 for model"}"""));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions());

        var exception = await Assert.ThrowsAsync<SoroushException>(() => client.CompleteAsync("hi"));

        Assert.Contains("****1234", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key-1234", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkErrors_AreRetriedUntilExhausted()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions());

        var exception = await Assert.ThrowsAsync<SoroushException>(() => client.CompleteAsync("hi"));

        Assert.Contains("Network error", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count); // 1 initial + MaxRetries(2)
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    [Fact]
    public async Task EmptyPrompt_FailsWithoutAnyHttpCall()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, "{}"));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteAsync("   "));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InsecureRemoteEndpoint_FailsWithoutAnyHttpCall()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, "{}"));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions() with { Endpoint = "http://api.example.com/v1" });

        var exception = await Assert.ThrowsAsync<SoroushException>(() => client.CompleteAsync("hi"));

        Assert.Contains("Insecure AI endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidOptions_FailFast_BeforeAnyHttpCall()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, "{}"));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions() with { MaxTokens = 0 });

        var exception = await Assert.ThrowsAsync<SoroushException>(() => client.CompleteAsync("hi"));

        Assert.Contains("MaxTokens", exception.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RetryableStatusCodes_AreCustomizable()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? JsonResponse(HttpStatusCode.Forbidden, """{"error":"nope"}""")
                : JsonResponse(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}""");
        });
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions() with
        {
            RetryableStatusCodes = new HashSet<HttpStatusCode> { HttpStatusCode.Forbidden },
        });

        var result = await client.CompleteAsync("hi");

        Assert.Equal(2, result.Attempts); // 403 retried because the custom set says so
    }

    [Fact]
    public async Task RetryableStatusCodes_CanBeEmptied_TransientsThenFailFast()
    {
        var handler = new StubHandler(_ => RateLimited());
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions() with
        {
            RetryableStatusCodes = new HashSet<HttpStatusCode>(),
        });

        var exception = await Assert.ThrowsAsync<SoroushException>(() => client.CompleteAsync("hi"));

        Assert.Equal(429, exception.StatusCode);
        Assert.Single(handler.Requests); // default would have retried twice
    }

    [Fact]
    public async Task MaxErrorBodyLength_IsConfigurable()
    {
        var handler = new StubHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized, string.Concat(Enumerable.Repeat("a", 80))));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions() with { MaxErrorBodyLength = 60 });

        var exception = await Assert.ThrowsAsync<SoroushException>(() => client.CompleteAsync("hi"));

        Assert.Contains(new string('a', 60) + "…", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 80), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedSuccessBody_ProducesFriendlyError()
    {
        var handler = new StubHandler(_ => JsonResponse(HttpStatusCode.OK, """{"unexpected":true}"""));
        using var http = new HttpClient(handler);
        var client = new SoroushClient(http, TestOptions());

        var exception = await Assert.ThrowsAsync<SoroushException>(() => client.CompleteAsync("hi"));

        Assert.Contains("unexpected OpenAI-compatible response shape", exception.Message, StringComparison.Ordinal);
    }

    private static SoroushOptions TestOptions(string provider = "openai", string endpoint = "https://ai.test/v1/chat/completions") => new()
    {
        Provider = provider,
        Endpoint = endpoint,
        ApiKey = "test-key-1234",
        Model = "test-model",
        MaxRetries = 2,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RequestTimeout = TimeSpan.FromSeconds(5),
    };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage RateLimited()
    {
        var response = JsonResponse(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    /// <summary>Records every request and body, then answers with a canned response.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request)))
        {
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
            this.responder = responder;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return await responder(request, cancellationToken);
        }
    }
}
