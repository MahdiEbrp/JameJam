namespace JameJam.Sync;

/// <summary>
/// Transport for device sync: GET/PUT of one JSON document at a URL. Any server that can
/// store and serve a blob works — Node.js, PHP, a WebDAV share, an S3 bucket, anything.
/// </summary>
public interface ISyncClient
{
    /// <summary>Downloads the remote document. Returns null when the remote does not exist yet (HTTP 404).</summary>
    /// <exception cref="SyncException">Transport failure, oversized response, or an unreadable remote.</exception>
    Task<string?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Uploads <paramref name="json"/> as the remote document.</summary>
    /// <exception cref="SyncException">Transport failure or a rejecting remote.</exception>
    Task PutAsync(string json, CancellationToken cancellationToken = default);
}
