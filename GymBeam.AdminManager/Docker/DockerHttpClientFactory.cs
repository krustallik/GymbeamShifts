using System.Net.Sockets;

namespace GymBeam.AdminManager.Docker;

internal static class DockerHttpClientFactory
{
    public static HttpClient Create(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://docker")
        };
    }
}
