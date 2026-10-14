namespace Hako.Core;

public interface IUserSettingsStore
{
    Task<UserSettings?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
