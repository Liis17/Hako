namespace Hako.Core.Instances;

public enum BackupFailureReason
{
    BuildUnavailable,
    BuildNotReady,
    BuildInstalling,
    BuildRunning,
    BuildFilesBusy,
    WorldUnavailable,
    ReservedFolderOccupied,
    ReparsePointNotAllowed,
    DestinationConflict,
    SourceChanged,
    Cancelled,
    OperationFailed
}

public sealed record BackupFailure(BackupFailureReason Reason, string Diagnostic, string? DiagnosticPath = null);

public sealed record BackupResult(string? Path, BackupFailure? Failure)
{
    public bool Succeeded => Path is not null && Failure is null;

    internal static BackupResult Success(string path) => new(path, null);
    internal static BackupResult Failed(BackupFailureReason reason, string diagnostic, string? diagnosticPath = null) =>
        new(null, new BackupFailure(reason, diagnostic, diagnosticPath));
}

internal static class BackupFailureMapping
{
    internal static BackupFailureReason From(Exception exception) => exception switch
    {
        OperationCanceledException => BackupFailureReason.Cancelled,
        BackupFolderOccupiedException => BackupFailureReason.ReservedFolderOccupied,
        InstanceFileTreeReparseException => BackupFailureReason.ReparsePointNotAllowed,
        InstanceFileTreeChangedException => BackupFailureReason.SourceChanged,
        InstanceStorageException { Error: InstanceStorageError.BuildInstalling } => BackupFailureReason.BuildInstalling,
        InstanceStorageException { Error: InstanceStorageError.BuildRunning } => BackupFailureReason.BuildRunning,
        InstanceStorageException { Error: InstanceStorageError.BuildFilesBusy } => BackupFailureReason.BuildFilesBusy,
        InstanceStorageException { Error: InstanceStorageError.InstanceRegistryUnavailable or InstanceStorageError.InstanceRegistryNotLoaded } => BackupFailureReason.BuildUnavailable,
        InstanceStorageException { Error: InstanceStorageError.ReparsePointNotAllowed } => BackupFailureReason.ReparsePointNotAllowed,
        InstanceStorageException { Error: InstanceStorageError.DuplicateBuildName or InstanceStorageError.BuildFolderExists } => BackupFailureReason.DestinationConflict,
        InstanceWorldException { Error: InstanceWorldFailureReason.ReparsePointNotAllowed } => BackupFailureReason.ReparsePointNotAllowed,
        InstanceWorldException => BackupFailureReason.WorldUnavailable,
        _ => BackupFailureReason.OperationFailed
    };
}

internal sealed class BackupFolderOccupiedException(string folder)
    : IOException($"The build folder '{folder}' is reserved for backups.");

internal sealed class InstanceFileTreeChangedException(string message) : IOException(message);
internal sealed class InstanceFileTreeReparseException(string message) : IOException(message);
