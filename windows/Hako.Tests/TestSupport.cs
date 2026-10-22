using Hako.Core;
using System.Net;
using System.Text;

namespace Hako.Tests;

internal sealed class CallbackHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        return callback(request, cancellationToken);
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public int PendingTimerCount
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(timer => timer.IsPending);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        Change(timer, dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_lock)
        {
            _now += amount;
            foreach (var timer in _timers)
            {
                timer.FireIfDue(_now, callbacks);
            }
        }

        foreach (var (callback, state) in callbacks)
        {
            callback(state);
        }
    }

    private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            timer.SetDue(_now, dueTime, period);
            if (!_timers.Contains(timer))
            {
                _timers.Add(timer);
            }
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            timer.MarkDisposed();
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _due;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        public bool IsPending => !_disposed && _due is not null;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            owner.Change(this, dueTime, period);
            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void SetDue(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
        {
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            _period = period;
        }

        public void FireIfDue(DateTimeOffset now, List<(TimerCallback Callback, object? State)> callbacks)
        {
            if (_due is not { } due || due > now || _disposed)
            {
                return;
            }

            callbacks.Add((callback, state));
            _due = _period == Timeout.InfiniteTimeSpan ? null : due + _period;
        }

        public void MarkDisposed()
        {
            _disposed = true;
            _due = null;
        }
    }
}

internal sealed class MemorySettingsStore(UserSettings? settings = null) : IUserSettingsStore
{
    public UserSettings? Settings { get; private set; } = settings;
    public bool FailLoad { get; set; }
    public bool FailSave { get; set; }

    public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (FailLoad)
        {
            throw new IOException("settings read failed");
        }

        return Task.FromResult(Settings);
    }

    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        if (FailSave)
        {
            throw new IOException("settings write failed");
        }

        Settings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class PausingSettingsStore(UserSettings? settings = null) : IUserSettingsStore
{
    private int _saveCount;

    public UserSettings? Settings { get; private set; } = settings;
    public int FailFirstSaves { get; set; }
    public TaskCompletionSource FirstSaveReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Settings);

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        var saveNumber = Interlocked.Increment(ref _saveCount);
        if (saveNumber == 1)
        {
            FirstSaveReached.TrySetResult();
            await ReleaseFirstSave.Task;
        }

        if (saveNumber <= FailFirstSaves)
        {
            throw new IOException("settings write failed");
        }

        Settings = settings;
    }
}

internal sealed class MemoryAccountStore(AccountSession? account = null) : IAccountStore
{
    public AccountSession? SavedAccount { get; private set; } = account;
    public bool FailLoad { get; set; }
    public bool FailSave { get; set; }
    public bool FailClear { get; set; }

    public Task<AccountSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (FailLoad)
        {
            throw new AccountStoreException();
        }

        return Task.FromResult(SavedAccount);
    }

    public Task<bool> SaveAsync(AccountSession account, Func<Action, bool> commitIfCurrent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailSave)
        {
            throw new AccountStoreException();
        }

        return Task.FromResult(commitIfCurrent(() => SavedAccount = account));
    }

    public Task<bool> ClearAsync(Func<Action, bool> commitIfCurrent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailClear)
        {
            throw new IOException("account delete failed");
        }

        return Task.FromResult(commitIfCurrent(() => SavedAccount = null));
    }
}

internal sealed class PausingAccountStore(AccountSession? account = null) : IAccountStore
{
    private int _saveCount;

    public AccountSession? SavedAccount { get; private set; } = account;
    public TaskCompletionSource FirstSaveReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirstSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AccountSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(SavedAccount);

    public async Task<bool> SaveAsync(AccountSession account, Func<Action, bool> commitIfCurrent, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _saveCount) == 1)
        {
            FirstSaveReached.TrySetResult();
            await ReleaseFirstSave.Task;
        }

        return commitIfCurrent(() => SavedAccount = account);
    }

    public Task<bool> ClearAsync(Func<Action, bool> commitIfCurrent, CancellationToken cancellationToken = default) =>
        Task.FromResult(commitIfCurrent(() => SavedAccount = null));
}

internal sealed class SequenceAuthService : IMicrosoftAuthService
{
    private readonly Func<int, Task<AccountSession>> _signIn;
    private int _attempt;

