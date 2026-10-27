using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        // DeploymentManager itself needs the framework in the process package graph. The SDK's
        // bootstrapper uses the build-time runtime version and does nothing for packaged launches.
        Microsoft.Windows.ApplicationModel.DynamicDependency.BootstrapCS.AutoInitialize.AccessWindowsAppSDK();
        uint packageNameLength = 0;
        if (GetCurrentPackageFullName(ref packageNameLength, IntPtr.Zero) == 15700) return;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
