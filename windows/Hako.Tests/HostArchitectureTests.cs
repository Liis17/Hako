using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hako.Core.Minecraft;

namespace Hako.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class HostArchitectureTests
{
    private const uint MachineTypeAttributeUserEnabled = 0x00000001;
    private const ushort ImageFileMachineAmd64 = 0x8664;

    [TestMethod]
    public void HResultSuccessAndUserEnabledAllowAmd64()
    {
        var enabled = WindowsHostArchitectureProvider.IsAmd64UserEnabled((ushort machine, out uint attributes) =>
        {
            Assert.AreEqual(ImageFileMachineAmd64, machine);
            attributes = MachineTypeAttributeUserEnabled;
            return 0;
        });

        Assert.IsTrue(enabled);
    }

    [TestMethod]
    public void FailedHResultDoesNotAllowAmd64()
    {
        var enabled = WindowsHostArchitectureProvider.IsAmd64UserEnabled((ushort _, out uint attributes) =>
        {
            attributes = MachineTypeAttributeUserEnabled;
            return -1;
        });

        Assert.IsFalse(enabled);
    }

    [TestMethod]
    public void MissingMachineTypeAttributesEntryPointDoesNotAllowAmd64()
    {
        var enabled = WindowsHostArchitectureProvider.IsAmd64UserEnabled((ushort _, out uint _) => throw new EntryPointNotFoundException());

        Assert.IsFalse(enabled);
    }

    [TestMethod]
    public void NativeAmd64MachineTypeAttributesSignatureReturnsUserEnabledOnX64()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Assert.Inconclusive("This integration check requires an x64 Windows host.");
        }

        try
        {
            var hResult = WindowsHostArchitectureProvider.QueryMachineTypeAttributes(ImageFileMachineAmd64, out var attributes);

            Assert.IsTrue(hResult >= 0, $"GetMachineTypeAttributes failed with HRESULT 0x{hResult:X8}.");
            Assert.AreNotEqual(0u, attributes & MachineTypeAttributeUserEnabled);
        }
        catch (EntryPointNotFoundException)
        {
            Assert.Inconclusive("GetMachineTypeAttributes is not available on this Windows version.");
        }
    }
}
