namespace InputAtlas.Core;

public static class TimeBuckets
{
    public const long FiveMinutesSeconds = 300;
    public const long OneHourSeconds = 3600;

    public static long AlignFiveMinutes(long unixSeconds) => Align(unixSeconds, FiveMinutesSeconds);

    public static long AlignHour(long unixSeconds) => Align(unixSeconds, OneHourSeconds);

    public static long Align(long unixSeconds, long intervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unixSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalSeconds, 0);
        return unixSeconds - (unixSeconds % intervalSeconds);
    }

    public static StatisticsGranularity ChooseGranularity(TimeSpan visibleSpan)
    {
        if (visibleSpan <= TimeSpan.FromDays(2)) return StatisticsGranularity.FiveMinutes;
        if (visibleSpan <= TimeSpan.FromDays(7)) return StatisticsGranularity.FifteenMinutes;
        if (visibleSpan <= TimeSpan.FromDays(14)) return StatisticsGranularity.ThirtyMinutes;
        if (visibleSpan <= TimeSpan.FromDays(45)) return StatisticsGranularity.OneHour;
        if (visibleSpan <= TimeSpan.FromDays(120)) return StatisticsGranularity.SixHours;
        if (visibleSpan <= TimeSpan.FromDays(18 * 30.4375)) return StatisticsGranularity.OneDay;
        if (visibleSpan <= TimeSpan.FromDays(5 * 365.2425)) return StatisticsGranularity.OneWeek;
        return StatisticsGranularity.OneMonth;
    }
}

