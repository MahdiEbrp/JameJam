namespace JameJam.Soroush;

/// <summary>
/// Hardened HTTP plumbing for AI calls.
/// Redirects are disabled: endpoints are validated up front (HTTPS, or loopback HTTP),
/// and silently following a redirect could move a call — and its credentials — somewhere else.
/// (.NET already strips Authorization on cross-origin redirects; failing loudly is safer still.)
/// </summary>
public static class SoroushHttp
{
    /// <summary>Creates an HttpClientHandler with redirects disabled.</summary>
    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false };

    /// <summary>Creates an HttpClient wired to <see cref="CreateHandler"/>.</summary>
    public static HttpClient CreateClient() => new(CreateHandler());
}
