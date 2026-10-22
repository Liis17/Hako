using Hako.Core.Instances;
using System.Collections.Generic;

namespace Hako;

internal static class ContentResourceKeys
{
    internal static string FormatFailure(ContentFailure failure, Func<string, string> getString)
    {
        var template = getString(Failure(failure.Code));
        try
        {
            return failure.Code switch
            {
                ContentFailureCode.FabricApiIncompatible => string.Format(template,
                    failure.Arguments.GetValueOrDefault("version", ""),
                    failure.Arguments.GetValueOrDefault("loader", ""),
                    failure.Arguments.GetValueOrDefault("java", "")),
                ContentFailureCode.BulkUpdateFailed => string.Format(template,
                    failure.Arguments.GetValueOrDefault("failureCount", "0")),
                _ => template
            };
        }
        catch (FormatException) { return template; }
    }

    internal static string FormatFailureDetails(ContentFailure failure, Func<string, string> getString)
    {
        var message = FormatFailure(failure, getString);
        return failure.Arguments.TryGetValue("diagnostic", out var diagnostic) && !string.IsNullOrWhiteSpace(diagnostic)
            ? $"{message}\n{getString("ContentDiagnosticLabel")} {diagnostic}"
            : message;
    }

    public static string ConfirmationTitle(ContentConfirmationCode code) => code switch
    {
        ContentConfirmationCode.Dependencies => "ContentConfirmationDependenciesTitle",
        ContentConfirmationCode.IncompatibleContent => "ContentConfirmationIncompatibleTitle",
        ContentConfirmationCode.ReplaceExisting => "ContentConfirmationReplaceTitle",
        ContentConfirmationCode.DeleteContent => "ContentConfirmationDeleteTitle",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    public static string ConfirmationMessage(ContentConfirmationCode code) => code switch
    {
        ContentConfirmationCode.Dependencies => "ContentConfirmationDependenciesMessage",
        ContentConfirmationCode.IncompatibleContent => "ContentConfirmationIncompatibleMessage",
        ContentConfirmationCode.ReplaceExisting => "ContentConfirmationReplaceMessage",
        ContentConfirmationCode.DeleteContent => "ContentConfirmationDeleteMessage",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    public static string ConfirmationPrimary(ContentConfirmationCode code) => code switch
    {
        ContentConfirmationCode.Dependencies => "ContentConfirmationDependenciesPrimary",
        ContentConfirmationCode.IncompatibleContent => "ContentConfirmationIncompatiblePrimary",
        ContentConfirmationCode.ReplaceExisting => "ContentConfirmationReplacePrimary",
        ContentConfirmationCode.DeleteContent => "ContentConfirmationDeletePrimary",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    public static string ConfirmationAlternative(ContentConfirmationCode code) => code switch
    {
        ContentConfirmationCode.Dependencies => "ContentConfirmationDependenciesAlternative",
        ContentConfirmationCode.IncompatibleContent => "ContentConfirmationIncompatibleAlternative",
        ContentConfirmationCode.ReplaceExisting => "ContentConfirmationReplaceAlternative",
        ContentConfirmationCode.DeleteContent => "ContentConfirmationDeleteAlternative",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    public static string ConfirmationNote(ContentConfirmationNoteCode code) => code switch
    {
        ContentConfirmationNoteCode.DisabledWillBeEnabled => "ContentNoteDisabledWillBeEnabled",
        ContentConfirmationNoteCode.Unsupported => "ContentNoteUnsupported",
        ContentConfirmationNoteCode.RequiresFabric => "ContentNoteRequiresFabric",
        ContentConfirmationNoteCode.NoCompatibleVersion => "ContentNoteNoCompatibleVersion",
        ContentConfirmationNoteCode.ProjectLimitReached => "ContentNoteProjectLimitReached",
        ContentConfirmationNoteCode.ProjectUnavailable => "ContentNoteProjectUnavailable",
        ContentConfirmationNoteCode.FabricLoaderRequirement => "ContentNoteFabricLoaderRequirement",
        ContentConfirmationNoteCode.JavaRequirement => "ContentNoteJavaRequirement",
        ContentConfirmationNoteCode.IncompatibleWith => "ContentNoteIncompatibleWith",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    public static string Failure(ContentFailureCode code) => code switch
    {
        ContentFailureCode.BuildUnavailable => "ContentFailureBuildUnavailable",
        ContentFailureCode.BuildBusy => "ContentFailureBuildBusy",
        ContentFailureCode.BuildInstalling => "ContentFailureBuildInstalling",
        ContentFailureCode.ModRequiresFabric => "ContentFailureModRequiresFabric",
        ContentFailureCode.InvalidChannel => "ContentFailureInvalidChannel",
        ContentFailureCode.NoCompatibleVersion => "ContentFailureNoCompatibleVersion",
        ContentFailureCode.RegistryCorrupt => "ContentFailureRegistryCorrupt",
        ContentFailureCode.RegistryUnavailable => "ContentFailureRegistryUnavailable",
        ContentFailureCode.StaleContent => "ContentFailureStaleContent",
        ContentFailureCode.ConfirmationRequired => "ContentFailureConfirmationRequired",
        ContentFailureCode.ConfirmationDeclined => "ContentFailureConfirmationDeclined",
        ContentFailureCode.FabricApiIncompatible => "ContentFailureFabricApiIncompatible",
        ContentFailureCode.ModrinthRequestFailed => "ContentFailureModrinthRequestFailed",
        ContentFailureCode.ContentOperationFailed => "ContentFailureOperationFailed",
        ContentFailureCode.BulkUpdateFailed => "ContentFailureBulkUpdateFailed",
        ContentFailureCode.Cancelled => "ContentFailureCancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };
}
