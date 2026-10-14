using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;
using WinRT;

namespace Hako;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var mainInstance = AppInstance.FindOrRegisterForKey("Hako.Main");
        if (!mainInstance.IsCurrent)
        {
            RedirectActivationTo(mainInstance, activationArgs);
            return;
        }

        Application.Start(p =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            mainInstance.Activated += (_, _) => dispatcher.TryEnqueue(App.ActivateMainWindow);
            _ = new App();
        });
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
    }

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint handleCount, IntPtr[] handles, out uint index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
