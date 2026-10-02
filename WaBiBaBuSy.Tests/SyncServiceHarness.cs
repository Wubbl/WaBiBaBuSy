using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using WaBiBaBuSy.Grpc;
using WaBiBaBuSy.Grpc.Services;
using WaBiBaBuSy.Models.Configuration;

namespace WaBiBaBuSy.Tests;

/// <summary>
/// An in-process <see cref="WallpaperSyncService"/> with fake clients: <see cref="Connect"/> opens a
/// SyncStream whose commands go to a callback, and the RPCs a client calls are invoked directly.
/// No network, no topology writes (only the persisted topology is read, as on every server start).
/// </summary>
internal sealed class SyncServiceHarness : IDisposable
{
    private readonly string _contentDir = Path.Combine(Path.GetTempPath(), $"wbbs-sync-{Guid.NewGuid():N}");
    private readonly List<CancellationTokenSource> _streams = new();

    public WallpaperSyncService Service { get; }

    public SyncServiceHarness()
    {
        Service = new WallpaperSyncService(NullLogger<WallpaperSyncService>.Instance,
            new ServerConfiguration { ContentDirectory = _contentDir });
    }

    /// <summary>Register a command stream for <paramref name="clientId"/>; every command the server sends goes to <paramref name="onCommand"/>.</summary>
    public void Connect(string clientId, Action<SyncCommand> onCommand)
    {
        var cts = new CancellationTokenSource();
        _streams.Add(cts);
        // SyncStream registers the stream before its first await, so the client is reachable on return.
        _ = Service.SyncStream(new BlockingReader<SyncResponse>(),
            new CallbackWriter<SyncCommand>(onCommand),
            new FakeServerCallContext(new Metadata { { "client-id", clientId } }, cts.Token));
    }

    /// <summary>A client's FETCH_LOGS reply (SendClientLogs RPC).</summary>
    public Task<LogReceiveResponse> ReplyLogsAsync(string clientId, string content) =>
        Service.SendClientLogs(new ClientLogData { ClientId = clientId, LogContent = content },
            new FakeServerCallContext(new Metadata(), CancellationToken.None));

    /// <summary>Upload a probe result the way WallpaperSyncClient does: header first, then capture chunks.</summary>
    public Task<ProbeResultAck> SubmitProbeAsync(string clientId, string probeId, string resultJson = "{}",
        IReadOnlyDictionary<int, byte[]>? captures = null, int chunkSize = 256 * 1024)
    {
        var chunks = new List<ProbeResultChunk>
        {
            new() { Header = new ProbeResultHeader { ClientId = clientId, ProbeId = probeId, ResultJson = resultJson } },
        };
        foreach (var (monitor, png) in captures ?? new Dictionary<int, byte[]>())
            for (int offset = 0; offset < png.Length; offset += chunkSize)
                chunks.Add(new ProbeResultChunk
                {
                    Capture = new ProbeCaptureChunk { MonitorIndex = monitor, Data = ByteString.CopyFrom(png, offset, Math.Min(chunkSize, png.Length - offset)) },
                });
        return Service.SubmitProbeResult(new ListReader<ProbeResultChunk>(chunks), new FakeServerCallContext(new Metadata(), CancellationToken.None));
    }

    public void Dispose()
    {
        foreach (var cts in _streams) cts.Cancel();
        try { Directory.Delete(_contentDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class BlockingReader<T> : IAsyncStreamReader<T>
    {
        public T Current => default!;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return false;
        }
    }

    private sealed class ListReader<T> : IAsyncStreamReader<T>
    {
        private readonly IReadOnlyList<T> _items;
        private int _index = -1;
        public ListReader(IReadOnlyList<T> items) => _items = items;
        public T Current => _items[_index];
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(++_index < _items.Count);
    }

    private sealed class CallbackWriter<T> : IServerStreamWriter<T>
    {
        private readonly Action<T> _onWrite;
        public CallbackWriter(Action<T> onWrite) => _onWrite = onWrite;
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message)
        {
            _onWrite(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServerCallContext : ServerCallContext
    {
        private readonly Metadata _headers;
        private readonly CancellationToken _ct;

        public FakeServerCallContext(Metadata headers, CancellationToken ct)
        {
            _headers = headers;
            _ct = ct;
        }

        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:12345";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => _headers;
        protected override CancellationToken CancellationTokenCore => _ct;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
