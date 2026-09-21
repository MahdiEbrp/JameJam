using JameJam.Anahita;

namespace JameJam.Tests.Anahita;

/// <summary>
/// Exhaustive WMO table coverage and record semantics (equality, cloning, display names)
/// for the Anahita data model.
/// </summary>
public sealed class AnahitaModelCoverageTests
{
    /// <summary>Every WMO code the service knows, in table order.</summary>
    private static readonly (int Code, string Description)[] WmoTable =
    [
        (0, "Clear sky"), (1, "Mainly clear"), (2, "Partly cloudy"), (3, "Overcast"),
        (45, "Fog"), (48, "Rime fog"),
        (51, "Light drizzle"), (53, "Drizzle"), (55, "Dense drizzle"),
        (56, "Freezing drizzle"), (57, "Dense freezing drizzle"),
        (61, "Light rain"), (63, "Rain"), (65, "Heavy rain"),
        (66, "Freezing rain"), (67, "Heavy freezing rain"),
        (71, "Light snow"), (73, "Snow"), (75, "Heavy snow"), (77, "Snow grains"),
        (80, "Light showers"), (81, "Showers"), (82, "Violent showers"),
        (85, "Snow showers"), (86, "Heavy snow showers"),
        (95, "Thunderstorm"), (96, "Thunderstorm with hail"), (99, "Severe thunderstorm with hail"),
    ];

    [Theory]
    [InlineData(0, "Clear sky")]
    [InlineData(1, "Mainly clear")]
    [InlineData(2, "Partly cloudy")]
    [InlineData(3, "Overcast")]
    [InlineData(45, "Fog")]
    [InlineData(48, "Rime fog")]
    [InlineData(51, "Light drizzle")]
    [InlineData(53, "Drizzle")]
    [InlineData(55, "Dense drizzle")]
    [InlineData(56, "Freezing drizzle")]
    [InlineData(57, "Dense freezing drizzle")]
    [InlineData(61, "Light rain")]
    [InlineData(63, "Rain")]
    [InlineData(65, "Heavy rain")]
    [InlineData(66, "Freezing rain")]
    [InlineData(67, "Heavy freezing rain")]
    [InlineData(71, "Light snow")]
    [InlineData(73, "Snow")]
    [InlineData(75, "Heavy snow")]
    [InlineData(77, "Snow grains")]
    [InlineData(80, "Light showers")]
    [InlineData(81, "Showers")]
    [InlineData(82, "Violent showers")]
    [InlineData(85, "Snow showers")]
    [InlineData(86, "Heavy snow showers")]
    [InlineData(95, "Thunderstorm")]
    [InlineData(96, "Thunderstorm with hail")]
    [InlineData(99, "Severe thunderstorm with hail")]
    public void Describe_CoversTheEntireWmoTable(int code, string description) =>
        Assert.Equal(description, WeatherCode.Describe(code));

    private static readonly int[] ThunderFamily = [95, 96, 97, 98, 99];

    [Theory]
    [InlineData(0, "☀")]
    [InlineData(1, "☀")]
    [InlineData(2, "☁")]
    [InlineData(3, "☁")]
    [InlineData(45, "☁")]
    [InlineData(48, "☁")]
    [InlineData(51, "☂")]
    [InlineData(57, "☂")]
    [InlineData(61, "☂")]
    [InlineData(67, "☂")]
    [InlineData(71, "❆")]
    [InlineData(77, "❆")]
    [InlineData(80, "☂")]
    [InlineData(82, "☂")]
    [InlineData(85, "❆")]
    [InlineData(86, "❆")]
    [InlineData(95, "⚡")]
    [InlineData(99, "⚡")]
    [InlineData(50, "·")] // unmapped → neutral dot
    public void Icon_MatchesTheConditionFamily(int code, string icon) =>
        Assert.Equal(icon, WeatherCode.Icon(code));

    [Fact]
    public void Thunderstorms_SpanTheirWholeFamily() =>
        Assert.All(ThunderFamily, code => Assert.True(WeatherCode.IsThunderstorm(code)));

    [Fact]
    public void DisplayName_ComposesNameAdminAndCountry()
    {
        GeoPlace full = new("Berlin", "Germany", 52.52, 13.41, "Europe/Berlin") { Admin1 = "State of Berlin" };
        Assert.Equal("Berlin, State of Berlin, Germany", full.DisplayName);

        GeoPlace noAdmin = new("Berlin", "Germany", 52.52, 13.41, "Europe/Berlin");
        Assert.Equal("Berlin, Germany", noAdmin.DisplayName);

        GeoPlace adminEqualsName = new("Berlin", "Germany", 52.52, 13.41, "Europe/Berlin") { Admin1 = "berlin" };
        Assert.Equal("Berlin, Germany", adminEqualsName.DisplayName);

        GeoPlace noCountry = new("52.52, 13.41", string.Empty, 52.52, 13.41, "auto") { Admin1 = null };
        Assert.Equal("52.52, 13.41", noCountry.DisplayName);

        GeoPlace adminOnly = new("Nowhere", string.Empty, 1, 2, "auto") { Admin1 = "Somewhere" };
        Assert.Equal("Nowhere, Somewhere", adminOnly.DisplayName);
    }

    [Fact]
    public void Points_SupportEqualityCloningAndDeconstruction()
    {
        var hour = new HourlyPoint(new DateTime(2026, 9, 19, 14, 0, 0), 18.4, 17.9, 10, 0.0, 2, 12.4, 62);
        var same = hour with { };
        var different = hour with { TemperatureC = 20 };

        Assert.True(hour.Equals(same));
        Assert.Equal(hour.GetHashCode(), same.GetHashCode());
        Assert.False(hour.Equals(different));
        Assert.False(hour.Equals(null));

        var (time, temp, apparent, chance, precip, code, wind, humidity) = hour;
        Assert.Equal(18.4, temp);
        Assert.Equal(10, chance);
        Assert.Equal(2, code);

        var conditions = new CurrentConditions(time, 18.4, 17.9, 62, 0.4, 2, 12.4, 315);
        Assert.True(conditions.Equals(conditions with { }));
        Assert.False(conditions.Equals(conditions with { Code = 3 }));

        // Records render readable diagnostics.
        Assert.Contains("HourlyPoint", hour.ToString(), StringComparison.Ordinal);
        Assert.Contains("CurrentConditions", conditions.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_SupportsEqualityAndDeconstruction()
    {
        var report = AnahitaTestSupport.MakeReport(AnahitaTestSupport.Day(new DateOnly(2026, 9, 19)));
        var (place, fetchedAt, current, hourly, daily) = report;

        Assert.Equal(AnahitaTestSupport.Berlin().Name, place.Name);
        Assert.Equal(AnahitaTestSupport.Now, fetchedAt);
        Assert.Single(daily);
        Assert.True(report.Equals(report with { }));
        Assert.False(report.Equals(report with { Place = place with { Country = "Elsewhere" } }));
        Assert.Contains("WeatherReport", report.ToString(), StringComparison.Ordinal);
    }

}
