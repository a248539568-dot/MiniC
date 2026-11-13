using System.IO;

namespace MiniC.Widgets;

internal static class WeatherLegacyCleanup
{
    public static void RemoveObsoleteProviderCredentials()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniC");
        foreach (var name in new[] { "weather-key.dat", "weather-provider.dat" })
        {
            try
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
