using System.Net;
using System.Net.Sockets;
using T.Services;

namespace T.Tests;

public class TunnelRelayTests
{
    [Fact]
    public async Task RelaysBothDirections_AndCountsTheBytes()
    {
        var token = TestContext.Current.CancellationToken;
        using var echo = new EchoServer();
        int localPort = TunnelRelay.FindFreeLoopbackPort();
        using var relay = new TunnelRelay(localPort, echo.Port);
        relay.Start();

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, localPort, token);
            var stream = client.GetStream();
            var data = new byte[5000];
            Random.Shared.NextBytes(data);
            await stream.WriteAsync(data, token);
            client.Client.Shutdown(SocketShutdown.Send); // half-close: the echo still answers

            var echoed = new MemoryStream();
            await stream.CopyToAsync(echoed, token);
            Assert.Equal(data, echoed.ToArray());
        }

        await WaitUntilAsync(() => relay.Statistics.OpenConnections == 0, token);
        var stats = relay.Statistics;
        Assert.Equal((5000L, 5000L, 0, 1L), (stats.BytesSent, stats.BytesReceived, stats.OpenConnections, stats.TotalConnections));
    }

    [Fact]
    public void PortInUse_FailsOnStart()
    {
        int localPort = TunnelRelay.FindFreeLoopbackPort();
        using var first = new TunnelRelay(localPort, 1);
        first.Start();

        using var second = new TunnelRelay(localPort, 1);
        var error = Assert.ThrowsAny<SocketException>(second.Start);
        Assert.Equal(SocketError.AddressAlreadyInUse, error.SocketErrorCode);
    }

    [Fact]
    public async Task Dispose_ClosesOpenConnections_AndReleasesThePort()
    {
        var token = TestContext.Current.CancellationToken;
        using var echo = new EchoServer();
        int localPort = TunnelRelay.FindFreeLoopbackPort();
        var relay = new TunnelRelay(localPort, echo.Port);
        relay.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, localPort, token);
        await WaitUntilAsync(() => relay.Statistics.OpenConnections == 1, token);

        relay.Dispose();

        // The forwarded connection ends (EOF or reset) instead of staying open.
        var buffer = new byte[16];
        try
        {
            var read = await client.GetStream().ReadAsync(buffer, token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(0, read);
        }
        catch (IOException)
        {
            // reset - also closed
        }

        using var again = new TunnelRelay(localPort, echo.Port);
        again.Start();
    }

    [Fact]
    public async Task RemoteRule_RelaysFromAnInternalPortToANamedTarget_AndCountsTheBytes()
    {
        // The relay of a remote rule: SSH.NET connects to a port the relay chose, the target is a host name.
        var token = TestContext.Current.CancellationToken;
        using var echo = new EchoServer();
        using var relay = new TunnelRelay(0, "localhost", echo.Port);
        relay.Start();
        Assert.InRange(relay.Port, 1, 65535);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, relay.Port, token);
            var stream = client.GetStream();
            var data = new byte[3000];
            Random.Shared.NextBytes(data);
            await stream.WriteAsync(data, token);
            client.Client.Shutdown(SocketShutdown.Send);

            var echoed = new MemoryStream();
            await stream.CopyToAsync(echoed, token);
            Assert.Equal(data, echoed.ToArray());
        }

        await WaitUntilAsync(() => relay.Statistics.OpenConnections == 0, token);
        var stats = relay.Statistics;
        Assert.Equal((3000L, 3000L, 0, 1L), (stats.BytesSent, stats.BytesReceived, stats.OpenConnections, stats.TotalConnections));
    }

    [Fact]
    public async Task UnreachableTarget_IsReported_AndTheConnectionIsClosed()
    {
        var token = TestContext.Current.CancellationToken;
        int closedPort = TunnelRelay.FindFreeLoopbackPort();
        using var relay = new TunnelRelay(0, IPAddress.Loopback.ToString(), closedPort);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.TargetConnectFailed += ex => failure.TrySetResult(ex);
        relay.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.Port, token);

        Assert.IsType<SocketException>(await failure.Task.WaitAsync(TimeSpan.FromSeconds(10), token));
        var buffer = new byte[16];
        try
        {
            var read = await client.GetStream().ReadAsync(buffer, token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Equal(0, read);
        }
        catch (IOException)
        {
            // reset - also closed
        }

        // A connection that never reached the target is not counted.
        Assert.Equal((0, 0L), (relay.Statistics.OpenConnections, relay.Statistics.TotalConnections));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        for (int i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20, token);
        Assert.True(condition());
    }

    /// <summary>Loopback server that echoes everything and half-closes after the client did.</summary>
    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public EchoServer()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                await client.GetStream().CopyToAsync(client.GetStream(), _stop.Token);
                                client.Client.Shutdown(SocketShutdown.Send);
                            }
                            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
