namespace Hako.Core;

public interface IAccountStore
{
    Task<AccountSession?> LoadAsync(CancellationToken cancellationToken = default);

    Task<bool> SaveAsync(
        AccountSession account,
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken = default);

    Task<bool> ClearAsync(
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken = default);
}
