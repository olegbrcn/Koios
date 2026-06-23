using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Koios.Core;

// Resident host: owns one warm Engine and answers requests over a Unix domain socket.
// The loaded Roslyn Solution is immutable, so requests are handled concurrently.
public sealed class Server : IDisposable
{
    private readonly Engine engine;
    private readonly string socketPath;
    private readonly TimeSpan idleTimeout;
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly DateTime startedAt = DateTime.UtcNow;
    private Socket? listener;
    private int requestCount;
    private long lastActivityTicks = DateTime.UtcNow.Ticks;

    public CancellationToken ShutdownToken => shutdownCts.Token;

    public Server(Engine engine, string socketPath, TimeSpan idleTimeout)
    {
        this.engine = engine;
        this.socketPath = socketPath;
        this.idleTimeout = idleTimeout;
    }

    public void Start()
    {
        if (File.Exists(socketPath)) File.Delete(socketPath); // clear a stale socket
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(backlog: 16);
        _ = AcceptLoopAsync(shutdownCts.Token);
        _ = IdleWatchdogAsync(shutdownCts.Token);
    }

    public void Stop() => shutdownCts.Cancel();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener!.AcceptAsync(ct);
                _ = Task.Run(() => HandleAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex) { Console.Error.WriteLine($"accept loop error: {ex.Message}"); }
    }

    private async Task HandleAsync(Socket client, CancellationToken ct)
    {
        using var _ = client;
        using var stream = new NetworkStream(client, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(TimeSpan.FromMinutes(2)); // per-request ceiling

        try
        {
            var line = await reader.ReadLineAsync(reqCts.Token);
            if (line is null) return;

            Interlocked.Increment(ref requestCount);
            Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);

            var req = JsonSerializer.Deserialize<Request>(line, Protocol.Wire)
                      ?? new Request();

            if (req.Verb == Protocol.ShutdownVerb)
            {
                await WriteAsync(writer, new Envelope<object> { State = "stopping", Notes = new[] { "shutting down" } });
                Stop();
                return;
            }

            object result = req.Verb == "status"
                ? engine.Status(ResidentSnapshot())
                : await Protocol.DispatchAsync(engine, req, reqCts.Token);
            await WriteAsync(writer, result);
        }
        catch (OperationCanceledException)
        {
            await TryWriteErrorAsync(writer, "timeout", "Request timed out or server is stopping.", retryable: true);
        }
        catch (Exception ex)
        {
            await TryWriteErrorAsync(writer, "internal_error", ex.Message, retryable: false);
        }
    }

    private ResidentInfo ResidentSnapshot() => new()
    {
        Pid = Environment.ProcessId,
        SocketPath = socketPath,
        UptimeSeconds = (long)(DateTime.UtcNow - startedAt).TotalSeconds,
        RequestsServed = Volatile.Read(ref requestCount),
    };

    private async Task IdleWatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var last = new DateTime(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);
                if (DateTime.UtcNow - last > idleTimeout)
                {
                    Console.Error.WriteLine($"idle for {idleTimeout.TotalMinutes:0} min; shutting down.");
                    Stop();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private static async Task WriteAsync(StreamWriter writer, object envelope)
    {
        // Serialize by runtime type so the boxed Envelope<T> emits its real shape.
        var json = JsonSerializer.Serialize(envelope, envelope.GetType(), Protocol.Wire);
        await writer.WriteLineAsync(json);
    }

    private static async Task TryWriteErrorAsync(StreamWriter writer, string code, string message, bool retryable)
    {
        try
        {
            await WriteAsync(writer, new Envelope<object>
            {
                Ok = false,
                Error = new ErrorInfo { Code = code, Message = message, Retryable = retryable },
            });
        }
        catch { /* client already gone */ }
    }

    public void Dispose()
    {
        shutdownCts.Cancel();
        listener?.Dispose();
        engine.Dispose();
        if (File.Exists(socketPath)) File.Delete(socketPath);
        shutdownCts.Dispose();
    }
}
