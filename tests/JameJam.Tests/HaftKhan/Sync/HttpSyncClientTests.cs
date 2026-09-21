using System.Net;
using System.Text;
using JameJam.HaftKhan;
using JameJam.Sync;

namespace JameJam.Tests.HaftKhan.Sync;

/// <summary>Tests for the hardened sync transport against a stubbed HTTP pipeline (no network).</summary>
public sealed class HttpSyncClientTests
{
    private static SyncOptions Options(string endpoint = "https://sync.test/todos.json", string? token = null) =>
        new() { Endpoint = endpoint, BearerToken = token, RetryBaseDelay = TimeSpan.FromMilliseconds(1) };

    private static string BackupJson(string uid = "uid-1") => Backup.ToJson(new Backup.BackupFile(
        Backup.CurrentVersion,
        "2026-09-19T00:00:00.0000000+00:00",
        [new Backup.TaskDto(1, "remote task", "", 1, 0, null, "2026-09-19T00:00:00.0000000+00:00", "2026-09-19T00:00:00.0000000+00:00", null, "", [], 0, 0, 1, null, uid)],
        []));

    [Fact]
    public async Task Get_ReturnsTheRemoteJson_AndSendsBearerToken()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, BackupJson()));
        using var http = new HttpClient(handler);
        var client = new HttpSyncClient(http, Options(token: "sync-token-1234"));

        var json = await client.GetAsync();

        Assert.NotNull(json);
        Assert.Equal("remote task", Assert.Single(Backup.FromJson(json).Tasks).Title);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sync-token-1234", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Get_404_ReturnsNull_EmptyRemote()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.NotFound, "{}"));
        using var http = new HttpClient(handler);
        var client = new HttpSyncClient(http, Options());

        Assert.Null(await client.GetAsync());
    }

    [Fact]
    public async Task Get_UnknownPayload_IsReturnedVerbatim_ParsingIsTheCallersJob()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, """{"hello":true}"""));
        using var http = new HttpClient(handler);
        var client = new HttpSyncClient(http, Options());

        Assert.Equal("""{"hello":true}""", await client.GetAsync());
    }

    [Fact]
    public async Task ErrorBodies_AreScrubbed_WhenTheyEchoTheToken()
    {
        var handler = new StubHandler(_ => StubHandler.Json(
            HttpStatusCode.Unauthorized, """{"error":"bad token sync-token-1234"}"""));
        using var http = new HttpClient(handler);
        var client = new HttpSyncClient(http, Options(token: "sync-token-1234"));

        var exception = await Assert.ThrowsAsync<SyncException>(() => client.GetAsync());

        Assert.Contains("****1234", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sync-token-1234", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransientFailures_AreRetried_ThenSucceed()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls == 1 ? StubHandler.Json(HttpStatusCode.ServiceUnavailable, "busy") : StubHandler.Json(HttpStatusCode.OK, BackupJson());
        });
        using var http = new HttpClient(handler);
        var client = new HttpSyncClient(http, Options());

        Assert.NotNull(await client.GetAsync());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Put_SendsTheJsonBody_WithAuthorization()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, "{}"));
        using var http = new HttpClient(handler);
        var client = new HttpSyncClient(http, Options(token: "tok"));

        await client.PutAsync(BackupJson());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("tok", request.Headers.Authorization?.Parameter);
        Assert.Contains("remote task", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedResponses_AreRejected()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, new string('x', 5_000)));
        using var http = new HttpClient(handler);
        var client = new HttpSyncClient(http, Options() with { MaxResponseBytes = 1_000 });

        var exception = await Assert.ThrowsAsync<SyncException>(() => client.GetAsync());

        Assert.Contains("exceeds the configured maximum", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsecureRemoteEndpoints_FailBeforeAnyCall()
    {
        var handler = new StubHandler(_ => StubHandler.Json(HttpStatusCode.OK, BackupJson()));
        using var http = new HttpClient(handler);
        var client = new HttpSyncClient(http, Options(endpoint: "http://sync.test/todos.json"));

        await Assert.ThrowsAsync<SyncException>(() => client.GetAsync());
        await Assert.ThrowsAsync<SyncException>(() => client.PutAsync(BackupJson()));
        Assert.Empty(handler.Requests);
    }

    /// <summary>Records every request and body, then answers with a canned response.</summary>
    public sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => this.responder = responder;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
            new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return responder(request);
        }
    }
}
