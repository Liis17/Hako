using Hako.Core;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Hako;

internal static class HakoStrings
{
    private static readonly ResourceManager Manager = new();
    private static readonly ResourceMap ResourceMap = Manager.MainResourceMap.GetSubtree("Resources");
    private static readonly ResourceContext Context = Manager.CreateResourceContext();
    private static AppLanguage _language;

    public static event EventHandler? LanguageChanged;

    public static string Get(string key) => ResourceMap.GetValue(key, Context).ValueAsString;

    public static void SetLanguage(AppLanguage language, bool force = false)
    {
        if (!force && _language == language)
        {
            return;
        }

        _language = language;
        Context.QualifierValues["Language"] = language == AppLanguage.Russian ? "ru-RU" : "en-US";
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }
}
