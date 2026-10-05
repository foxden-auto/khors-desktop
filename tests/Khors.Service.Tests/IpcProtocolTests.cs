using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Khors.Ipc;
using Khors.Service.Ipc;
using Xunit;

namespace Khors.Service.Tests;

public class IpcProtocolTests
{
    private static readonly ServiceInfo s_info = new("1.2.3", new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FrameRoundTrip()
    {
        using var stream = new MemoryStream();
        await IpcFraming.WriteFrameAsync(stream, "{\"a\":1}"u8.ToArray(), Ct);
        await IpcFraming.WriteFrameAsync(stream, "{}"u8.ToArray(), Ct);
        stream.Position = 0;

        Assert.Equal("{\"a\":1}"u8.ToArray(), await IpcFraming.ReadFrameAsync(stream, Ct));
        Assert.Equal("{}"u8.ToArray(), await IpcFraming.ReadFrameAsync(stream, Ct));
        Assert.Null(await IpcFraming.ReadFrameAsync(stream, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IpcFraming.MaxFrameSize + 1)]
    [InlineData(int.MaxValue)]
    public async Task FrameSizeIsCheckedBeforeReadingBody(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadFrameAsync(stream, Ct));
    }

    [Fact]
    public async Task TruncatedFrameIsProtocolError()
    {
        var data = new byte[4 + 3];
        BinaryPrimitives.WriteInt32LittleEndian(data, 10);
        using var stream = new MemoryStream(data);

        await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadFrameAsync(stream, Ct));
    }

