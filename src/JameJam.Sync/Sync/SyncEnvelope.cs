using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JameJam.Sync;

/// <summary>
/// The unit of device sync: a sealed, self-describing envelope. The payload is service-owned
/// JSON; everything else is metadata the engine and safety layer rely on. The checksum makes
/// corruption detectable, the schema makes protocol drift detectable, and the service tag makes
/// cross-service clobbering (pointing <c>divan sync</c> at a Haft Khan URL) impossible.
/// </summary>
/// <param name="Schema">Protocol marker, e.g. <c>jamejam.sync/1</c>.</param>
/// <param name="Service">Which service this payload belongs to (<c>divan</c>, <c>haftkhan</c>, …).</param>
/// <param name="DeviceId">Stable GUID of the device that sealed the envelope.</param>
/// <param name="DeviceName">Friendly device name for reports.</param>
/// <param name="CreatedAt">When the envelope was sealed (UTC).</param>
/// <param name="Payload">The service-owned JSON document.</param>
/// <param name="Checksum">SHA-256 hex digest of the UTF-8 payload.</param>
public sealed record SyncEnvelope(
    string Schema,
    string Service,
    string DeviceId,
    string DeviceName,
    DateTimeOffset CreatedAt,
    string Payload,
    string Checksum)
{
    /// <summary>The current protocol marker.</summary>
    public const string CurrentSchema = "jamejam.sync/1";

    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes to compact JSON (culture-invariant, UTC timestamps).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriterOptions);
}

/// <summary>
/// Seals and opens sync envelopes: checksums, schema gates, and size rails. Everything that
/// crosses a network boundary passes through here — nothing is trusted on either side.
/// </summary>
public static class SyncSafety
{
    private static readonly JsonSerializerOptions ReaderOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Seals a payload into an envelope, verifying the size rail first.</summary>
    /// <exception cref="SyncException">Empty payload or payload above the size rail.</exception>
    public static SyncEnvelope Seal(
        string service,
        string payload,
        string deviceId,
        string deviceName,
        DateTimeOffset now,
        long maxPayloadBytes = SyncDefaults.MaxPayloadBytes)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new SyncException("Sync requires a service tag.");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new SyncException("Sync payloads must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new SyncException("Sync requires a device id.");
        }

        var size = Encoding.UTF8.GetByteCount(payload);
        if (size > maxPayloadBytes)
        {
            throw new SyncException(
                FormattableString.Invariant(
                    $"Sync payload is {size} bytes — above the configured maximum of {maxPayloadBytes}. Trim it or raise SyncDefaults.MaxPayloadBytes."));
        }

        return new SyncEnvelope(
            SyncEnvelope.CurrentSchema,
            service,
            deviceId,
            deviceName,
            now,
            payload,
            ChecksumOf(payload));
    }

    /// <summary>
    /// Opens an envelope: parses it, verifies the schema and the checksum. The returned
    /// envelope is safe to hand to an adapter.
    /// </summary>
    /// <exception cref="SyncException">Not an envelope, wrong schema, or a corrupted payload.</exception>
    public static SyncEnvelope Open(string envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson))
        {
            throw new SyncException("The remote document is empty — nothing to sync.");
        }

        SyncEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SyncEnvelope>(envelopeJson, ReaderOptions);
        }
        catch (JsonException ex)
        {
            throw new SyncException("The remote document is not a JameJam sync envelope.", innerException: ex);
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Payload) || string.IsNullOrWhiteSpace(envelope.Service))
        {
            throw new SyncException("The remote document is not a JameJam sync envelope.");
        }

        if (envelope.Schema != SyncEnvelope.CurrentSchema)
        {
            throw new SyncException(
                $"The remote speaks sync protocol '{envelope.Schema}'; this build speaks '{SyncEnvelope.CurrentSchema}'. Upgrade JameJam on both devices.");
        }

        if (ChecksumOf(envelope.Payload) != envelope.Checksum)
        {
            throw new SyncException(
                "The remote envelope failed its integrity check (checksum mismatch) — the document is corrupted or was tampered with. Nothing was merged.");
        }

        return envelope;
    }

    /// <summary>SHA-256 of the UTF-8 payload, lowercase hex.</summary>
    public static string ChecksumOf(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
