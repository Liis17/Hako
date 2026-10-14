using Windows.Storage;

namespace Hako;

internal static class AppDataDirectory
{
    public static string GetLocalPath()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current.Id;
            return ApplicationData.Current.LocalFolder.Path;
        }
        catch (InvalidOperationException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hako");
        }
    }
}
