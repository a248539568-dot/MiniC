using System.Globalization;

namespace MiniC.Widgets;

internal sealed record CalendarDayInfo(string Lunar, IReadOnlyList<string> Observances,
    string? OfficialStatus, string? OfficialName, string SolarTerm,
    string Ganzhi, IReadOnlyList<string> Appropriate, IReadOnlyList<string> Avoid)
{
    public IReadOnlyList<string> InternationalObservances { get; init; } = [];
    public string LunarDate { get; init; } = "";
}

internal static class CalendarDayInfoProvider
{
    private static readonly ChineseLunisolarCalendar LunarCalendar = new();
    private static readonly string[] MonthNames = ["正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊"];
    private static readonly string[] DayNames = ["初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"];

    public static CalendarDayInfo Get(DateTime date)
    {
        date = date.Date;
        var observances = new List<string>();
        var lunar = "";
        var lunarDate = "";
        if (date >= LunarCalendar.MinSupportedDateTime && date <= LunarCalendar.MaxSupportedDateTime)
        {
            var year = LunarCalendar.GetYear(date);
            var month = LunarCalendar.GetMonth(date);
            var day = LunarCalendar.GetDayOfMonth(date);
            var leap = LunarCalendar.GetLeapMonth(year);
            var isLeap = leap > 0 && month == leap;
            var ordinal = leap > 0 && month > leap ? month - 1 : month;
            lunar = day == 1 ? $"{(isLeap ? "闰" : "")}{MonthNames[ordinal - 1]}月" : DayNames[day - 1];
            lunarDate = $"{(isLeap ? "闰" : "")}{MonthNames[ordinal - 1]}月{DayNames[day - 1]}";
            if (!isLeap)
            {
                var traditional = (ordinal, day) switch
                {
                    (1, 1) => "春节", (1, 15) => "元宵节", (5, 5) => "端午节",
                    (7, 7) => "七夕", (8, 15) => "中秋节", (9, 9) => "重阳节",
                    (12, 8) => "腊八节", _ => null
                };
                if (traditional is not null) observances.Add(traditional);
                if (ordinal == 12 && day >= 29 && date < LunarCalendar.MaxSupportedDateTime.Date &&
                    LunarCalendar.GetMonth(date.AddDays(1)) == 1)
                    observances.Add("除夕");
            }
        }
        var solar = (date.Month, date.Day) switch
        {
            (1, 1) => "元旦", (2, 14) => "情人节", (3, 8) => "国际妇女节",
            (4, 1) => "愚人节", (5, 1) => "劳动节", (5, 4) => "青年节",
            (6, 1) => "儿童节", (9, 10) => "教师节", (10, 1) => "国庆节",
            (12, 25) => "圣诞节", _ => null
        };
        if (solar is not null && !observances.Contains(solar)) observances.Add(solar);
        // UN observances are commemorative dates, not worldwide statutory leave.
        var international = (date.Month, date.Day) switch
        {
            (2, 2) => "世界湿地日", (3, 8) => "国际妇女节", (3, 22) => "世界水日",
            (3, 23) => "世界气象日", (4, 7) => "世界卫生日", (4, 22) => "世界地球日",
            (4, 23) => "世界读书日", (5, 15) => "国际家庭日", (6, 5) => "世界环境日",
            (6, 8) => "世界海洋日", (9, 8) => "国际扫盲日", (9, 21) => "国际和平日",
            (10, 16) => "世界粮食日", (10, 24) => "联合国日",
            (12, 1) => "世界艾滋病日", (12, 10) => "世界人权日", _ => null
        };
        if (international is not null && !observances.Contains(international)) observances.Add(international);
        var (status, name) = Official2026(date);
        var almanac = Lunar.Solar.FromDate(date).Lunar;
        if (almanac.JieQi == "清明") observances.Add("清明节");
        return new CalendarDayInfo(lunar, observances, status, name,
            almanac.JieQi, $"{almanac.YearInGanZhi}年 {almanac.MonthInGanZhi}月 {almanac.DayInGanZhi}日",
            almanac.DayYi, almanac.DayJi)
        {
            InternationalObservances = international is null ? [] : [international],
            LunarDate = lunarDate
        };
    }

    private static (string? Status, string? Name) Official2026(DateTime date)
    {
        if (date.Year != 2026) return (null, null);
        var workdays = new[] { "01-04", "02-14", "02-28", "05-09", "09-20", "10-10" };
        if (workdays.Contains(date.ToString("MM-dd", CultureInfo.InvariantCulture))) return ("调休上班", null);
        var ranges = new (int Month, int From, int To, string Name)[]
        {
            (1, 1, 3, "元旦"), (2, 15, 23, "春节"), (4, 4, 6, "清明节"),
            (5, 1, 5, "劳动节"), (6, 19, 21, "端午节"),
            (9, 25, 27, "中秋节"), (10, 1, 7, "国庆节")
        };
        foreach (var range in ranges)
            if (date.Month == range.Month && date.Day >= range.From && date.Day <= range.To)
                return ("放假", range.Name);
        return (null, null);
    }
}
