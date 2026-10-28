using System.Net;
using System.Net.Sockets;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Pins that <see cref="ClosedLoopbackPort"/> yields a port that refuses connections promptly.</summary>
public sealed class ClosedLoopbackPortTests
{
    [Fact]
    public async Task Connect_IsRefusedPromptly_NotLeftToTimeOut()
    {
        int port = ClosedLoopbackPort.Next();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        // No cancellation token: cancelling or disposing a pending connect is the very race this fixture avoids.
        Task connect = socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        Task finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(5)));

        if (finished != connect)
        {
            // Deliberately not disposed: disposing with a connect in flight is the crash being avoided, and leaking
            // one socket in a failing test is the safe choice.
            Assert.Fail($"connect to 127.0.0.1:{port} was not refused within 5 s; the port is filtered, so it is not a usable unreachable-server fixture");
        }

        try
        {
            SocketException ex = await Assert.ThrowsAsync<SocketException>(() => connect);
            Assert.Equal(SocketError.ConnectionRefused, ex.SocketErrorCode);
        }
        finally
        {
            socket.Dispose();
        }
    }
}
