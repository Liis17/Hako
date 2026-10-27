using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hako.Core;

[SupportedOSPlatform("windows")]
public sealed class HakoSessionStore(string directory) : IHakoSessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hako.Session.v1");
    private readonly string _path = Path.Combine(directory, "hako-session.dat");

    public async Task<HakoSession?> LoadAsync(CancellationToken cancellationToken = default)
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
            var session = JsonSerializer.Deserialize(json, HakoJsonContext.Default.HakoSession);
            if (!IsValid(session))
            {
                throw new JsonException();
            }

            return session;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            throw new HakoSessionStoreException(exception);
        }
    }

    public async Task<bool> SaveAsync(
        HakoSession session,
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken = default)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(commitIfCurrent);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(session, HakoJsonContext.Default.HakoSession);
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
            throw new HakoSessionStoreException(exception);
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
        ArgumentNullException.ThrowIfNull(commitIfCurrent);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(commitIfCurrent(() => File.Delete(_path)));
    }

    private static bool IsValid(HakoSession? session) => session is not null
        && !string.IsNullOrWhiteSpace(session.Token)
        && session.Account is not null
        && !string.IsNullOrWhiteSpace(session.Account.Id)
        && HakoUsername.TryNormalize(session.Account.Username, out _)
        && !string.IsNullOrWhiteSpace(session.DeviceName)
        && session.DeviceName.Trim().Length <= 128
        && session.ExpiresAt != default;

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Hako session encryption requires Windows DPAPI.");
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
