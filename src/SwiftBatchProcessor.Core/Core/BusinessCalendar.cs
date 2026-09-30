using System.Globalization;
using System.Text.RegularExpressions;

namespace SwiftBatchApp.Core;

/// <summary>Working days, cut-off routing and holiday helpers. Holidays are "yyyy-MM-dd" strings.</summary>
public static class BusinessCalendar
{
    private static readonly Regex SplitRx = new(@"[\s,;|]+", RegexOptions.Compiled);
    private static readonly string[] DateFormats = { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "yyyyMMdd" };

    /// <summary>Parses a holiday list (comma/semicolon/space/newline separated). Invalid entries are ignored.</summary>
    public static HashSet<string> ParseHolidays(string? csv)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (string token in SplitRx.Split(csv))
        {
            if (DateTime.TryParseExact(token.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                set.Add(Key(d));
        }
        return set;
    }

    public static string FormatHolidays(IEnumerable<string> holidays) =>
        string.Join(", ", holidays.Distinct().OrderBy(h => h, StringComparer.Ordinal));

    public static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool IsWorkingDay(DateTime day, ISet<string> holidays) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(Key(day));

    public static DateTime NextWorkingDay(DateTime day, ISet<string> holidays)
    {
        DateTime d = day.Date.AddDays(1);
        for (int guard = 0; guard < 366 && !IsWorkingDay(d, holidays); guard++) d = d.AddDays(1);
        return d;
    }

    /// <summary>
    /// Files arriving after the cut-off, or on a weekend/holiday, are deferred to the next working day.
    /// Arriving exactly at the cut-off still counts as on time.
    /// </summary>
    public static (bool Defer, DateTime TargetDay) Route(DateTime now, TimeSpan cutoff, ISet<string> holidays)
    {
        if (!IsWorkingDay(now.Date, holidays)) return (true, NextWorkingDay(now.Date, holidays));
        if (now.TimeOfDay > cutoff) return (true, NextWorkingDay(now.Date, holidays));
        return (false, now.Date);
    }

    /// <summary>Accepts "13:00", "13.00", "1300", "13".</summary>
    public static TimeSpan ParseTime(string? s, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        string t = s.Trim().Replace('.', ':');
        if (TimeSpan.TryParseExact(t, new[] { @"h\:mm", @"hh\:mm", @"hh\:mm\:ss", "hhmm", "hh", "%h" },
                CultureInfo.InvariantCulture, out TimeSpan ts) && ts < TimeSpan.FromDays(1))
            return ts;
        return fallback;
    }

    public static string FormatTime(TimeSpan t) => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- suggested holidays

    /// <summary>Greek bank holidays for a year (fixed dates + Orthodox Easter based). A suggestion to review.</summary>
    public static IEnumerable<(DateTime Day, string Name)> GreekBankHolidays(int year)
    {
        DateTime easter = OrthodoxEaster(year);
        yield return (new DateTime(year, 1, 1), "New Year's Day");
        yield return (new DateTime(year, 1, 6), "Epiphany");
        yield return (easter.AddDays(-48), "Clean Monday");
        yield return (new DateTime(year, 3, 25), "Independence Day");
        yield return (easter.AddDays(-2), "Orthodox Good Friday");
        yield return (easter.AddDays(1), "Orthodox Easter Monday");
        yield return (new DateTime(year, 5, 1), "Labour Day");
        yield return (easter.AddDays(50), "Whit Monday (Holy Spirit)");
        yield return (new DateTime(year, 8, 15), "Assumption");
        yield return (new DateTime(year, 10, 28), "Ochi Day");
        yield return (new DateTime(year, 12, 25), "Christmas Day");
        yield return (new DateTime(year, 12, 26), "Synaxis of the Theotokos");
    }

    /// <summary>TARGET2 (T2) closing days: 1 Jan, Good Friday, Easter Monday (Western), 1 May, 25–26 Dec.</summary>
    public static IEnumerable<(DateTime Day, string Name)> Target2Holidays(int year)
    {
        DateTime easter = WesternEaster(year);
        yield return (new DateTime(year, 1, 1), "New Year's Day");
        yield return (easter.AddDays(-2), "Good Friday (TARGET2)");
        yield return (easter.AddDays(1), "Easter Monday (TARGET2)");
        yield return (new DateTime(year, 5, 1), "Labour Day");
        yield return (new DateTime(year, 12, 25), "Christmas Day");
        yield return (new DateTime(year, 12, 26), "Boxing Day");
    }

    /// <summary>Orthodox Easter (Meeus Julian algorithm, converted to the Gregorian calendar).</summary>
    public static DateTime OrthodoxEaster(int year)
    {
        int a = year % 4, b = year % 7, c = year % 19;
        int d = (19 * c + 15) % 30;
        int e = (2 * a + 4 * b - d + 34) % 7;
        int month = (d + e + 114) / 31;
        int day = (d + e + 114) % 31 + 1;
        int julianToGregorian = year / 100 - year / 400 - 2;   // 13 days for 1900–2099
        return new DateTime(year, month, day).AddDays(julianToGregorian);
    }

    /// <summary>Western Easter (anonymous Gregorian algorithm).</summary>
    public static DateTime WesternEaster(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100;
        int d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(year, month, day);
    }
}
