using Khors.Platform.Windows.Tun;
using Xunit;

namespace Khors.Platform.Windows.Tests;

/// <summary>Какие устройства удаляются при сбросе: только wintun с именем KHORS (удаление — только вживую, с правами).</summary>
public class WintunAdapterCleanupTests
{
    [Theory]
    [InlineData(new[] { "Wintun" }, "KHORS", true)]
    [InlineData(new[] { "wintun" }, "khors", true)]
    [InlineData(new[] { "Wintun" }, "throne-tun", false)]
    [InlineData(new[] { "Wintun" }, null, false)]
    [InlineData(new[] { "PCI\\VEN_8086&DEV_15F3" }, "KHORS", false)]
    [InlineData(new string[0], "KHORS", false)]
    public void OnlyKhorsWintunAdapterMatches(string[] hardwareIds, string? connectionName, bool expected) =>
        Assert.Equal(expected, WintunAdapterCleanup.IsTunnelAdapter(hardwareIds, connectionName, "KHORS"));
}
