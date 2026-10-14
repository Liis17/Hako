using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace Hako.Core;

public sealed class AccountStoreException : Exception
{
    public AccountStoreException() : base("The saved account could not be read or written.") { }
    public AccountStoreException(Exception innerException) : base("The saved account could not be read or written.", innerException) { }
}

[SupportedOSPlatform("windows")]
public sealed class AccountSessionStore(string directory) : IAccountStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hako.AccountSession.v1");
    private readonly string _path = Path.Combine(directory, "session.dat");

    public async Task<AccountSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            var json = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var account = JsonSerializer.Deserialize(json, HakoJsonContext.Default.AccountSession);
            if (account is null || string.IsNullOrWhiteSpace(account.Xuid) || string.IsNullOrWhiteSpace(account.Gamertag)
                || string.IsNullOrWhiteSpace(account.MicrosoftRefreshToken))
            {
                throw new JsonException();
            }

            return account;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            throw new AccountStoreException(exception);
        }
    }

    public async Task<bool> SaveAsync(
        AccountSession account,
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(account, HakoJsonContext.Default.AccountSession);
            var protectedBytes = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(protectedBytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return commitIfCurrent(() => File.Move(temporaryPath, _path, true));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            throw new AccountStoreException(exception);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public Task<bool> ClearAsync(
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(commitIfCurrent(() => File.Delete(_path)));
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Account session encryption requires Windows DPAPI.");
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
