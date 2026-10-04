using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Khors.Engines.Tests;

public class PortAllocatorTests
{
    [Fact]
    public void FreePreferredPortIsKept()
    {
        var free = PortAllocator.Allocate((int?)null)[0];

        Assert.Equal(free, PortAllocator.Allocate(free)[0]);
    }

    [Fact]
    public void BusyPreferredPortIsReplacedAndPortsAreDistinct()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var busyPort = ((IPEndPoint)busy.LocalEndpoint).Port;

            var ports = PortAllocator.Allocate(busyPort, null, null);

            Assert.NotEqual(busyPort, ports[0]);
            Assert.Equal(3, ports.Distinct().Count());
        }
        finally
        {
            busy.Stop();
        }
    }
}
