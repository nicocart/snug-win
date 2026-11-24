using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Support;

namespace SnugWin.Windows.Display;

public sealed record VddMode(
    [property: JsonPropertyName("width"), JsonRequired] uint Width,
    [property: JsonPropertyName("height"), JsonRequired] uint Height,
    [property: JsonPropertyName("refresh_rates"), JsonRequired] IReadOnlyList<uint> RefreshRates);

public sealed record VddMonitor(
    [property: JsonPropertyName("id"), JsonRequired] uint Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("enabled"), JsonRequired] bool Enabled,
    [property: JsonPropertyName("modes"), JsonRequired] IReadOnlyList<VddMode> Modes);

internal sealed record VddLegacyMode(
    [property: JsonPropertyName("width")] uint Width,
    [property: JsonPropertyName("height")] uint Height,
    [property: JsonPropertyName("refresh_rate")] uint RefreshRate);

internal sealed record VddLegacyMonitor(
    [property: JsonPropertyName("id")] uint Id,
    [property: JsonPropertyName("modes")] IReadOnlyList<VddLegacyMode> Modes);

internal interface IVirtualDisplayIpc : IAsyncDisposable
{
    bool IsLegacyProtocol { get; }
    Task NotifyAsync(IReadOnlyList<VddMonitor> monitors, CancellationToken ct = default);
    Task ReplaceLegacyAsync(IReadOnlyList<uint> removeIds, IReadOnlyList<VddMonitor> addMonitors, CancellationToken ct = default);
    Task RemoveAsync(IReadOnlyList<uint> ids, CancellationToken ct = default);
    Task RemoveAllAsync(CancellationToken ct = default);
}

internal interface IVirtualDisplayStateSession
{
    Task<(IReadOnlyList<VddMonitor>? State, long ConnectionId)> ReadStateSessionAsync(CancellationToken ct);
    Task NotifyOnConnectionAsync(IReadOnlyList<VddMonitor> monitors, long expectedConnectionId, CancellationToken ct = default);
}

/// <summary>
/// Named-pipe client supporting both:
/// - virtual-display-rs master (InOut byte pipe, EOT 0x04 framing, State/Notify/Remove/RemoveAll)
/// - virtual-display-rs v0.3.1 (Out write-only pipe, raw JSON message, Add/Remove/RemoveAll)
/// </summary>
public sealed class VirtualDisplayIpcClient : IVirtualDisplayIpc, IVirtualDisplayState, IVirtualDisplayStateSession
{
    public const string DefaultPipeName = "virtualdisplaydriver";
    public const byte Eot = 0x04;
    private const int MaxFrameBytes = 1024 * 1024;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        // A later Notify replaces every monitor; fields we cannot round-trip must not be discarded.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _pipeName;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private CancellationTokenSource? _lifetime = new();
    private NamedPipeClientStream? _pipe;
    private V2Receiver? _receiver;
    private long _connectionId;
    private volatile bool _isLegacyProtocol;

    public VirtualDisplayIpcClient(string pipeName = DefaultPipeName)
    {
        _pipeName = pipeName;
    }

    public bool IsLegacyProtocol => _isLegacyProtocol || (IsPipeConnected(_pipe) && !_pipe!.CanRead);

