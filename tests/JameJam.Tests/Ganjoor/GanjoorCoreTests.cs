using JameJam.Ganjoor;

namespace JameJam.Tests.Ganjoor;

/// <summary>Rail tests for <see cref="GanjoorOptions"/> — every bound and the rate table policy.</summary>
public sealed class GanjoorOptionsTests
{
    private static void Rejects(GanjoorOptions options, string messagePart)
    {
        var exception = Assert.Throws<GanjoorException>(options.Validate);
        Assert.Contains(messagePart, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_AreValid() => Assert.Null(Record.Exception(() => new GanjoorOptions().Validate()));

    [Theory]
    [InlineData("US")]      // too short
    [InlineData("USDD")]    // too long
    [InlineData("US1")]     // not letters
    [InlineData("")]
    public void DefaultCurrency_MustBeACurrencyCode(string code) =>
        Rejects(new GanjoorOptions { DefaultCurrency = code }, "Default currency is invalid");

    [Theory]
    [InlineData(0.001)]
    [InlineData(1_000_000_000_001)]
    public void MaxAmount_HasRails(decimal max) => Rejects(new GanjoorOptions { MaxAmount = max }, "MaxAmount must be");

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void UndoDepth_HasRails(int depth) => Rejects(new GanjoorOptions { UndoDepth = depth }, "UndoDepth must be");

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void MaxAccounts_HasRails(int max) => Rejects(new GanjoorOptions { MaxAccounts = max }, "MaxAccounts must be");

    [Theory]
    [InlineData(0)]
    [InlineData(50_001)]
    public void MaxImportRows_HasRails(int rows) =>
        Rejects(new GanjoorOptions { MaxImportRows = rows }, "MaxImportRows must be");

    [Fact]
    public void Rates_MustBeValidCurrencies_WithPositiveValues()
    {
        Rejects(
            new GanjoorOptions { Rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["EURO"] = 1 } },
            "Currency must be a 3-letter code");
        Rejects(
            new GanjoorOptions { Rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["EUR"] = 0 } },
            "Rate for EUR must be between 0 and");
    }

    [Fact]
    public void ValidRates_Pass()
    {
        var options = new GanjoorOptions
        {
            Rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["EUR"] = 1.08m },
        };
        Assert.Null(Record.Exception(options.Validate));
    }
}

/// <summary>The input security gate: amounts, currencies, dates, enums, ids, and tags.</summary>
public sealed class MoneyGuardTests
{
    [Theory]
    [InlineData("42.5", 42.5)]
    [InlineData("0.01", 0.01)]
    [InlineData("1,234.5", 1234.5)] // thousands separator is valid invariant number style
    public void Amount_ParsesPositiveValues(string text, double expected) =>
        Assert.Equal((decimal)expected, MoneyGuard.Amount(text, 1_000_000));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("2000000")]
    public void Amount_RejectsJunk_AndRailViolations(string text) =>
        Assert.Throws<ArgumentException>(() => MoneyGuard.Amount(text, 1_000_000));

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData(" Eur ", "EUR")]
    public void Currency_Normalizes(string code, string expected) => Assert.Equal(expected, MoneyGuard.Currency(code));

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("12A")]
    [InlineData("")]
    [InlineData(null)]
    public void Currency_RejectsBadCodes(string? code) => Assert.Throws<ArgumentException>(() => MoneyGuard.Currency(code));

    [Fact]
    public void Tags_SplitTrimDedup_AndRespectTheCount()
    {
        var tags = MoneyGuard.Tags(" Food, food , drinks ,, ", maxCount: 5);
        Assert.Equal(["Food", "drinks"], tags);
        Assert.Throws<ArgumentException>(() => MoneyGuard.Tags("a,b,c,d,e,f", maxCount: 5));
    }

    [Fact]
    public void Dates_AndMonths_AreStrict()
    {
        Assert.Equal(new DateOnly(2026, 9, 19), MoneyGuard.Date("2026-09-19"));
        Assert.Throws<ArgumentException>(() => MoneyGuard.Date("19-09-2026"));
        Assert.Throws<ArgumentException>(() => MoneyGuard.Date("not-a-date"));
        Assert.Equal(new DateOnly(2026, 9, 1), MoneyGuard.Month("2026-09"));
        Assert.Throws<ArgumentException>(() => MoneyGuard.Month("2026-9"));
    }

    [Theory]
    [InlineData("income", GanjoorTxKind.Income)]
    [InlineData("in", GanjoorTxKind.Income)]
    [InlineData(" SPEND ", GanjoorTxKind.Expense)]
    [InlineData("transfer", GanjoorTxKind.Transfer)]
    public void Kind_ParsesAliases(string text, GanjoorTxKind expected) => Assert.Equal(expected, MoneyGuard.Kind(text));

    [Fact]
    public void Kind_RejectsUnknown() => Assert.Throws<ArgumentException>(() => MoneyGuard.Kind("barter"));

    [Theory]
    [InlineData("daily", GanjoorFrequency.Daily)]
    [InlineData("yearly", GanjoorFrequency.Yearly)]
    public void Frequency_Parses(string text, GanjoorFrequency expected) => Assert.Equal(expected, MoneyGuard.Frequency(text));

    [Fact]
    public void Frequency_RejectsUnknown() => Assert.Throws<ArgumentException>(() => MoneyGuard.Frequency("whenever"));

    [Fact]
    public void Id_RequiresPositiveNumbers()
    {
        Assert.Equal(7, MoneyGuard.Id("7", "Thing"));
        Assert.Throws<ArgumentException>(() => MoneyGuard.Id("0", "Thing"));
        Assert.Throws<ArgumentException>(() => MoneyGuard.Id("-2", "Thing"));
        Assert.Throws<ArgumentException>(() => MoneyGuard.Id("many", "Thing"));
    }

    [Fact]
    public void Money_FormatsInvariantly()
    {
        Assert.Equal("1,234.50 USD", MoneyGuard.Money(1234.5m, "USD"));
        Assert.Equal("0.07", MoneyGuard.Money(0.07m));
    }
}
