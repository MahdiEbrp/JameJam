using System.Globalization;
using System.Text.Json;

using JameJam.Sync;

namespace JameJam.Tests.Sync;

/// <summary>The envelope safety layer: sealing, opening, checksums, schema gates, size rails.</summary>
public sealed class SyncSafetyTests
{
    private const string Payload = """{"hello":["world",1,2]}""";

    [Fact]
    public void SealOpen_RoundTrips_ThePayload()
    {
        var envelope = SyncSafety.Seal("divan", Payload, "device-a", "laptop", Fixed.Now);
        var opened = SyncSafety.Open(envelope.ToJson());

        Assert.Equal("divan", opened.Service);
        Assert.Equal("device-a", opened.DeviceId);
        Assert.Equal("laptop", opened.DeviceName);
        Assert.Equal(Fixed.Now, opened.CreatedAt);
        Assert.Equal(Payload, opened.Payload);
        Assert.Equal(SyncEnvelope.CurrentSchema, opened.Schema);
    }

    [Fact]
    public void Checksum_IsStableSha256Hex()
    {
        // SHA-256 of "hello"
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", SyncSafety.ChecksumOf("hello"));
    }

    [Fact]
    public void Open_CorruptedPayload_FailsWithIntegrityError()
    {
        var envelope = SyncSafety.Seal("divan", Payload, "a", "laptop", Fixed.Now) with { Payload = """{"hello":"tampered"}""" };
        var exception = Assert.Throws<SyncException>(() => SyncSafety.Open(envelope.ToJson()));
        Assert.Contains("integrity check", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was merged", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_WrongSchema_FailsWithUpgradeHint()
    {
        var envelope = SyncSafety.Seal("divan", Payload, "a", "laptop", Fixed.Now) with { Schema = "jamejam.sync/0" };
        var exception = Assert.Throws<SyncException>(() => SyncSafety.Open(envelope.ToJson()));
        Assert.Contains("upgrade", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_GarbageDocument_FailsFriendly()
    {
        Assert.Throws<SyncException>(() => SyncSafety.Open("""{"hello":true}"""));
        Assert.Throws<SyncException>(() => SyncSafety.Open("not json at all"));
        Assert.Throws<SyncException>(() => SyncSafety.Open(""));
    }

    [Fact]
    public void Seal_OversizePayload_FailsBeforeAnyWire()
    {
        var exception = Assert.Throws<SyncException>(
            () => SyncSafety.Seal("divan", new string('x', 2048), "a", "n", Fixed.Now, maxPayloadBytes: 1024));
        Assert.Contains("above the configured maximum", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_RejectsEmptyEssentials()
    {
        Assert.Throws<SyncException>(() => SyncSafety.Seal("", Payload, "a", "n", Fixed.Now));
        Assert.Throws<SyncException>(() => SyncSafety.Seal("divan", "   ", "a", "n", Fixed.Now));
        Assert.Throws<SyncException>(() => SyncSafety.Seal("divan", Payload, " ", "n", Fixed.Now));
    }

    [Fact]
    public void Open_HandWrittenLowercaseEnvelope_Works()
    {
        var envelope = SyncSafety.Seal("divan", Payload, "a", "n", Fixed.Now);
        var canonical = SyncSafety.Open(envelope.ToJson());

        // A hand-written envelope (lowercase keys, e.g. from another tool) must open too.
        var handwritten = string.Create(CultureInfo.InvariantCulture,
            $"{{\"schema\":\"jamejam.sync/1\",\"service\":\"{canonical.Service}\",\"deviceId\":\"{canonical.DeviceId}\",\"deviceName\":\"n\",\"createdAt\":\"{canonical.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}\",\"payload\":{JsonSerializer.Serialize(Payload)},\"checksum\":\"{canonical.Checksum}\"}}");
        Assert.Equal(Payload, SyncSafety.Open(handwritten).Payload);
    }
}

/// <summary>Shared fixed time for sync tests.</summary>
internal static class Fixed
{
    public static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
}
