using System.Globalization;

namespace Hako.Core.Playtime;

public static class PlaytimeFormatter
{
    public static string Format(double seconds, AppLanguage language, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        if (!double.IsFinite(seconds) || seconds <= 0) return localize("PlaytimeZero");
        var minutes = (long)Math.Floor(seconds / 60);
        if (minutes == 0) return localize("PlaytimeLessThanMinute");
        if (minutes < 60) return Unit(minutes, "Minute", language, localize);

        var hours = minutes / 60;
        var remainingMinutes = minutes % 60;
        if (hours < 24)
        {
            return remainingMinutes == 0
                ? Unit(hours, "Hour", language, localize)
                : Unit(hours, "Hour", language, localize) + " " + Unit(remainingMinutes, "Minute", language, localize);
        }

        var days = hours / 24;
        var remainingHours = hours % 24;
        return remainingHours == 0
            ? Unit(days, "Day", language, localize)
            : Unit(days, "Day", language, localize) + " " + Unit(remainingHours, "Hour", language, localize);
    }

    private static string Unit(long value, string unit, AppLanguage language, Func<string, string> localize)
    {
        var category = language == AppLanguage.Russian
            ? value % 10 == 1 && value % 100 != 11 ? "One"
                : value % 10 is >= 2 and <= 4 && value % 100 is not (>= 12 and <= 14) ? "Few" : "Many"
            : value == 1 ? "One" : "Other";
        return string.Format(CultureInfo.InvariantCulture, localize("Playtime" + unit + category), value);
    }
}
