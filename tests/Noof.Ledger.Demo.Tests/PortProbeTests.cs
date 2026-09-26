using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;

namespace Noof.Ledger.Demo.Tests;

public sealed class PortProbeTests
{
    [Fact]
    public void A_listening_port_is_seen_and_a_closed_one_is_not()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        PortProbe.IsListening(port).Should().BeTrue();

        listener.Stop();
        PortProbe.IsListening(port).Should().BeFalse();
    }
}
