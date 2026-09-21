using System.Globalization;

using JameJam.Taqvim;

namespace JameJam.Tests.Taqvim;

/// <summary>Every option rail rejects out-of-range values and accepts in-range ones.</summary>
public sealed class TaqvimOptionsTests
{
    [Fact]
    public void Defaults_PassValidation()
    {
        var ex = Record.Exception(() => TaqvimOptions.CreateValidated(new TaqvimOptions()));
        Assert.Null(ex);
    }

    [Fact]
    public void CreateValidated_Null_YieldsDefaults()
    {
        var options = TaqvimOptions.CreateValidated(null);
        Assert.Equal(TaqvimDefaults.MaxEvents, options.MaxEvents);
        Assert.Equal(TaqvimDefaults.OutdoorTag, options.OutdoorTag);
    }

    public static TheoryData<string, TaqvimOptions> Invalid() => new()
    {
        { "MaxTitleLength", new TaqvimOptions { MaxTitleLength = 0 } },
        { "MaxTitleLengthHigh", new TaqvimOptions { MaxTitleLength = TaqvimDefaults.MaxTitleLength + 1 } },
        { "MaxNotesLength", new TaqvimOptions { MaxNotesLength = 0 } },
        { "MaxNotesLengthHigh", new TaqvimOptions { MaxNotesLength = TaqvimDefaults.MaxNotesLength + 1 } },
        { "MaxEvents", new TaqvimOptions { MaxEvents = 0 } },
        { "MaxEventsHigh", new TaqvimOptions { MaxEvents = TaqvimDefaults.MaxEvents + 1 } },
        { "MaxRemindersPerEvent", new TaqvimOptions { MaxRemindersPerEvent = 0 } },
        { "MaxRemindersPerEventHigh", new TaqvimOptions { MaxRemindersPerEvent = TaqvimDefaults.MaxRemindersPerEvent + 1 } },
        { "MaxAgendaDays", new TaqvimOptions { MaxAgendaDays = 0 } },
        { "MaxAgendaDaysHigh", new TaqvimOptions { MaxAgendaDays = TaqvimDefaults.MaxAgendaDays + 1 } },
        { "MaxScheduleHorizonYears", new TaqvimOptions { MaxScheduleHorizonYears = 0 } },
        { "MaxScheduleHorizonYearsHigh", new TaqvimOptions { MaxScheduleHorizonYears = TaqvimDefaults.MaxScheduleHorizonYears + 1 } },
        { "UndoDepth", new TaqvimOptions { UndoDepth = -1 } },
        { "UndoDepthHigh", new TaqvimOptions { UndoDepth = TaqvimDefaults.UndoDepth + 1 } },
        { "SearchLimit", new TaqvimOptions { SearchLimit = 0 } },
        { "SearchLimitHigh", new TaqvimOptions { SearchLimit = TaqvimDefaults.SearchLimitBound + 1 } },
        { "MaxAiEvents", new TaqvimOptions { MaxAiEvents = 0 } },
        { "MaxAiEventsHigh", new TaqvimOptions { MaxAiEvents = TaqvimDefaults.MaxAiEvents + 1 } },
        { "OutdoorTag", new TaqvimOptions { OutdoorTag = new string('x', TaqvimDefaults.MaxTagLength + 1) } },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void OutsideTheRail_Throws(string label, TaqvimOptions options)
    {
        _ = label;
        Assert.Throws<TaqvimException>(() => TaqvimOptions.CreateValidated(options));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(-3, false)]
    public void PositiveInt_ParsesOrRejects(int value, bool ok)
    {
        if (ok)
        {
            Assert.Equal(value, TaqvimParse.PositiveInt(value.ToString(CultureInfo.InvariantCulture), "X"));
        }
        else
        {
            Assert.Throws<TaqvimException>(() => TaqvimParse.PositiveInt(value.ToString(CultureInfo.InvariantCulture), "X"));
        }
    }

    [Fact]
    public void PositiveInt_NonNumeric_Throws()
    {
        Assert.Throws<TaqvimException>(() => TaqvimParse.PositiveInt("soon", "X"));
    }
}
