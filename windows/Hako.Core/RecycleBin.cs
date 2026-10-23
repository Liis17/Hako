using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Hako.Core.Instances;

namespace Hako.Core;

public enum RecycleBinError
{
    OperationFailed
}

public sealed class RecycleBinException : IOException
{
    public RecycleBinException(string message, Exception? innerException = null)
        : base(message, innerException) => Error = RecycleBinError.OperationFailed;

    public RecycleBinError Error { get; }
}

public enum RecycleBinItemStatus
{
    Recycled,
    NotRecyclable,
    Failed,
    Cancelled
}

public sealed record RecycleBinItemResult(string Path, RecycleBinItemStatus Status, string? Error = null);
public sealed record RecycleBinResult(IReadOnlyList<RecycleBinItemResult> Items);

[SupportedOSPlatform("windows")]
public sealed class RecycleBin
{
    private readonly IRecycleBinOperation _operation;

    public RecycleBin() : this(new ShellRecycleBinOperation()) { }

    internal RecycleBin(IRecycleBinOperation operation) => _operation = operation;

    public Task<RecycleBinResult> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        DeleteAsync([path], cancellationToken);

    public async Task<RecycleBinResult> DeleteAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var results = new List<RecycleBinItemResult>();
        foreach (var path in paths)
        {
            var fullPath = path ?? string.Empty;
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                fullPath = Path.GetFullPath(path);
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Add(new RecycleBinItemResult(fullPath, RecycleBinItemStatus.Cancelled));
                    continue;
                }

                var status = await _operation.DeleteAsync(fullPath, cancellationToken);
                results.Add(new RecycleBinItemResult(fullPath, status));
            }
            catch (OperationCanceledException exception)
            {
                results.Add(new RecycleBinItemResult(fullPath, RecycleBinItemStatus.Cancelled, exception.Message));
            }
            catch (Exception exception)
            {
                results.Add(new RecycleBinItemResult(fullPath, RecycleBinItemStatus.Failed, exception.Message));
            }
        }

        return new RecycleBinResult(results);
    }

    public void PermanentDelete(string path, string allowedRoot) => PermanentDelete([path], allowedRoot);

    public void PermanentDelete(IEnumerable<string> paths, string allowedRoot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);
        if (!Path.IsPathFullyQualified(allowedRoot))
        {
            throw new ArgumentException("The allowed root must be fully qualified.", nameof(allowedRoot));
        }

        var fullRoot = Path.GetFullPath(allowedRoot);
        if (Path.GetDirectoryName(fullRoot) is null)
        {
            throw new ArgumentException("The allowed root cannot be a volume root.", nameof(allowedRoot));
        }

        try
        {
            if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("The allowed root cannot be a reparse point.", nameof(allowedRoot));
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }

        foreach (var path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var fullPath = ValidatePermanentDeletePath(path, fullRoot);
            InstanceStorage.DeleteEntryWithoutFollowingReparsePoints(fullPath);
        }
    }

    private static string ValidatePermanentDeletePath(string path, string allowedRoot)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A fully qualified path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (Path.GetDirectoryName(fullPath) is null)
        {
            throw new ArgumentException("A volume root cannot be deleted.", nameof(path));
        }

        var relative = Path.GetRelativePath(allowedRoot, fullPath);
        if (relative == "." || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new ArgumentException("The path must be inside the allowed root.", nameof(path));
        }

        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = allowedRoot;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            current = Path.Combine(current, parts[index]);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException("A path cannot pass through a reparse point.", nameof(path));
                }
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }

        return fullPath;
    }
}

