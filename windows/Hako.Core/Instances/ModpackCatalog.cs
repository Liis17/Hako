using System.ComponentModel;
using Hako.Core.Minecraft;

namespace Hako.Core.Instances;

/// <summary>Search and pagination state for the ready-made Modrinth modpack catalog.</summary>
public sealed class ModpackCatalog : INotifyPropertyChanged, IDisposable
{
    private readonly ModrinthClient _client;
    private readonly object _sync = new();
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<ModrinthProject> _projects = [];
    private Exception? _error;
    private string _query = "";
    private string? _minecraft;
    private ModrinthSort _sort = ModrinthSort.Relevance;
    private int _nextOffset;
    private int _total;
    private long _generation;
    private bool _isLoading;
    private bool _isLoadingMore;
    private bool _disposed;

    public ModpackCatalog(ModrinthClient client) => _client = client;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ModrinthProject> Projects { get { lock (_sync) return _projects; } }
    public Exception? Error { get { lock (_sync) return _error; } }
    public string Query { get { lock (_sync) return _query; } }
    public string? Minecraft { get { lock (_sync) return _minecraft; } }
    public ModrinthSort Sort { get { lock (_sync) return _sort; } }
    public int NextOffset { get { lock (_sync) return _nextOffset; } }
    public int Total { get { lock (_sync) return _total; } }
    public bool IsLoading { get { lock (_sync) return _isLoading; } }
    public bool IsLoadingMore { get { lock (_sync) return _isLoadingMore; } }
    public bool HasMore { get { lock (_sync) return _nextOffset < _total; } }

    public async Task SearchAsync(string query, string? minecraft, ModrinthSort sort,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource search;
        long generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            search = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _searchCancellation = search;
            generation = ++_generation;
            _query = query;
            _minecraft = minecraft;
            _sort = sort;
            _projects = [];
            _error = null;
            _nextOffset = 0;
            _total = 0;
            _isLoading = true;
            _isLoadingMore = false;
        }
        NotifyAll();

        try
        {
            var page = await _client.SearchModpacksAsync(query, minecraft, sort, cancellationToken: search.Token)
                .ConfigureAwait(false);
            lock (_sync)
            {
                if (generation != _generation) return;
                _projects = page.Hits.Where(project => project.ProjectType == "modpack")
                    .DistinctBy(project => project.Id, StringComparer.Ordinal).ToArray();
                _nextOffset = page.Hits.Count;
                _total = page.TotalHits;
            }
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (generation == _generation) _error = exception;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (generation == _generation) _isLoading = false;
            }
            NotifyAll();
        }
    }

    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        long generation;
        int offset;
        string query;
        string? minecraft;
        ModrinthSort sort;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isLoading || _isLoadingMore || _nextOffset >= _total || _searchCancellation is null) return;
            generation = _generation;
            offset = _nextOffset;
            query = _query;
            minecraft = _minecraft;
            sort = _sort;
            linked = CancellationTokenSource.CreateLinkedTokenSource(_searchCancellation.Token, cancellationToken);
            _isLoadingMore = true;
            _error = null;
        }
        NotifyAll();

        try
        {
            var page = await _client.SearchModpacksAsync(query, minecraft, sort, offset, cancellationToken: linked.Token)
                .ConfigureAwait(false);
            lock (_sync)
            {
                if (generation != _generation) return;
                var projects = _projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
                foreach (var project in page.Hits.Where(project => project.ProjectType == "modpack"))
                {
                    projects.TryAdd(project.Id, project);
                }
                _projects = projects.Values.ToArray();
                _nextOffset += page.Hits.Count;
                _total = page.TotalHits;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (generation == _generation) _error = exception;
            }
        }
        finally
        {
            linked.Dispose();
            lock (_sync)
            {
                if (generation == _generation) _isLoadingMore = false;
            }
            NotifyAll();
        }
    }

    public Task<IReadOnlyList<ModrinthVersion>> VersionsAsync(
        ModrinthProject project,
        string? minecraft,
        CancellationToken cancellationToken = default) =>
        _client.GetModpackVersionsAsync(project.Id, minecraft, cancellationToken);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
        }
    }

    private void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Projects)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NextOffset)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Total)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoadingMore)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMore)));
    }
}
