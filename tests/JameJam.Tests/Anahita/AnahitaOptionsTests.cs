using System.Globalization;

using JameJam.Anahita;

namespace JameJam.Tests.Anahita;

/// <summary>Rail tests for <see cref="AnahitaOptions"/> — every bound, endpoint policy, and env override.</summary>
[Collection("EnvSequential")]
public sealed class AnahitaOptionsTests
{
    private static void Rejects(AnahitaOptions options, string messagePart)
    {
        var exception = Assert.Throws<AnahitaException>(options.Validate);
        Assert.Contains(messagePart, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_AreValid()
    {
        var exception = Record.Exception(() => new AnahitaOptions().Validate());
        Assert.Null(exception);
    }

    [Fact]
    public void ForecastEndpoint_IsRequired()
    {
        Rejects(new AnahitaOptions { ForecastEndpoint = "" }, "forecast endpoint must not be empty");
        Rejects(new AnahitaOptions { ForecastEndpoint = "not a url" }, "Invalid forecast endpoint");
    }

    [Fact]
    public void ForecastEndpoint_RejectsQueryStrings()
    {
        Rejects(
            new AnahitaOptions { ForecastEndpoint = "https://api.test/v1/forecast?key=x" },
            "without a query string");
    }

    [Fact]
    public void GeocodingEndpoint_FollowsTheSamePolicy()
    {
        Rejects(new AnahitaOptions { GeocodingEndpoint = "" }, "geocoding endpoint must not be empty");
        Rejects(new AnahitaOptions { GeocodingEndpoint = "ftp://geo.test" }, "Insecure geocoding endpoint");
        Rejects(
            new AnahitaOptions { GeocodingEndpoint = "http://geo.example.com/search" },
            "Insecure geocoding endpoint");
    }

    [Fact]
    public void LoopbackHttp_IsAllowed()
    {
        var exception = Record.Exception(() => new AnahitaOptions
        {
            ForecastEndpoint = "http://127.0.0.1:8080/v1/forecast",
            GeocodingEndpoint = "http://localhost:8080/search",
        }.Validate());
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("0:00:00")]     // zero — must be positive
    [InlineData("0:11:00")]     // above the bound
    public void RequestTimeout_HasRails(string timeout)
    {
        Rejects(new AnahitaOptions { RequestTimeout = TimeSpan.Parse(timeout, CultureInfo.InvariantCulture) }, "RequestTimeout must be");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void MaxRetries_HasRails(int retries) =>
        Rejects(new AnahitaOptions { MaxRetries = retries }, "MaxRetries must be between 0 and 10");

    [Fact]
    public void RetryBaseDelay_CannotBeNegative() =>
        Rejects(new AnahitaOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(-1) }, "RetryBaseDelay");

    [Theory]
    [InlineData(0)]
    [InlineData(65L * 1024 * 1024)]
    public void MaxResponseBytes_HasRails(long bytes) =>
        Rejects(new AnahitaOptions { MaxResponseBytes = bytes }, "MaxResponseBytes must be between 1 and");

    [Theory]
    [InlineData(-1)]
    [InlineData(25)]
    public void CacheTtl_HasRails(double hours) =>
        Rejects(new AnahitaOptions { CacheTtl = TimeSpan.FromHours(hours) }, "CacheTtl must be between zero and");

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void ForecastDays_HasRails(int days) =>
        Rejects(new AnahitaOptions { ForecastDays = days }, "ForecastDays must be between 1 and 16");

    [Theory]
    [InlineData(0)]
    [InlineData(49)]
    public void HourlyWindow_HasRails(int hours) =>
        Rejects(new AnahitaOptions { HourlyWindow = hours }, "HourlyWindow must be between 1 and 48");

    [Fact]
    public void FromEnvironment_OverridesEndpointsAndKey()
    {
        Environment.SetEnvironmentVariable(AnahitaDefaults.EndpointEnvironmentVariable, "https://mirror.test/forecast");
        Environment.SetEnvironmentVariable(AnahitaDefaults.GeocodingEnvironmentVariable, "https://mirror.test/search");
        Environment.SetEnvironmentVariable(AnahitaDefaults.ApiKeyEnvironmentVariable, "mirror-key-42");
        try
        {
            var options = AnahitaOptions.FromEnvironment();
            Assert.Equal("https://mirror.test/forecast", options.ForecastEndpoint);
            Assert.Equal("https://mirror.test/search", options.GeocodingEndpoint);
            Assert.Equal("mirror-key-42", options.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnahitaDefaults.EndpointEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(AnahitaDefaults.GeocodingEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(AnahitaDefaults.ApiKeyEnvironmentVariable, null);
        }
    }

    [Fact]
    public void FromEnvironment_BlankValues_FallBackToDefaults()
    {
        Environment.SetEnvironmentVariable(AnahitaDefaults.EndpointEnvironmentVariable, "   ");
        try
        {
            var options = AnahitaOptions.FromEnvironment();
            Assert.Equal(AnahitaDefaults.ForecastEndpoint, options.ForecastEndpoint);
            Assert.Equal(AnahitaDefaults.GeocodingEndpoint, options.GeocodingEndpoint);
            Assert.Null(options.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AnahitaDefaults.EndpointEnvironmentVariable, null);
        }
    }
}
