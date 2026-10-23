using Hako.Core.Instances;

namespace Hako;

internal static class BackupResourceKeys
{
    internal static string FormatFailure(BackupFailure? failure, Func<string, string> get)
    {
        if (failure is null) return get("BackupFailed");
        var message = get(FailureKey(failure.Reason));
        if (!string.IsNullOrWhiteSpace(failure.Diagnostic))
            message += "\n" + string.Format(get("BackupDiagnosticFormat"), failure.Diagnostic);
        if (!string.IsNullOrWhiteSpace(failure.DiagnosticPath))
            message += "\n" + string.Format(get("BackupDiagnosticPathFormat"), failure.DiagnosticPath);
        return message;
    }

    internal static string FailureKey(BackupFailureReason reason) => reason switch
        {
            BackupFailureReason.BuildUnavailable => "BackupBuildUnavailable",
            BackupFailureReason.BuildNotReady => "BackupBuildNotReady",
            BackupFailureReason.BuildInstalling => "BackupBuildInstalling",
            BackupFailureReason.BuildRunning => "BackupBuildRunning",
            BackupFailureReason.BuildFilesBusy => "BackupBuildFilesBusy",
            BackupFailureReason.WorldUnavailable => "BackupWorldUnavailable",
            BackupFailureReason.ReservedFolderOccupied => "BackupReservedFolderOccupied",
            BackupFailureReason.ReparsePointNotAllowed => "BackupReparsePointNotAllowed",
            BackupFailureReason.DestinationConflict => "BackupDestinationConflict",
            BackupFailureReason.SourceChanged => "BackupSourceChanged",
            BackupFailureReason.Cancelled => "BackupCancelled",
            _ => "BackupFailed"
        };
}
