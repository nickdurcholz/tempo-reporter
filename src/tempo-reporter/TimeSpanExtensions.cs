namespace tempo_reporter;

public static class TimeSpanExtensions
{
    public static string GetHoursMinutesString(this TimeSpan timeSpent)
    {
        return Math.Truncate(timeSpent.TotalHours) > 0.0
            ? $"{Math.Truncate(timeSpent.TotalHours):0}h {timeSpent.Minutes}m"
            : $"{timeSpent.Minutes}m";
    }


}