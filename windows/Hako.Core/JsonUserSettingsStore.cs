using System.Text.Json;

namespace Hako.Core;

public sealed class JsonUserSettingsStore(string directory) : IUserSettingsStore
{
    private readonly string _path = Path.Combine(directory, "settings.json");

    public async Task<UserSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllBytesAsync(_path, cancellationToken);
            return JsonSerializer.Deserialize(json, HakoJsonContext.Default.UserSettings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, HakoJsonContext.Default.UserSettings);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
