namespace Hako;

internal sealed class NameValidation
{
    private long _revision;
    private long _runningRevision = -1;

    public void Invalidate() => Interlocked.Increment(ref _revision);

    public async Task<NameValidationResult> ValidateAsync(string name, Func<string, Task<string?>> validate)
    {
        var revision = Interlocked.Read(ref _revision);
        var runningRevision = Interlocked.Read(ref _runningRevision);
        if (runningRevision == revision)
        {
            return new NameValidationResult(false, null);
        }

        while (Interlocked.CompareExchange(ref _runningRevision, revision, runningRevision) != runningRevision)
        {
            runningRevision = Interlocked.Read(ref _runningRevision);
            if (runningRevision == revision)
            {
                return new NameValidationResult(false, null);
            }
        }

        try
        {
            var errorKey = await validate(name);
            var isCurrent = Interlocked.Read(ref _revision) == revision;
            return new NameValidationResult(isCurrent, isCurrent ? errorKey : null);
        }
        finally
        {
            Interlocked.CompareExchange(ref _runningRevision, -1, revision);
        }
    }
}

internal readonly record struct NameValidationResult(bool IsCurrent, string? ErrorKey);
