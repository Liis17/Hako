using System.Globalization;

namespace Hako.Core;

public enum AppLanguage
{
    Russian,
    English
}

public static class AppLanguages
{
    public static AppLanguage Default => CultureInfo.CurrentUICulture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
        ? AppLanguage.Russian
        : AppLanguage.English;

    public static AppLanguage Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "ru" or "ru-ru" => AppLanguage.Russian,
        "en" or "en-us" => AppLanguage.English,
        _ => Default
    };

    public static string ToTag(this AppLanguage language) => language == AppLanguage.Russian ? "ru" : "en";
}
