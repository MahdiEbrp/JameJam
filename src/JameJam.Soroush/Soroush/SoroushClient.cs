using System.Diagnostics;
using System.Net;
using JameJam.Soroush.Providers;

namespace JameJam.Soroush;

/// <summary>
/// HTTP client for AI providers with the safety layer built in:
/// options validation, prompt sanitization, endpoint validation, per-attempt timeouts,
/// and retries with jittered exponential backoff (honoring Retry-After).
/// Every behavior — retryable statuses, jitter, error-body size — is an option on
/// <see cref="SoroushOptions"/>, validated by the guard before the first call.
/// Secrets never reach logs or error messages — provider bodies are scrubbed too.
/// </summary>
/// <param name="http">The HTTP pipeline to use. Injected for testability.</param>
/// <param name="options">Resolved provider options.</param>
public sealed class SoroushClient(HttpClient http, SoroushOptions options) : ISoroushClient
{
    private const string TruncationSuffix = "…";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly SoroushOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ISoroushProvider _provider = SoroushProviders.Resolve(options.Provider);

    /// <inheritdoc />
    public async Task<SoroushResult> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        SoroushGuard.ValidateOptions(_options);
        var safePrompt = SoroushGuard.SanitizePrompt(prompt, _options.MaxPromptLength);
        var endpoint = SoroushGuard.ValidateEndpoint(_options.Endpoint);
        var clock = Stopwatch.StartNew();
        var maxAttempts = _options.MaxRetries + 1;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = _provider.BuildRequest(_options, endpoint, safePrompt);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);

                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    var content = _provider.ParseResponse(json);
                    return new SoroushResult(content, _provider.Name, _options.Model, attempt, clock.Elapsed);
                }

                if (!_options.RetryableStatusCodes.Contains(response.StatusCode) || attempt >= maxAttempts)
                    throw await CreateApiErrorAsync(response, timeout.Token).ConfigureAwait(false);

                await DelayBeforeRetryAsync(response.Headers.RetryAfter?.Delta, attempt, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Per-attempt timeout (the caller did not cancel): retry while attempts remain.
                if (attempt >= maxAttempts)
                    throw new SoroushException($"AI request timed out after {attempt} attempt(s) without success.");

                await DelayBeforeRetryAsync(null, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await DelayBeforeRetryAsync(null, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new SoroushException(
                    $"Network error talking to the AI provider: {ex.Message}", innerException: ex);
            }
        }
    }

    private async Task DelayBeforeRetryAsync(TimeSpan? retryAfter, int attempt, CancellationToken cancellationToken)
    {
        // Exponential backoff with configurable jitter: spreading retries out prevents
        // synchronized retry storms against an already-struggling provider.
        var backoff = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jitter = _options.JitterScale;
        var jittered = backoff * (1 - jitter + (2 * jitter * Random.Shared.NextDouble()));
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(jittered);

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SoroushException> CreateApiErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > _options.MaxErrorBodyLength)
            body = body[.._options.MaxErrorBodyLength] + TruncationSuffix;

        // Defense in depth: if the provider ever echoes our key back in an error body,
        // scrub it before the message can reach a console or log.
        body = SoroushGuard.RedactIn(body, _options.ApiKey);

        return new SoroushException(
            $"AI provider returned {(int)response.StatusCode} ({response.StatusCode}). Body: {body}",
            (int)response.StatusCode);
    }
}
