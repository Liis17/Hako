using System.Runtime.CompilerServices;

namespace Hako;

internal static class WindowsAppSdkBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length > 1 && arguments[1] == "--playtime-helper") return;

#if HAKO_WINDOWSAPPSDK_SELF_CONTAINED
        return;
#else
        var options = new Microsoft.Windows.ApplicationModel.WindowsAppRuntime.DeploymentInitializeOptions
        {
            OnErrorShowUI = true
        };
        var result = Microsoft.Windows.ApplicationModel.WindowsAppRuntime.DeploymentManager.Initialize(options);
        if (result.Status != Microsoft.Windows.ApplicationModel.WindowsAppRuntime.DeploymentStatus.Ok)
        {
            var error = result.ExtendedError.HResult;
            Environment.Exit(error);
            Environment.FailFast($"Windows App SDK deployment initialization failed: 0x{error:X8}.");
        }
#endif
    }
}