internal interface IRecycleBinOperation
{
    Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
internal sealed partial class ShellRecycleBinOperation : IRecycleBinOperation
{
    public Task<RecycleBinItemStatus> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<RecycleBinItemStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(DeleteOnSta(path));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Hako Recycle Bin"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static RecycleBinItemStatus DeleteOnSta(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows Recycle Bin is only available on Windows.");
        }

        var initialized = CoInitializeEx(IntPtr.Zero, 0x2);
        if (initialized < 0)
        {
            Marshal.ThrowExceptionForHR(initialized);
        }

        IFileOperation? operation = null;
        IShellItem? item = null;
        uint cookie = 0;
        var advised = false;
        try
        {
            var classId = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09");
            var interfaceId = new Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
            var createResult = CoCreateInstance(classId, IntPtr.Zero, 0x17, interfaceId, out var createdOperation);
            Marshal.ThrowExceptionForHR(createResult);
            operation = createdOperation;

            var sink = new RecycleBinProgressSink();
            operation.SetOperationFlags(
                0x00000004u // FOF_SILENT
                | 0x00000010u // FOF_NOCONFIRMATION
                | 0x00000040u // FOF_ALLOWUNDO
                | 0x00000400u // FOF_NOERRORUI
                | 0x00080000u // FOFX_RECYCLEONDELETE
                | 0x00100000u); // FOFX_EARLYFAILURE
            operation.Advise(sink, out cookie);
            advised = true;

            try
            {
                var itemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                // SHCreateItemFromParsingName expects a Shell parsing name, not the Win32 \\?\ namespace.
                var shellItemResult = SHCreateItemFromParsingName(path, IntPtr.Zero, itemId, out var createdItem);
                Marshal.ThrowExceptionForHR(shellItemResult);
                item = createdItem;
                operation.DeleteItem(item, IntPtr.Zero);
                var operationResult = operation.PerformOperations();
                operation.GetAnyOperationsAborted(out var aborted);

                if (sink.NotRecyclable)
                {
                    return RecycleBinItemStatus.NotRecyclable;
                }

                if (operationResult < 0 || aborted || !sink.DeleteSucceeded || !sink.RecycleWasRequested)
                {
                    throw new RecycleBinException("The item may have been deleted. Check whether it still exists.");
                }

                return RecycleBinItemStatus.Recycled;
            }
            finally
            {
                try
                {
                    if (advised)
                    {
                        operation.Unadvise(cookie);
                    }
                }
                finally
                {
                    var shellItem = item;
                    item = null;
                    ReleaseComObject(shellItem);
                }
            }
        }
        finally
        {
            try
            {
                var shellItem = item;
                item = null;
                ReleaseComObject(shellItem);
            }
            finally
            {
                try
                {
                    var shellOperation = operation;
                    operation = null;
                    ReleaseComObject(shellOperation);
                }
                finally
                {
                    CoUninitialize();
                }
            }
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is ComObject com)
        {
            com.FinalRelease();
        }
    }

    [LibraryImport("ole32.dll", EntryPoint = "CoInitializeEx")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll", EntryPoint = "CoUninitialize")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IFileOperation operation);

    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid interfaceId, out IShellItem item);
}

[GeneratedComInterface]
[Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IFileOperation
{
    void Advise(IFileOperationProgressSink sink, out uint cookie);
    void Unadvise(uint cookie);
    void SetOperationFlags(uint flags);
    void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
    void SetProgressDialog(IntPtr dialog);
    void SetProperties(IntPtr propertyChangeArray);
    void SetOwnerWindow(IntPtr ownerWindow);
    void ApplyPropertiesToItem(IShellItem item);
    void ApplyPropertiesToItems(IntPtr items);
    void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
    void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
    void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
    void MoveItems(IntPtr items, IShellItem destination);
    void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
    void CopyItems(IntPtr items, IShellItem destination);
    void DeleteItem(IShellItem item, IntPtr sink);
    void DeleteItems(IntPtr items);
    void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, IFileOperationProgressSink? sink);
    [PreserveSig]
    int PerformOperations();
    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
}

[GeneratedComInterface]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IShellItem { }

[GeneratedComInterface]
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal partial interface IFileOperationProgressSink
{
    void StartOperations();
    void FinishOperations(int result);
    void PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
    void PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
    void PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    void PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
    void PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    void PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? newItem);
    [PreserveSig]
    int PreDeleteItem(uint flags, IShellItem item);
    void PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem);
    void PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    void PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string templateName, uint attributes, int result, IShellItem? newItem);
    void UpdateProgress(uint workTotal, uint workSoFar);
    void ResetTimer();
    void PauseTimer();
    void ResumeTimer();
}

[GeneratedComClass]
internal partial class RecycleBinProgressSink : IFileOperationProgressSink
{
    internal const uint TransferDeleteRecycleIfPossible = 0x80;
    internal const int HResultAbort = unchecked((int)0x80004004);

    internal bool DeleteSucceeded { get; private set; }
    internal bool RecycleWasRequested { get; private set; }
    internal bool NotRecyclable { get; private set; }

    public void StartOperations() { }
    public void FinishOperations(int result) { }
    public void PreRenameItem(uint flags, IShellItem item, string name) { }
    public void PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? newItem) { }
    public void PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name) { }
    public void PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? newItem) { }
    public void PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) { }
    public void PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? newItem) { }

    public int PreDeleteItem(uint flags, IShellItem item)
    {
        if ((flags & TransferDeleteRecycleIfPossible) == 0)
        {
            NotRecyclable = true;
            return HResultAbort;
        }

        RecycleWasRequested = true;
        return 0;
    }

    public void PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem) => DeleteSucceeded = result >= 0 && newItem is not null;
    public void PreNewItem(uint flags, IShellItem destination, string name) { }
    public void PostNewItem(uint flags, IShellItem destination, string name, string templateName, uint attributes, int result, IShellItem? newItem) { }
    public void UpdateProgress(uint workTotal, uint workSoFar) { }
    public void ResetTimer() { }
    public void PauseTimer() { }
    public void ResumeTimer() { }
}
