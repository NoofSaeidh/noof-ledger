using System.Net;
using System.Net.Sockets;

namespace Noof.Ledger.Demo;

internal static class PortProbe
{
    public static bool IsListening(int port)
    {
        using var client = new TcpClient();
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