    [Fact]
    public void EnvelopeRoundTrip()
    {
        var envelope = new IpcEnvelope(7, new HelloRequest(1, "0.1.0"));

        Assert.True(IpcSerializer.TryDeserialize(IpcSerializer.Serialize(envelope), out var parsed, out _, out _));
        Assert.Equal(envelope, parsed);
        Assert.Contains("\"type\":\"hello\"", Encoding.UTF8.GetString(IpcSerializer.Serialize(envelope)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"id":5,"payload":{"type":"runProcess","path":"C:\\Windows\\System32\\cmd.exe"}}""", 5, IpcErrorCode.UnknownCommand)]
    [InlineData("""{"id":6,"payload":{"protocolVersion":1}}""", 6, IpcErrorCode.UnknownCommand)]
    [InlineData("""{"id":7,"payload":{"type":"hello"}}""", 7, IpcErrorCode.BadRequest)]
    [InlineData("""{"id":8,"payload":"hello"}""", 8, IpcErrorCode.BadRequest)]
    [InlineData("""not json""", 0, IpcErrorCode.BadRequest)]
    [InlineData("""[1,2]""", 0, IpcErrorCode.BadRequest)]
    public void BadFramesAreRejectedWithRequestId(string json, long expectedId, IpcErrorCode expected)
    {
        Assert.False(IpcSerializer.TryDeserialize(Encoding.UTF8.GetBytes(json), out var envelope, out var id, out var error));
        Assert.Null(envelope);
        Assert.Equal(expectedId, id);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void CommandsRequireHelloWithSameProtocolVersion()
    {
        var handler = new ServiceRequestHandler(s_info);

        Assert.Equal(new ErrorResponse(IpcErrorCode.HelloRequired), handler.Handle(new GetStatusRequest()));
        Assert.Equal(new ErrorResponse(IpcErrorCode.ProtocolMismatch, IpcProtocol.Version), handler.Handle(new HelloRequest(IpcProtocol.Version + 1, "9.9")));
        Assert.Equal(new ErrorResponse(IpcErrorCode.HelloRequired), handler.Handle(new GetStatusRequest()));

        Assert.Equal(new HelloResponse(IpcProtocol.Version, "1.2.3"), handler.Handle(new HelloRequest(IpcProtocol.Version, "0.1.0")));
        Assert.Equal(new ServiceStatusResponse("1.2.3", s_info.StartedAt), handler.Handle(new GetStatusRequest()));
    }

    [Fact]
    public void ServiceDoesNotAcceptItsOwnMessagesAsCommands()
    {
        var handler = new ServiceRequestHandler(s_info);
        handler.Handle(new HelloRequest(IpcProtocol.Version, "0.1.0"));

        Assert.Equal(new ErrorResponse(IpcErrorCode.UnknownCommand), handler.Handle(new ServiceStatusResponse("x", DateTimeOffset.UnixEpoch)));
        Assert.Equal(new ErrorResponse(IpcErrorCode.UnknownCommand), handler.Handle(new ErrorResponse(IpcErrorCode.Internal)));
    }

    [Fact]
    public async Task ClientTalksToServiceSession()
    {
        await using var pair = await SocketPair.CreateAsync(Ct);
        var session = IpcServer.ServeConnectionAsync(pair.Server, new ServiceRequestHandler(s_info), Ct);

        await using (var client = await IpcClient.ConnectAsync(new FixedTransport(pair.Client), "0.1.0", TimeSpan.FromSeconds(5), Ct))
        {
            Assert.Equal("1.2.3", client.Service.ServiceVersion);
            var status = await client.RequestAsync(new GetStatusRequest(), Ct);
            Assert.Equal(new ServiceStatusResponse("1.2.3", s_info.StartedAt), status);
        }

        // Клиент закрыл соединение — сессия службы завершается сама.
        await session.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task SessionAnswersGarbageAndKeepsWorking()
    {
        await using var pair = await SocketPair.CreateAsync(Ct);
        var session = IpcServer.ServeConnectionAsync(pair.Server, new ServiceRequestHandler(s_info), Ct);

        await IpcFraming.WriteFrameAsync(pair.Client, """{"id":3,"payload":{"type":"exec"}}"""u8.ToArray(), Ct);
        Assert.True(IpcSerializer.TryDeserialize((await IpcFraming.ReadFrameAsync(pair.Client, Ct))!, out var answer, out _, out _));
        Assert.Equal(new IpcEnvelope(3, new ErrorResponse(IpcErrorCode.UnknownCommand)), answer);

        await IpcFraming.WriteFrameAsync(pair.Client, IpcSerializer.Serialize(new IpcEnvelope(4, new HelloRequest(IpcProtocol.Version, "0.1.0"))), Ct);
        Assert.True(IpcSerializer.TryDeserialize((await IpcFraming.ReadFrameAsync(pair.Client, Ct))!, out answer, out _, out _));
        Assert.IsType<HelloResponse>(answer!.Payload);

        pair.Client.Dispose();
        await session.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task OversizedFrameEndsSession()
    {
        await using var pair = await SocketPair.CreateAsync(Ct);
        var session = IpcServer.ServeConnectionAsync(pair.Server, new ServiceRequestHandler(s_info), Ct);

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, IpcFraming.MaxFrameSize + 1);
        await pair.Client.WriteAsync(header, Ct);

        await Assert.ThrowsAsync<IpcProtocolException>(() => session.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task VersionMismatchIsReported()
    {
        await using var pair = await SocketPair.CreateAsync(Ct);
        var session = Task.Run(async () =>
        {
            var frame = await IpcFraming.ReadFrameAsync(pair.Server, Ct);
            IpcSerializer.TryDeserialize(frame!, out var request, out _, out _);
            var answer = new IpcEnvelope(request!.Id, new ErrorResponse(IpcErrorCode.ProtocolMismatch, 99));
            await IpcFraming.WriteFrameAsync(pair.Server, IpcSerializer.Serialize(answer), Ct);
        }, Ct);

        var error = await Assert.ThrowsAsync<IpcVersionMismatchException>(() =>
            IpcClient.ConnectAsync(new FixedTransport(pair.Client), "0.1.0", TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(99, error.ServiceProtocolVersion);
        await session;
    }

    [Fact]
    public async Task PendingRequestFailsWhenServiceDisconnects()
    {
        await using var pair = await SocketPair.CreateAsync(Ct);
        var session = IpcServer.ServeConnectionAsync(pair.Server, new ServiceRequestHandler(s_info), Ct);
        await using var client = await IpcClient.ConnectAsync(new FixedTransport(pair.Client), "0.1.0", TimeSpan.FromSeconds(5), Ct);
        var disconnected = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        pair.Server.Dispose();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await Assert.ThrowsAsync<IpcDisconnectedException>(() => client.RequestAsync(new GetStatusRequest(), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => session);
    }

    private sealed class FixedTransport(Stream stream) : IIpcClientTransport
    {
        public Task<Stream> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(stream);
    }

    /// <summary>Два конца TCP-соединения на loopback — двунаправленный поток без привязки к ОС.</summary>
    private sealed class SocketPair : IAsyncDisposable
    {
        private SocketPair(NetworkStream client, NetworkStream server)
        {
            Client = client;
            Server = server;
        }

        public NetworkStream Client { get; }

        public NetworkStream Server { get; }

        public static async Task<SocketPair> CreateAsync(CancellationToken cancellationToken)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync(cancellationToken);
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
            var server = await accept;
            return new SocketPair(new NetworkStream(client.Client, ownsSocket: true), new NetworkStream(server.Client, ownsSocket: true));
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
