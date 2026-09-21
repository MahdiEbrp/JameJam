using JameJam.Anahita;

namespace JameJam.Tests.Anahita;

/// <summary>WMO code mapping and unit conversion maths.</summary>
public sealed class WeatherCodeAndUnitMathTests
{
    [Theory]
    [InlineData(0, "Clear sky")]
    [InlineData(1, "Mainly clear")]
    [InlineData(2, "Partly cloudy")]
    [InlineData(3, "Overcast")]
    [InlineData(45, "Fog")]
    [InlineData(61, "Light rain")]
    [InlineData(65, "Heavy rain")]
    [InlineData(71, "Light snow")]
    [InlineData(80, "Light showers")]
    [InlineData(95, "Thunderstorm")]
    [InlineData(99, "Severe thunderstorm with hail")]
    public void Describe_MapsTheWmoTable(int code, string expected) =>
        Assert.Equal(expected, WeatherCode.Describe(code));

    [Theory]
    [InlineData(-5)]
    [InlineData(100)]
    public void Describe_UnknownCodes_AreHonest(int code) =>
        Assert.Equal("Unrecognized weather code", WeatherCode.Describe(code));

    [Fact]
    public void Icons_AreNeverEmpty() =>
        Assert.All(SampledCodes, code => Assert.False(string.IsNullOrEmpty(WeatherCode.Icon(code))));

    private static readonly int[] SampledCodes = [0, 2, 45, 61, 71, 95, 100];

    [Theory]
    [InlineData(94, false)]
    [InlineData(95, true)]
    [InlineData(99, true)]
    [InlineData(100, false)]
    public void IsThunderstorm_BracketsTheRange(int code, bool expected) =>
        Assert.Equal(expected, WeatherCode.IsThunderstorm(code));

    [Theory]
    [InlineData(0, 32)]
    [InlineData(100, 212)]
    [InlineData(-40, -40)]
    [InlineData(36.6, 97.88)]
    public void Fahrenheit_ConvertsExactly(double celsius, double fahrenheit) =>
        Assert.Equal(fahrenheit, UnitMath.FahrenheitFromCelsius(celsius), 10);

    [Theory]
    [InlineData(100, 62.1371192237334)]
    [InlineData(0, 0)]
    public void Mph_ConvertsExactly(double kmh, double mph) =>
        Assert.Equal(mph, UnitMath.MphFromKmh(kmh), 10);

    [Fact]
    public void MetresPerSecond_ConvertsExactly()
    {
        Assert.Equal(25, UnitMath.MetresPerSecondFromKmh(90), 10);
        Assert.Equal(10, UnitMath.KnotsFromKmh(18.52), 10);
    }

    [Theory]
    [InlineData(18.4, WeatherUnits.Metric, "18.4°C")]
    [InlineData(18.4, WeatherUnits.Imperial, "65.1°F")]
    [InlineData(20, WeatherUnits.Metric, "20°C")]
    public void Temperature_FormatsPerUnits(double celsius, WeatherUnits units, string expected) =>
        Assert.Equal(expected, UnitMath.Temperature(celsius, units));

    [Theory]
    [InlineData(12.4, WeatherUnits.Metric, "12.4 km/h")]
    [InlineData(12.4, WeatherUnits.Imperial, "7.7 mph")]
    [InlineData(60, WeatherUnits.Metric, "60 km/h")]
    public void Speed_FormatsPerUnits(double kmh, WeatherUnits units, string expected) =>
        Assert.Equal(expected, UnitMath.Speed(kmh, units));
}
