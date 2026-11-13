namespace MiniC.Widgets;

internal static class WidgetDateTime
{
    public static bool TryToUtc(DateTime date, int hour, int minute, out DateTimeOffset? utc)
    {
        utc = null;
        if (hour is < 0 or > 23 || minute is < 0 or > 59) return false;
        var local = date.Date.AddHours(hour).AddMinutes(minute);
        if (TimeZoneInfo.Local.IsInvalidTime(local)) return false;
        utc = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
        return true;
    }
}