    public SequenceAuthService(Func<int, Task<AccountSession>>? signIn = null)
    {
        _signIn = signIn ?? (_ => Task.FromResult(TestData.Account()));
    }

    public TaskCompletionSource TokenWaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseTokenWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HoldTokenWait { get; set; }
    public int Attempt => Volatile.Read(ref _attempt);

    public Task<DeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken) => Task.FromResult(TestData.DeviceCode());

    public async Task<MicrosoftToken> WaitForTokenAsync(DeviceCode deviceCode, CancellationToken cancellationToken)
    {
        TokenWaitStarted.TrySetResult();
        if (HoldTokenWait)
        {
            await ReleaseTokenWait.Task.WaitAsync(cancellationToken);
        }

        return new MicrosoftToken("ms-access", "ms-refresh", "user@example.com");
    }

    public Task<AccountSession> SignInAsync(MicrosoftToken token, CancellationToken cancellationToken) => _signIn(Interlocked.Increment(ref _attempt));

    public async Task<MinecraftConnection> ConnectMinecraftAsync(
        string refreshToken,
        Func<string, CancellationToken, Task> persistRefreshToken,
        CancellationToken cancellationToken)
    {
        await persistRefreshToken(refreshToken + "-rotated", cancellationToken);
        return TestData.MinecraftConnection();
    }
}

internal sealed class RefreshAuthService : IMicrosoftAuthService
{
    private readonly Func<string, Func<string, CancellationToken, Task>, CancellationToken, Task<MinecraftConnection>> _connect;
    private int _connectionCount;

    public RefreshAuthService(
        Func<string, Func<string, CancellationToken, Task>, CancellationToken, Task<MinecraftConnection>>? connect = null)
    {
        _connect = connect ?? DefaultConnectAsync;
    }

    public int ConnectionCount => Volatile.Read(ref _connectionCount);
    public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<DeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken) => Task.FromResult(TestData.DeviceCode());
    public Task<MicrosoftToken> WaitForTokenAsync(DeviceCode deviceCode, CancellationToken cancellationToken) =>
        Task.FromResult(new MicrosoftToken("access", "refresh", null));
    public Task<AccountSession> SignInAsync(MicrosoftToken token, CancellationToken cancellationToken) => Task.FromResult(TestData.Account());

    public Task<MinecraftConnection> ConnectMinecraftAsync(
        string refreshToken,
        Func<string, CancellationToken, Task> persistRefreshToken,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectionCount);
        ConnectStarted.TrySetResult();
        return _connect(refreshToken, persistRefreshToken, cancellationToken);
    }

    private static async Task<MinecraftConnection> DefaultConnectAsync(
        string refreshToken,
        Func<string, CancellationToken, Task> persistRefreshToken,
        CancellationToken cancellationToken)
    {
        await persistRefreshToken("rotated-refresh", cancellationToken);
        return TestData.MinecraftConnection();
    }
}

internal static class TestData
{
    public static DeviceCode DeviceCode() => new("device-secret", "ABCD-EFGH", new Uri("https://microsoft.com/link"), 900, 5);

    public static AccountSession Account(string xuid = "123", string gamertag = "HakoPlayer") =>
        new(xuid, gamertag, "player@example.com", null, "profile-id", "Player", "refresh-secret", "minecraft-secret", DateTimeOffset.Parse("2025-01-01T01:00:00Z"));

    public static MinecraftConnection MinecraftConnection() =>
        new("profile-id", "Player", "minecraft-connected-secret", DateTimeOffset.Parse("2025-01-01T02:00:00Z"));

    public static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    public static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 2000; i++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(1);
        }

        Assert.Fail("The expected asynchronous state was not reached.");
    }

    public static Dictionary<string, string> ParseForm(string? body) => (body ?? string.Empty)
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(
            pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString((pair.Length > 1 ? pair[1] : string.Empty).Replace('+', ' ')));
}

internal sealed class TemporaryDirectory : IDisposable, IAsyncDisposable
{
    public TemporaryDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "Hako.Tests", Guid.NewGuid().ToString("N"));
        Paths = HakoPaths.CreateIsolated(Path.Combine(Root, "Game"), Path.Combine(Root, "Data"));
    }

    public string Root { get; }
    public HakoPaths Paths { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
