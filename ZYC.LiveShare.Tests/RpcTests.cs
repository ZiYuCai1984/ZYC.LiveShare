using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ZYC.LiveShare.Rpc;

namespace ZYC.LiveShare.Tests;

public class RpcTests
{
    [Fact]
    public async Task FramingRoundTripsMessagesSplitAcrossReads()
    {
        var bodies = new[] { "{\"a\":1}", "{\"b\":\"" + new string('x', 100_000) + "\"}", "{}" };
        var data = bodies.SelectMany(b => MessageWriter.Frame(Encoding.UTF8.GetBytes(b))).ToArray();
        var reader = new MessageReader(new TrickleStream(data, 7));

        foreach (var expected in bodies)
        {
            var body = await reader.ReadAsync(CancellationToken.None);
            Assert.Equal(expected, Encoding.UTF8.GetString(body!));
        }

        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FramingRejectsMissingContentLength()
    {
        var reader = new MessageReader(new MemoryStream(Encoding.ASCII.GetBytes("Content-Type: x\r\n\r\n{}")));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RequestsResponsesAndNotificationsFlowBothWays()
    {
        var (clientStream, serverStream) = await CreateConnectedStreamsAsync();
        await using var client = new RpcConnection(clientStream, NullLogger.Instance);
        await using var server = new RpcConnection(serverStream, NullLogger.Instance);

        server.RequestHandler = (request, _) => request.Method switch
        {
            "math.add" => Task.FromResult<object?>(request.Params[0].GetInt32() + request.Params[1].GetInt32()),
            "math.fail" => throw new RpcRemoteException(-32099, "boom"),
            _ => throw new RpcRemoteException(RpcConnection.MethodNotFound, "nope")
        };
        var notified = new TaskCompletionSource<RpcNotification>();
        client.NotificationReceived += (_, n) => notified.TrySetResult(n);
        client.Start();
        server.Start();

        Assert.Equal(5, await client.InvokeAsync<int>("math.add", [2, 3], CancellationToken.None));

        var error = await Assert.ThrowsAsync<RpcRemoteException>(() =>
            client.InvokeAsync("math.fail", [], CancellationToken.None));
        Assert.Equal(-32099, error.Code);

        var missing =
            await Assert.ThrowsAsync<RpcRemoteException>(() =>
                client.InvokeAsync("other.call", [], CancellationToken.None));
        Assert.Equal(RpcConnection.MethodNotFound, missing.Code);

        await server.NotifyAsync("svc.changed", new { value = 42 }, CancellationToken.None);
        var notification = await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("svc.changed", notification.Method);
        Assert.Equal(42, notification.Params.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task MessagesCarryMessageIdAndAckId()
    {
        var (clientStream, serverStream) = await CreateConnectedStreamsAsync();
        await using var client = new RpcConnection(clientStream, NullLogger.Instance);
        var serverReader = new MessageReader(serverStream);
        client.Start();

        var call = client.InvokeAsync("svc.first", ["arg"], CancellationToken.None);
        using (var request = JsonDocument.Parse((await serverReader.ReadAsync(CancellationToken.None))!))
        {
            var root = request.RootElement;
            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
            Assert.Equal("svc.first", root.GetProperty("method").GetString());
            Assert.Equal("arg", root.GetProperty("params")[0].GetString());
            Assert.Equal(root.GetProperty("id").GetInt64(), root.GetProperty("messageId").GetInt64());
            Assert.False(root.TryGetProperty("ackId", out _));

            var response =
                $"{{\"jsonrpc\":\"2.0\",\"messageId\":7,\"id\":{root.GetProperty("id").GetInt64()},\"result\":\"ok\"}}";
            await serverStream.WriteAsync(MessageWriter.Frame(Encoding.UTF8.GetBytes(response)));
        }

        Assert.Equal("ok", (await call).GetString());

        _ = client.InvokeAsync("svc.second", [], CancellationToken.None);
        using var second = JsonDocument.Parse((await serverReader.ReadAsync(CancellationToken.None))!);
        Assert.Equal(7, second.RootElement.GetProperty("ackId").GetInt64());
    }

    [Fact]
    public async Task PendingRequestsFailWhenConnectionCloses()
    {
        var (clientStream, serverStream) = await CreateConnectedStreamsAsync();
        await using var client = new RpcConnection(clientStream, NullLogger.Instance);
        client.Start();

        var call = client.InvokeAsync("svc.never", [], CancellationToken.None);
        await serverStream.DisposeAsync();

        await Assert.ThrowsAsync<RpcDisconnectedException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    internal static async Task<(Stream Client, Stream Server)> CreateConnectedStreamsAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await accept;
            return (client.GetStream(), server.GetStream());
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Returns at most <c>chunkSize</c> bytes per read.</summary>
    private sealed class TrickleStream(byte[] data, int chunkSize) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            return base.Read(buffer, offset, Math.Min(count, chunkSize));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
        }
    }
}