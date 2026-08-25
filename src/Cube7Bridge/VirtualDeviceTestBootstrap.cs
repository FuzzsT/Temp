using System.Runtime.CompilerServices;

namespace Cube7Bridge;

internal static class VirtualDeviceTestBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            VirtualDeviceEmulationTests.Run();
            BleOfficialSessionTests.Run();
            BleOfficialSessionRuntimeTests.Run();
        }
    }
}
