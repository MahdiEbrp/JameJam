using System.Diagnostics;
using System.Net;
using System.Text;

using JameJam.Soroush;

namespace JameJam.Sync;

/// <summary>
/// Hardened HTTP transport for remote sync: HTTPS/loopback policy, optional bearer token
/// (from the environment only), per-attempt timeouts, retries with jittered backoff honoring
/// Retry-After, a hard response-size cap, and token scrubbing from every error body.
/// </summary>
/// <param name="http">The HTTP pipeline to use. Injected for testability.</param>
/// <param name="options">Validated sync options.</param>
public sealed class HttpSyncClient(HttpClient http, SyncOptions options) : ISyncClient
{
    private const string TruncationSuffix = "…";
    private const double JitterScale = 0.5;

    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes = new()
    {
        HttpStatusCode.RequestTimeout,        // 408
        HttpStatusCode.TooManyRequests,       // 429
        HttpStatusCode.InternalServerError,   // 500
        HttpStatusCode.BadGateway,            // 502
        HttpStatusCode.ServiceUnavailable,    // 503
        HttpStatusCode.GatewayTimeout,        // 504
    };

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly SyncOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();
        var endpoint = new Uri(options.Endpoint);
        using var response = await SendWithRetriesAsync(() => BuildRequest(HttpMethod.Get, endpoint), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        EnsureSuccess(response);

        return await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PutAsync(string json, CancellationToken cancellationToken = default)
    {
        options.Validate();
        var endpoint = new Uri(options.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var response = await SendWithRetriesAsync(
            () =>
            {
                var request = BuildRequest(HttpMethod.Put, endpoint);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return request;
            },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, Uri endpoint)
    {
        var request = new HttpRequestMessage(method, endpoint);
        if (!string.IsNullOrEmpty(_options.BearerToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.BearerToken);

        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        var maxAttempts = _options.MaxRetries + 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A fresh request per attempt — HttpRequestMessage cannot be sent twice.
                using var request = requestFactory();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode
                    || !RetryableStatusCodes.Contains(response.StatusCode)
                    || attempt >= maxAttempts)
                {
                    return response;
                }

                using (response)
                {
                    await DelayBeforeRetryAsync(response.Headers.RetryAfter?.Delta, attempt, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= maxAttempts)
                    throw new SyncException($"Sync request timed out after {attempt} attempt(s) without success.");

                await DelayBeforeRetryAsync(null, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await DelayBeforeRetryAsync(null, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new SyncException($"Network error during sync: {ex.Message}", innerException: ex);
            }
        }
    }

    private async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var budget = _options.MaxResponseBytes + 1;
        var content = response.Content;
        if (content.Headers.ContentLength is > SyncDefaults.MaxResponseBytesBound)
            throw new SyncException("The remote response is too large.");

        var buffer = new StringBuilder();
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using (stream)
        {
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                budget -= read;
                if (budget < 0)
                    throw new SyncException(
                        $"The remote response exceeds the configured maximum of {_options.MaxResponseBytes} bytes.");

                _ = buffer.Append(Encoding.UTF8.GetString(chunk, 0, read));
            }
        }

        return buffer.ToString();
    }

    private void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = response.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (body.Length > 500)
            body = body[..500] + TruncationSuffix;

        body = SoroushGuard.RedactIn(body, _options.BearerToken);
        throw new SyncException(
            $"Sync remote returned {(int)response.StatusCode} ({response.StatusCode}). Body: {body}",
            (int)response.StatusCode);
    }

    private async Task DelayBeforeRetryAsync(TimeSpan? retryAfter, int attempt, CancellationToken cancellationToken)
    {
        var backoff = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jittered = backoff * (1 - JitterScale + (2 * JitterScale * Random.Shared.NextDouble()));
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(jittered);

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
