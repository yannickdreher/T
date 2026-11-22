using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using T.Models;

namespace T.Services;

/// <summary>
/// Local side of a tunnel: listens on 127.0.0.1:<c>localPort</c> and relays every connection to
/// <c>127.0.0.1:innerPort</c> (where SSH.NET's forwarded port listens), counting the bytes in both
/// directions. SSH.NET does not report traffic, and the relay also makes it possible to close open
/// forwarded connections when the tunnel stops.
/// </summary>
internal sealed class TunnelRelay : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly int _innerPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Inner)> _open = new();
    private long _received;
    private long _sent;
    private long _total;
    private int _nextId;
    private int _disposed;

    /// <param name="localPort">The port users connect to (loopback only).</param>
    /// <param name="innerPort">The port of the SSH forwarding listener (loopback only).</param>
    public TunnelRelay(int localPort, int innerPort)
    {
        _innerPort = innerPort;
        _listener = new TcpListener(IPAddress.Loopback, localPort);
        _listener.Server.ExclusiveAddressUse = true;
    }

    /// <summary>Binds the local port (throws a <see cref="SocketException"/> when it is in use) and starts accepting.</summary>
    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync(_stop.Token);
    }

    public TunnelStatistics Statistics => new(
        Interlocked.Read(ref _received), Interlocked.Read(ref _sent), _open.Count, Interlocked.Read(ref _total));

    /// <summary>A free loopback port for the inner (SSH) listener.</summary>
    public static int FindFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = RelayAsync(client, token);
        }
    }

    private async Task RelayAsync(TcpClient client, CancellationToken token)
    {
        int id = Interlocked.Increment(ref _nextId);
        var inner = new TcpClient();
        try
        {
            client.NoDelay = true;
            inner.NoDelay = true;
            await inner.ConnectAsync(IPAddress.Loopback, _innerPort, token);
            _open[id] = (client, inner);
            Interlocked.Increment(ref _total);

            var clientStream = client.GetStream();
            var innerStream = inner.GetStream();
            await Task.WhenAll(
                PumpAsync(clientStream, innerStream, inner.Client, bytes => Interlocked.Add(ref _sent, bytes), token),
                PumpAsync(innerStream, clientStream, client.Client, bytes => Interlocked.Add(ref _received, bytes), token));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
        {
            // The client, the target or Stop() closed the connection.
        }
        finally
        {
            _open.TryRemove(id, out _);
            client.Dispose();
            inner.Dispose();
        }
    }

    /// <summary>Copies one direction; at the end of the stream the other side gets a half-close (FIN).</summary>
    private static async Task PumpAsync(NetworkStream from, NetworkStream to, Socket toSocket, Action<int> count, CancellationToken token)
    {
        var buffer = new byte[BufferSize];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, token)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), token);
                count(read);
            }
            try { toSocket.Shutdown(SocketShutdown.Send); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // One side reset the connection: end the other direction as well.
            try { toSocket.Close(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Stops listening and closes all open relayed connections.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _listener.Stop(); }
        catch (SocketException ex) { Debug.WriteLine($"[TunnelRelay] Stop failed: {ex.Message}"); }

        foreach (var (client, inner) in _open.Values)
        {
            client.Dispose();
            inner.Dispose();
        }
        _open.Clear();
        _stop.Dispose();
    }
}
