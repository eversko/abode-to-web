using System.Runtime.Versioning;
using AbodeToWeb.Infrastructure.Runtime;

namespace AbodeToWeb.IntegrationTests;

[SupportedOSPlatform("windows")]
public sealed class RuntimeDesktopAccessTests
{
    private const int Delete = 0x00010000;
    private const int WriteDac = 0x00040000;
    private const int WriteOwner = 0x00080000;
    private const int WindowStationExitWindows = 0x00000040;
    private const int DesktopHookControl = 0x00000008;

    [Fact]
    public void Runtime_window_station_rights_are_limited_to_required_connect_read_and_create_capabilities()
    {
        const int expected = 0x0002 | 0x0008 | 0x0020;

        Assert.Equal(expected, RuntimeDesktopAccess.WindowStationRuntimeRights);
        Assert.Equal(0, RuntimeDesktopAccess.WindowStationRuntimeRights & (Delete | WriteDac | WriteOwner));
        Assert.Equal(0, RuntimeDesktopAccess.WindowStationRuntimeRights & WindowStationExitWindows);
    }

    [Fact]
    public void Runtime_desktop_rights_are_limited_to_required_read_and_create_capabilities()
    {
        const int expected = 0x0001 | 0x0002 | 0x0004 | 0x00040 | 0x00080;

        Assert.Equal(expected, RuntimeDesktopAccess.DesktopRuntimeRights);
        Assert.Equal(0, RuntimeDesktopAccess.DesktopRuntimeRights & (Delete | WriteDac | WriteOwner));
        Assert.Equal(0, RuntimeDesktopAccess.DesktopRuntimeRights & DesktopHookControl);
    }
}
