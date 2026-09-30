using System.Globalization;
using SwiftBatchApp.Core;

namespace SwiftBatchApp.Tests;

public class BusinessCalendarTests
{
    private static readonly TimeSpan Cutoff = new(13, 0, 0);

    [Theory]
    // now (yyyy-MM-dd HH:mm:ss)   holidays                 defer   target
    [InlineData("2026-07-07 10:00:00", "", false, "2026-07-07")]              // Tue before cut-off
    [InlineData("2026-07-07 13:00:00", "", false, "2026-07-07")]              // exactly at cut-off = on time
    [InlineData("2026-07-07 13:00:01", "", true, "2026-07-08")]               // after cut-off → Wed
    [InlineData("2026-07-10 14:00:00", "", true, "2026-07-13")]               // Fri late → Mon
    [InlineData("2026-07-11 09:00:00", "", true, "2026-07-13")]               // Sat → Mon
    [InlineData("2026-07-12 23:59:00", "", true, "2026-07-13")]               // Sun → Mon
    [InlineData("2026-07-10 14:00:00", "2026-07-13", true, "2026-07-14")]     // Fri late, Mon holiday → Tue
    [InlineData("2026-07-13 09:00:00", "2026-07-13", true, "2026-07-14")]     // arriving on a holiday
    [InlineData("2026-12-24 15:00:00", "2026-12-25,2026-12-26", true, "2026-12-28")]
    public void Route_defers_late_and_non_working_day_arrivals(string now, string holidays, bool defer, string target)
    {
        var result = BusinessCalendar.Route(
            DateTime.ParseExact(now, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Cutoff, BusinessCalendar.ParseHolidays(holidays));

        Assert.Equal(defer, result.Defer);
        Assert.Equal(DateTime.ParseExact(target, "yyyy-MM-dd", CultureInfo.InvariantCulture), result.TargetDay);
    }

    [Fact]
    public void ParseHolidays_accepts_mixed_separators_and_formats()
    {
        var set = BusinessCalendar.ParseHolidays("2026-01-01, 06/01/2026;2026-03-25\nbogus 2026-13-40");
        Assert.Equal(new[] { "2026-01-01", "2026-01-06", "2026-03-25" }, set.OrderBy(s => s));
    }

    [Theory]
    [InlineData("13:00", 13, 0)]
    [InlineData("1300", 13, 0)]
    [InlineData("13.00", 13, 0)]
    [InlineData("7:30", 7, 30)]
    [InlineData("07:05", 7, 5)]
    [InlineData("", 9, 9)]
    [InlineData("25:00", 9, 9)]
    [InlineData("noon", 9, 9)]
    public void ParseTime_accepts_common_notations(string text, int h, int m) =>
        Assert.Equal(new TimeSpan(h, m, 0), BusinessCalendar.ParseTime(text, new TimeSpan(9, 9, 0)));

    [Theory]
    [InlineData(2024, "2024-05-05", "2024-03-31")]
    [InlineData(2025, "2025-04-20", "2025-04-20")]
    [InlineData(2026, "2026-04-12", "2026-04-05")]
    [InlineData(2027, "2027-05-02", "2027-03-28")]
    public void Easter_dates(int year, string orthodox, string western)
    {
        Assert.Equal(DateTime.Parse(orthodox, CultureInfo.InvariantCulture), BusinessCalendar.OrthodoxEaster(year));
        Assert.Equal(DateTime.Parse(western, CultureInfo.InvariantCulture), BusinessCalendar.WesternEaster(year));
    }

    [Fact]
    public void Greek_bank_holidays_2026_include_the_movable_feasts()
    {
        var days = BusinessCalendar.GreekBankHolidays(2026).Select(h => BusinessCalendar.Key(h.Day)).ToList();
        Assert.Contains("2026-02-23", days);   // Clean Monday
        Assert.Contains("2026-04-10", days);   // Orthodox Good Friday
        Assert.Contains("2026-04-13", days);   // Orthodox Easter Monday
        Assert.Contains("2026-06-01", days);   // Whit Monday
        Assert.Contains("2026-10-28", days);
        Assert.Equal(12, days.Count);
    }

    [Fact]
    public void Target2_holidays_2026()
    {
        var days = BusinessCalendar.Target2Holidays(2026).Select(h => BusinessCalendar.Key(h.Day)).ToList();
        Assert.Equal(new[] { "2026-01-01", "2026-04-03", "2026-04-06", "2026-05-01", "2026-12-25", "2026-12-26" }, days);
    }
}
