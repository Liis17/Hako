using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Hako.Core;
using Hako.Core.Playtime;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinRT;
using Windows.ApplicationModel.Activation;

namespace Hako;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (TryRunPlaytimeHelper(args))
        {
            return;
        }

        ComWrappersSupport.InitializeComWrappers();

        HakoPathOverrides overrides;
        try
        {
            overrides = HakoPaths.ParseOverrides(args);
            App.LaunchOptions = ProfilePreviewOptions.Parse(args);
            if (overrides.Isolated && App.LaunchOptions is not null)
            {
                throw new ArgumentException("--profile-preview cannot be combined with isolated storage arguments.");
            }

            App.LaunchPaths = App.LaunchOptions is not null
                ? CreatePreviewPaths()
                : overrides.Isolated
                    ? HakoPaths.CreateIsolated(overrides.GameRoot!, overrides.DataRoot!)
                    : null;
        }
        catch (ArgumentException)
        {
            ShowStartupError("StartupInvalidArguments");
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowStartupError("StartupStorageUnavailable");
            return;
        }

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        App.InitialRestorePaths = ExtractRestorePaths(args)
            .Concat(ExtractRestorePaths(activationArgs))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var instanceKey = App.LaunchOptions is not null
            ? "Hako.ProfilePreview"
            : App.LaunchPaths?.GetInstanceKey() ?? "Hako.Main";
        var mainInstance = AppInstance.FindOrRegisterForKey(instanceKey);
        if (!mainInstance.IsCurrent)
        {
            RedirectActivationTo(mainInstance, activationArgs);
            App.CleanupPreviewRoot();
            return;
        }

        Application.Start(p =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            mainInstance.Activated += (_, activation) =>
            {
                var paths = ExtractRestorePaths(activation);
                dispatcher.TryEnqueue(() =>
                {
                    App.ActivateMainWindow();
                    App.QueueRestoreFiles(paths);
                });
            };
            _ = new App();
        });
    }

    private static IEnumerable<string> ExtractRestorePaths(string[] arguments) => arguments
        .Where(IsRestoreArchivePath);

    private static IEnumerable<string> ExtractRestorePaths(AppActivationArguments activation)
    {
        if (activation.Kind != ExtendedActivationKind.File
            || activation.Data is not IFileActivatedEventArgs fileActivation)
        {
            return [];
        }

        return fileActivation.Files
            .Select(file => file.Path)
            .Where(IsRestoreArchivePath)
            .ToArray();
    }

    private static bool IsRestoreArchivePath(string path) =>
        Path.GetExtension(path).Equals(".hakobackup", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".hakoworld", StringComparison.OrdinalIgnoreCase);

    private static bool TryRunPlaytimeHelper(string[] args)
    {
        if (args.Length == 0 || args[0] != "--playtime-helper")
        {
            return false;
        }

        Environment.ExitCode = RunPlaytimeHelper(args[1..]);
        return true;
    }

    private static int RunPlaytimeHelper(string[] arguments)
    {
        return PlaytimeHelperHost.Run(arguments);
    }

    private static HakoPaths CreatePreviewPaths()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Hako", "ProfilePreview");
        Directory.CreateDirectory(parent);
        while (true)
        {
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            if (CreateDirectoryExclusive(root))
            {
                App.PreviewRoot = root;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => App.CleanupPreviewRoot();
                return HakoPaths.CreatePreview(root);
            }
        }
    }

    private static bool CreateDirectoryExclusive(string path)
    {
        if (CreateDirectoryW(path, IntPtr.Zero))
        {
            return true;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == 183)
        {
            return false;
        }

        throw new System.ComponentModel.Win32Exception(error);
    }

    private static void ShowStartupError(string resourceKey)
    {
        HakoStrings.SetLanguage(AppLanguages.Default, true);
        MessageBox(IntPtr.Zero, HakoStrings.Get(resourceKey), "Hako", 0x10);
    }

    // Redirect runs on a worker thread. The STA thread waits in CoWaitForMultipleObjects so it keeps pumping
    // messages; a blocking Wait() here can deadlock while the target instance handles the activation.
    private static void RedirectActivationTo(AppInstance instance, AppActivationArguments args)
    {
        var redirectDone = CreateEvent(IntPtr.Zero, true, false, null);
        _ = Task.Run(async () =>
        {
            try
            {
                await instance.RedirectActivationToAsync(args);
            }
            finally
            {
                SetEvent(redirectDone);
            }
        });

        CoWaitForMultipleObjects(0, uint.MaxValue, 1, [redirectDone], out _);
        CloseHandle(redirectDone);

        // The redirecting process is the one allowed to bring the target window forward, so it does it here.
        try
        {
            using var target = Process.GetProcessById((int)instance.ProcessId);
            SetForegroundWindow(target.MainWindowHandle);
        }
        catch (Exception)
        {
            // The main instance may have exited after the redirect; this instance just exits quietly.
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint handleCount, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