    public static bool IsDriverPipeAvailable(string pipeName = DefaultPipeName)
    {
        try
        {
            return File.Exists($@"\\.\pipe\{pipeName}");
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<VddMonitor>?> ReadStateAsync(CancellationToken ct = default) =>
        (await ReadStateSessionAsync(ct).ConfigureAwait(false)).State;

    public async Task<(IReadOnlyList<VddMonitor>? State, long ConnectionId)> ReadStateSessionAsync(CancellationToken ct = default)
    {
        IReadOnlyList<VddMonitor>? state = null;
        long connectionId = 0;
        await WithPipeAsync(async (pipe, token) =>
        {
            connectionId = _connectionId;
            if (pipe.CanRead) state = await _receiver!.RequestStateAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return (state, connectionId);
    }

    public Task NotifyAsync(IReadOnlyList<VddMonitor> monitors, CancellationToken ct = default) =>
        WithPipeAsync((pipe, token) => WriteCommandAsync(pipe,
            pipe.CanRead ? SerializeNotify(monitors) : SerializeLegacyAdd(monitors), token), ct);

    public Task NotifyOnConnectionAsync(IReadOnlyList<VddMonitor> monitors, long expectedConnectionId, CancellationToken ct = default) =>
        WithPipeAsync((pipe, token) => WriteCommandAsync(pipe,
            pipe.CanRead ? SerializeNotify(monitors) : SerializeLegacyAdd(monitors), token), ct, expectedConnectionId);

    public Task ReplaceLegacyAsync(IReadOnlyList<uint> removeIds, IReadOnlyList<VddMonitor> addMonitors, CancellationToken ct = default) =>
        WithPipeAsync(async (pipe, token) =>
        {
            if (pipe.CanRead) throw new NotSupportedException("v2 驱动不支持 legacy 局部替换，未发送显示配置");
            await WriteCommandAsync(pipe, SerializeRemove(removeIds), token).ConfigureAwait(false);
            await Task.Delay(120, token).ConfigureAwait(false);
            await WriteCommandAsync(pipe, SerializeLegacyAdd(addMonitors), token).ConfigureAwait(false);
        }, ct);

    public Task RemoveAsync(IReadOnlyList<uint> ids, CancellationToken ct = default) =>
        WithPipeAsync((pipe, token) => WriteCommandAsync(pipe, SerializeRemove(ids), token), ct);

    public Task RemoveAllAsync(CancellationToken ct = default) =>
        WithPipeAsync((pipe, token) => WriteCommandAsync(pipe, SerializeRemoveAll(), token), ct);

    public static string SerializeNotify(IReadOnlyList<VddMonitor> monitors) =>
        JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<VddMonitor>>
        {
            ["Notify"] = monitors
        }, JsonOptions);

    public static string SerializeLegacyAdd(IReadOnlyList<VddMonitor> monitors)
    {
        var legacy = new List<VddLegacyMonitor>(monitors.Count);
        foreach (var m in monitors)
        {
            var modes = new List<VddLegacyMode>();
            foreach (var mode in m.Modes)
            {
                uint rr = mode.RefreshRates.Count > 0 ? mode.RefreshRates[0] : 60;
                modes.Add(new VddLegacyMode(mode.Width, mode.Height, rr));
            }
            legacy.Add(new VddLegacyMonitor(m.Id, modes));
        }
        return JsonSerializer.Serialize(new Dictionary<string, List<VddLegacyMonitor>>
        {
            ["Add"] = legacy
        }, JsonOptions);
    }

    public static string SerializeRemove(IReadOnlyList<uint> ids) =>
        JsonSerializer.Serialize(new Dictionary<string, IReadOnlyList<uint>>
        {
            ["Remove"] = ids
        }, JsonOptions);

    public static string SerializeRemoveAll() => "\"RemoveAll\"";

    public static string SerializeStateRequest() => "\"State\"";

    public static bool TryParseStateReply(string json, out IReadOnlyList<VddMonitor> monitors)
    {
        monitors = Array.Empty<VddMonitor>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            IsStateFrame(doc.RootElement, out var payload);
            monitors = ParseMonitors(payload);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return false; }
    }

    private static bool IsStateFrame(JsonElement root, out JsonElement payload)
    {
        if (root.ValueKind != JsonValueKind.Object || root.GetPropertyCount() != 1)
            throw new JsonException("Expected a single driver reply or event");
        bool isState = root.TryGetProperty("State", out payload);
        if ((!isState && !root.TryGetProperty("Changed", out payload)) || payload.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a State or Changed monitor list");
        return isState;
    }

    private static IReadOnlyList<VddMonitor> ParseMonitors(JsonElement payload)
    {
        var monitors = payload.Deserialize<List<VddMonitor>>(JsonOptions)!;
        if (monitors.Any(m => m is null || m.Modes.Any(mode => mode is null)))
            throw new JsonException("Null monitor or mode in driver state");
        return monitors;
    }

    private sealed class V2Receiver : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _pump;
        private TaskCompletionSource<IReadOnlyList<VddMonitor>>? _pending;
        private TaskCompletionSource<IReadOnlyList<VddMonitor>>? _frameRecipient;

        public V2Receiver(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _pump = Task.Run(ReceiveAsync);
        }

        public async Task<IReadOnlyList<VddMonitor>> RequestStateAsync(CancellationToken ct)
        {
            var reply = new TaskCompletionSource<IReadOnlyList<VddMonitor>>(TaskCreationOptions.RunContinuationsAsynchronously);
            // _ioGate permits one request; register before writing so a fast reply cannot be lost.
            Volatile.Write(ref _pending, reply);
            try
            {
                await WriteCommandAsync(_pipe, SerializeStateRequest(), ct).ConfigureAwait(false);
                var first = await Task.WhenAny(reply.Task, _pump).WaitAsync(ct).ConfigureAwait(false);
                if (first == _pump)
                {
                    await _pump.ConfigureAwait(false);
                    throw new EndOfStreamException("虚拟显示器驱动管道已断开");
                }
                ct.ThrowIfCancellationRequested();
                return await reply.Task.ConfigureAwait(false);
            }
            finally { Volatile.Write(ref _pending, null); }
        }

        private async Task ReceiveAsync()
        {
            var bytes = new byte[4096];
            var frame = new ArrayBufferWriter<byte>();
            try
            {
                while (true)
                {
                    int count = await _pipe.ReadAsync(bytes, _stop.Token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("虚拟显示器驱动管道已断开");
                    Consume(bytes.AsSpan(0, count), frame);
                }
            }
            finally { _pipe.Dispose(); }
        }

        private void Consume(ReadOnlySpan<byte> bytes, ArrayBufferWriter<byte> frame)
        {
            var recipient = Volatile.Read(ref _pending);
            while (!bytes.IsEmpty)
            {
                // A frame that started before this request cannot be its reply, even if EOT arrives later.
                if (frame.WrittenCount == 0) _frameRecipient = recipient;
                int eot = bytes.IndexOf(Eot);
                int length = eot < 0 ? bytes.Length : eot;
                if (frame.WrittenCount + length > MaxFrameBytes)
                    throw new InvalidDataException("虚拟显示器驱动回复超过大小限制");
                frame.Write(bytes[..length]);
                if (eot < 0) return;
                Dispatch(frame.WrittenMemory);
                _frameRecipient = null;
                frame.ResetWrittenCount();
                bytes = bytes[(length + 1)..];
            }
        }

        private void Dispatch(ReadOnlyMemory<byte> frame)
        {
            using var doc = JsonDocument.Parse(frame);
            if (!IsStateFrame(doc.RootElement, out var payload)) return;
            var state = ParseMonitors(payload);
            _frameRecipient?.TrySetResult(state);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _stop.Dispose();
        }
    }

    private async Task WithPipeAsync(Func<NamedPipeClientStream, CancellationToken, Task> operation, CancellationToken ct, long? expectedConnectionId = null)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var lifetime = Volatile.Read(ref _lifetime) ?? throw new ObjectDisposedException(nameof(VirtualDisplayIpcClient));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            deadline.CancelAfter(IoTimeout);
            if (expectedConnectionId is { } expected && (!IsPipeConnected(_pipe) || _connectionId != expected))
                throw new IOException("虚拟显示器驱动连接已断开，不能用旧状态发送 Notify");
            var pipe = await EnsureProtocolAsync(deadline.Token).ConfigureAwait(false);
            await operation(pipe, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            // A late State has no request ID; stop the old reader before releasing the gate or reconnecting.
            await DisconnectAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _lifetime) is null, this);
                throw new TimeoutException("虚拟显示器驱动管道请求超时", ex);
            }
            if (ex is JsonException) throw new InvalidDataException("虚拟显示器驱动 IPC 消息格式无效", ex);
            throw;
        }
        finally { _ioGate.Release(); }
    }

    private async Task<NamedPipeClientStream> EnsureProtocolAsync(CancellationToken ct)
    {
        if (IsPipeConnected(_pipe)) return _pipe!;
        await DisconnectAsync().ConfigureAwait(false);
        try
        {
            _pipe = await ConnectAsync(PipeDirection.InOut, 1500, ct).ConfigureAwait(false);
            _isLegacyProtocol = false;
            _receiver = new V2Receiver(_pipe);
        }
        catch (UnauthorizedAccessException)
        {
            // Only access denial is evidence of a v0.3.1 inbound pipe, not a stalled v2 driver.
            _pipe = await ConnectAsync(PipeDirection.Out, 2500, ct).ConfigureAwait(false);
            _isLegacyProtocol = true;
            Log.Info("检测到 virtual-display-rs v0.3.x 写通道协议（PipeDirection.Out + Add）");
        }
        _connectionId++;
        return _pipe;
    }

    private static bool IsPipeConnected(NamedPipeClientStream? pipe)
    {
        if (pipe is null) return false;
        try { return pipe.IsConnected; }
        catch (ObjectDisposedException) { return false; }
    }

    private async Task<NamedPipeClientStream> ConnectAsync(PipeDirection direction, int timeoutMs, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, direction, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static async Task WriteCommandAsync(NamedPipeClientStream pipe, string json, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(pipe.CanRead ? json + (char)Eot : json);
        await pipe.WriteAsync(payload, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task DisconnectAsync()
    {
        if (_receiver is not null) await _receiver.DisposeAsync().ConfigureAwait(false);
        _receiver = null;
        _pipe?.Dispose();
        _pipe = null;
    }

    public async ValueTask DisposeAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        lifetime?.Cancel();
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally
        {
            lifetime?.Dispose();
            _ioGate.Release();
        }
    }
}
