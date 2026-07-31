using System;
using System.Collections.Generic;
using System.Globalization;

public static class OfficeHolidayCalendar
{
    private readonly struct Holiday
    {
        public readonly DateTime date;
        public readonly string name;

        public Holiday(DateTime date, string name)
        {
            this.date = date.Date;
            this.name = name;
        }
    }

    public static string Describe(DateTime worldDate)
    {
        DateTime today = worldDate.Date;
        List<Holiday> holidays = BuildYear(today.Year);
        holidays.AddRange(BuildYear(today.Year + 1));
        holidays.Sort((left, right) => left.date.CompareTo(right.date));

        List<string> todayNames = new();
        Holiday? upcoming = null;
        foreach (Holiday holiday in holidays)
        {
            int days = (holiday.date - today).Days;
            if (days == 0)
            {
                if (!todayNames.Contains(holiday.name))
                    todayNames.Add(holiday.name);
                continue;
            }
            if (days > 0 && days <= 7 && !upcoming.HasValue)
                upcoming = holiday;
        }

        if (todayNames.Count > 0)
            return "today is " + string.Join(" and ", todayNames);
        if (!upcoming.HasValue)
            return "";

        int daysUntil = (upcoming.Value.date - today).Days;
        return daysUntil == 1
            ? upcoming.Value.name + " is tomorrow"
            : upcoming.Value.name + " is in " + daysUntil + " days";
    }

    private static List<Holiday> BuildYear(int year)
    {
        List<Holiday> result = new()
        {
            new Holiday(new DateTime(year, 1, 1), "New Year's Day"),
            new Holiday(new DateTime(year, 2, 14), "Valentine's Day"),
            new Holiday(new DateTime(year, 3, 8), "International Women's Day"),
            new Holiday(new DateTime(year, 4, 4), "Qingming Festival"),
            new Holiday(new DateTime(year, 5, 1), "Labour Day"),
            new Holiday(new DateTime(year, 10, 1), "PRC National Day"),
            new Holiday(new DateTime(year, 10, 31), "Halloween"),
            new Holiday(new DateTime(year, 12, 25), "Christmas Day")
        };

        AddLunar(result, year, 1, 1, "Spring Festival");
        AddLunar(result, year, 1, 15, "Lantern Festival");
        AddLunar(result, year, 5, 5, "Dragon Boat Festival");
        AddLunar(result, year, 7, 7, "Qixi Festival");
        AddLunar(result, year, 8, 15, "Mid-Autumn Festival");
        return result;
    }

    private static void AddLunar(List<Holiday> target, int lunarYear,
        int lunarMonth, int lunarDay, string name)
    {
        ChineseLunisolarCalendar calendar = new();
        if (lunarYear < calendar.MinSupportedDateTime.Year
            || lunarYear > calendar.MaxSupportedDateTime.Year)
            return;

        try
        {
            int calendarMonth = lunarMonth;
            int leapMonth = calendar.GetLeapMonth(lunarYear);
            if (leapMonth > 0 && lunarMonth >= leapMonth)
                calendarMonth++;
            DateTime date = calendar.ToDateTime(
                lunarYear, calendarMonth, lunarDay, 0, 0, 0, 0);
            target.Add(new Holiday(date, name));
        }
        catch (ArgumentOutOfRangeException)
        {
            // The simulation still has the fixed-date calendar for unsupported years.
        }
    }
}
