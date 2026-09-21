using System.Globalization;

using JameJam.Taqvim;

namespace JameJam.Tests.Taqvim;

/// <summary>The defensive RFC 5545 subset: parse (never throws), export (compliant escaping), rails.</summary>
public sealed class IcsTests
{
    private static TaqvimEvent Ev(
        string title = "Standup",
        string start = "2026-09-21T09:30:00+00:00",
        string end = "2026-09-21T09:45:00+00:00",
        bool allDay = false,
        string location = "",
        string notes = "",
        string tags = "",
        Recurrence? rule = null,
        IReadOnlyList<int>? reminders = null) => new(
        1, "Work", title, location, notes, tags,
        DateTimeOffset.Parse(start, CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(end, CultureInfo.InvariantCulture),
        allDay, rule, reminders ?? [],
        DateTimeOffset.Parse("2026-09-20T10:00:00+00:00", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-09-20T10:00:00+00:00", CultureInfo.InvariantCulture),
        Guid.NewGuid());

    [Fact]
    public void Parse_UtcEvents_WithRemindersAndRule()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:abc@x
            DTSTAMP:20260920T100000Z
            SUMMARY:Standup
            DTSTART:20260921T093000Z
            DTEND:20260921T094500Z
            RRULE:FREQ=DAILY;COUNT=5
            BEGIN:VALARM
            ACTION:DISPLAY
            TRIGGER:-PT15M
            END:VALARM
            BEGIN:VALARM
            ACTION:DISPLAY
            TRIGGER:-PT1H
            END:VALARM
            END:VEVENT
            END:VCALENDAR
            """;
        var events = Ics.Parse(ics);
        var ev = Assert.Single(events);
        Assert.Equal("Standup", ev.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 9, 30, 0, TimeSpan.Zero), ev.Start);
        Assert.Equal(TimeSpan.FromMinutes(15), ev.End - ev.Start);
        Assert.Equal([15, 60], ev.Reminders); // sorted
        Assert.NotNull(ev.Rule);
        Assert.Equal(RecurrenceKind.Daily, ev.Rule.Kind);
        Assert.Equal(5, ev.Rule.Count);
    }

    [Fact]
    public void Parse_AllDayDateForm()
    {
        const string ics = """
            BEGIN:VEVENT
            SUMMARY:Nowruz
            DTSTART;VALUE=DATE:20270321
            DTEND;VALUE=DATE:20270323
            END:VEVENT
            """;
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.True(ev.IsAllDay);
        Assert.Equal(new DateTimeOffset(2027, 3, 21, 0, 0, 0, TimeSpan.Zero), ev.Start);
        Assert.Equal(new DateTimeOffset(2027, 3, 23, 0, 0, 0, TimeSpan.Zero), ev.End);
    }

    [Fact]
    public void Parse_FloatingTimes_AssumeLocal()
    {
        const string ics = """
            BEGIN:VEVENT
            SUMMARY:Local lunch
            DTSTART:20260922T120000
            DTEND:20260922T130000
            END:VEVENT
            """;
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Equal(TimeSpan.FromHours(1), ev.End - ev.Start);
        Assert.False(ev.IsAllDay);
    }

    [Fact]
    public void Parse_LineFolding_IsUnfolded()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:Annual town\r\n  hall meeting\r\nDTSTART:20260922T090000\r\nDTEND:20260922T100000\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Equal("Annual town hall meeting", ev.Title);
    }

    [Fact]
    public void Parse_Escapes_AreReversed()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:Trip to Paris\\, France\\; day one\nDESCRIPTION:Line one\\nLine two with \\\\ a slash\r\nDTSTART:20260922T090000\r\nDTEND:20260922T100000\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Equal("Trip to Paris, France; day one", ev.Title);
        Assert.Equal("Line one\nLine two with \\ a slash", ev.Notes);
    }

    [Fact]
    public void Parse_Duration_WhenNoEnd()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:Short\r\nDTSTART:20260922T090000\r\nDURATION:PT90M\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Equal(TimeSpan.FromMinutes(90), ev.End - ev.Start);
    }

    [Fact]
    public void Parse_MissingEnd_GetsDefaultDuration()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:Quick call\r\nDTSTART:20260922T090000\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Equal(TimeSpan.FromMinutes(TaqvimDefaults.DefaultEventMinutes), ev.End - ev.Start);
    }

    [Fact]
    public void Parse_InvertedWindow_IsRepaired()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:Warp\r\nDTSTART:20260922T090000\r\nDTEND:20260922T080000\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.True(ev.End > ev.Start);
    }

    [Fact]
    public void Parse_NoStart_IsSkipped()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:No time\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nSUMMARY:Timed\r\nDTSTART:20260922T090000\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Equal("Timed", ev.Title);
    }

    [Fact]
    public void Parse_NoSummary_IsSkipped()
    {
        const string ics = "BEGIN:VEVENT\r\nDTSTART:20260922T090000\r\nEND:VEVENT\r\n";
        Assert.Empty(Ics.Parse(ics));
    }

    [Fact]
    public void Parse_CategoriesBecomeTags_AndLocationIsRead()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:Offsite\r\nLOCATION:Cafe Riviera\r\nCATEGORIES:work;team\r\nDTSTART:20260922T090000\r\nDTEND:20260922T100000\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Equal("Cafe Riviera", ev.Location);
        Assert.Equal("work,team", ev.Tags);
    }

    [Fact]
    public void Parse_UnknownRule_BecomesOneOff()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:Weird\r\nRRULE:FREQ=SECONDLY\r\nDTSTART:20260922T090000\r\nDTEND:20260922T100000\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Null(ev.Rule);
    }

    [Fact]
    public void Parse_Garbage_NeverThrows()
    {
        Assert.Empty(Ics.Parse(""));
        Assert.Empty(Ics.Parse("random text with no colons"));
        Assert.Empty(Ics.Parse("BEGIN:NOPE\nEND:NOPE"));
        Assert.Empty(Ics.Parse(":::"));
    }

    [Fact]
    public void Parse_TriggerAfterStart_IsNotAReminder()
    {
        const string ics = "BEGIN:VEVENT\r\nSUMMARY:After\r\nDTSTART:20260922T090000\r\nDTEND:20260922T100000\r\nBEGIN:VALARM\r\nTRIGGER:PT10M\r\nEND:VALARM\r\nEND:VEVENT\r\n";
        var ev = Assert.Single(Ics.Parse(ics));
        Assert.Empty(ev.Reminders);
    }

    [Fact]
    public void Export_RoundTripsThroughParse()
    {
        var source = new List<TaqvimEvent>
        {
            Ev(rule: new Recurrence(RecurrenceKind.Weekly, 1, [DayOfWeek.Monday], 4, null), reminders: [10, 30], location: "Room 4", notes: "weekly sync", tags: "team,work"),
            Ev(title: "Nowruz", start: "2027-03-21T00:00:00+00:00", end: "2027-03-22T00:00:00+00:00", allDay: true),
        };
        var text = Ics.Export(source);
        var parsed = Ics.Parse(text);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("Standup", parsed[0].Title);
        Assert.Equal("Room 4", parsed[0].Location);
        Assert.Equal("weekly sync", parsed[0].Notes);
        Assert.Equal("team,work", parsed[0].Tags);
        Assert.Equal([10, 30], parsed[0].Reminders);
        Assert.NotNull(parsed[0].Rule);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO;COUNT=4", Recurrences.ToRrule(parsed[0].Rule));

        Assert.True(parsed[1].IsAllDay);
        Assert.Equal(new DateTimeOffset(2027, 3, 21, 0, 0, 0, TimeSpan.Zero), parsed[1].Start);
    }

    [Fact]
    public void Export_HasHeader_AndEscapesSpecials()
    {
        var text = Ics.Export([Ev(title: "Semicolons; and, commas")]);
        Assert.StartsWith("BEGIN:VCALENDAR", text, StringComparison.Ordinal);
        Assert.Contains("PRODID:-//JameJam//Taqvim//EN", text, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Semicolons\\; and\\, commas", text, StringComparison.Ordinal);
        Assert.EndsWith("END:VCALENDAR\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_EmptySyncId_GetsGeneratedUid()
    {
        var text = Ics.Export([Ev() with { SyncId = Guid.Empty }]);
        Assert.Contains("UID:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Ics.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => Ics.Export(null!));
    }
}
