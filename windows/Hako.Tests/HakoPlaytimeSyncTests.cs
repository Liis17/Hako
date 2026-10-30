using Hako.Core;
using Hako.Core.Playtime;

namespace Hako.Tests;

[TestClass]
public sealed class HakoPlaytimeSyncTests
{
    [TestMethod]
    public async Task LogoutAppliesDurableBoundaryWhilePreviousUploadIsStillBlocked()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        var store = new PlaytimeLedgerStore(temporary.Paths.GameRoot);
        await store.MutateAsync(ledger => {
            ledger.Owners["guest"] = new PlaytimeOwnerRecord { TotalSeconds = 42 };
            return true;
        });
        var session = Session();
        using var account = new HakoAccountCoordinator(sessionStore: new MemoryStore(session));
        await account.InitializeAsync();
        var api = new BlockingApi();
        await using var playtime = new PlaytimeCoordinator(temporary.Paths.GameRoot);
        await using var sync = new HakoPlaytimeSync(playtime, account, api);
        await sync.InitializeAsync();
        await api.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A starting session supplies an observable ledger change during the logout boundary.
        var tracking = await playtime.BeginSessionAsync(Guid.NewGuid(), null);
        Assert.IsNotNull(tracking);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async void ObserveBoundary(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            try { if ((await store.ReadAsync()).HakoAccountId is null) applied.TrySetResult(); }
            catch (Exception error) { applied.TrySetException(error); }
        }
        sync.PropertyChanged += ObserveBoundary;
        try
        {
            Assert.IsTrue(await account.ClearLocalSessionAsync());
            // Observe automatic application; do not call the boundary drain from the test.
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var ledger = await store.ReadAsync();
            Assert.IsNull(ledger.HakoAccountId);
            Assert.IsNull(ledger.Sessions[tracking.SessionID].HakoAccountId);
            Assert.IsFalse(api.Release.Task.IsCompleted);
            Assert.AreEqual(session.Account.Id, ledger.Uploads.Values.Single().AccountId);
        }
        finally { sync.PropertyChanged -= ObserveBoundary; api.Release.TrySetResult(); }
    }

    [TestMethod]
    public async Task LostResponseRetriesSameUuidAfterRestartAndNeverUploadsToAnotherAccount()
    {
        await using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.GameRoot);
        var store = new PlaytimeLedgerStore(temporary.Paths.GameRoot);
        await store.MutateAsync(ledger => {
            ledger.Owners["guest"] = new PlaytimeOwnerRecord { TotalSeconds = 42 };
            return true;
        });
        var first = Session();
        var api = new RecordingApi { LoseNextResponse = true };
        await using (var playtime = new PlaytimeCoordinator(temporary.Paths.GameRoot))
        {
            using var account = new HakoAccountCoordinator(sessionStore: new MemoryStore(first));
            await account.InitializeAsync();
            await using var sync = new HakoPlaytimeSync(playtime, account, api);
            var finished = NextRefresh(sync);
            await sync.InitializeAsync();
            await finished.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(sync.HasError);
            Assert.AreEqual(1, (await store.ReadAsync()).Uploads.Count);
            Assert.AreEqual(42000L, api.Total);
        }
        var original = (await store.ReadAsync()).Uploads.Values.Single();
        var second = Session();
        await using (var playtime = new PlaytimeCoordinator(temporary.Paths.GameRoot))
        {
            using var account = new HakoAccountCoordinator(sessionStore: new MemoryStore(second));
            await account.InitializeAsync();
            await using var sync = new HakoPlaytimeSync(playtime, account, api);
            var finished = NextRefresh(sync);
            await sync.InitializeAsync();
            await finished.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, api.Attempts.Count);
            Assert.AreEqual(first.Account.Id, (await store.ReadAsync()).Uploads.Values.Single().AccountId);
        }
        await using (var playtime = new PlaytimeCoordinator(temporary.Paths.GameRoot))
        {
            using var account = new HakoAccountCoordinator(sessionStore: new MemoryStore(first));
            await account.InitializeAsync();
            await using var sync = new HakoPlaytimeSync(playtime, account, api);
            var finished = NextRefresh(sync);
            await sync.InitializeAsync();
            await finished.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(2, api.Attempts.Count);
            Assert.AreEqual(original.SessionId, api.Attempts[1]);
            Assert.AreEqual(42000L, api.Total);
            Assert.AreEqual(42000L, sync.TotalDurationMs);
            Assert.AreEqual(0, (await store.ReadAsync()).Uploads.Count);
            Assert.AreEqual(0, (await store.ReadAsync()).Owners["guest"].TotalSeconds);
        }
    }

    private static Task NextRefresh(HakoPlaytimeSync sync)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        System.ComponentModel.PropertyChangedEventHandler? handler = null;
        handler = (_, args) => {
            if (args.PropertyName != nameof(HakoPlaytimeSync.TotalDurationMs)) return;
            sync.PropertyChanged -= handler;
            completed.TrySetResult();
        };
        sync.PropertyChanged += handler;
        return completed.Task;
    }

    private static HakoSession Session() => new(Guid.NewGuid().ToString(),
        new HakoAccount(Guid.NewGuid().ToString(), "TestPlayer", DateTimeOffset.UtcNow),
        "Test PC", DateTimeOffset.UtcNow.AddDays(1), null);

    private sealed class BlockingApi : IHakoPlaytimeService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SubmitPlaytimeAsync(string token, Guid id, long duration, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        public Task<long> GetPlaytimeAsync(string token, CancellationToken cancellationToken) => Task.FromResult(42000L);
    }

    private sealed class RecordingApi : IHakoPlaytimeService
    {
        private readonly Dictionary<Guid, long> _accepted = [];
        public List<Guid> Attempts { get; } = [];
        public long Total => _accepted.Values.Sum();
        public bool LoseNextResponse { get; set; }
        public Task SubmitPlaytimeAsync(string token, Guid id, long duration, CancellationToken cancellationToken)
        {
            Attempts.Add(id);
            _accepted.TryAdd(id, duration);
            if (LoseNextResponse) { LoseNextResponse = false; throw new HttpRequestException("Response lost"); }
            return Task.CompletedTask;
        }
        public Task<long> GetPlaytimeAsync(string token, CancellationToken cancellationToken) => Task.FromResult(Total);
    }

    private sealed class MemoryStore(HakoSession session) : IHakoSessionStore
    {
        public Task<HakoSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<HakoSession?>(session);
        public Task<bool> SaveAsync(HakoSession value, Func<Action, bool> commitIfCurrent, CancellationToken cancellationToken = default) =>
            Task.FromResult(commitIfCurrent(() => { }));
        public Task<bool> ClearAsync(Func<Action, bool> commitIfCurrent, CancellationToken cancellationToken = default) =>
            Task.FromResult(commitIfCurrent(() => { }));
    }
}
