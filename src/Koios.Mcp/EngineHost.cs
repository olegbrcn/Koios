using Koios.Core;

namespace Koios.Mcp;

/// <summary>
/// Owns the in-process Engine for the MCP server: kicks off the cold load in the
/// background so the JSON-RPC handshake is not blocked by it, gates tools on
/// readiness, and keeps the snapshot current via the Watcher. This process IS the
/// resident — the agent spawns it once and it lives for the session.
/// </summary>
public sealed class EngineHost : IDisposable
{
    private readonly string solution;
    private readonly Engine engine = new();
    private readonly CancellationTokenSource cts = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTime startedAt = DateTime.UtcNow;
    private Watcher? watcher;
    private int requestCount;

    public EngineHost(string solution) => this.solution = solution;

    /// <summary>Start loading the workspace. Called once at process start; tools
    /// await <see cref="ReadyAsync"/> and so block until this completes.</summary>
    public void Begin()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                RepoSdk.Configure(solution);
                MSBuildBootstrap.Register();
                await engine.LoadAsync(solution, cts.Token);
                if (engine.LoadFailed)
                {
                    ready.TrySetException(new InvalidOperationException(
                        engine.FatalLoadError ?? "solution failed to load"));
                    return;
                }

                var s = engine.Status().Items[0];
                Console.Error.WriteLine(
                    $"koios: loaded {s.Projects.Loaded}/{s.Projects.Total} projects, {s.Documents} documents, {s.Symbols} symbols in {s.LoadMs} ms");

                watcher = new Watcher(engine, cts.Token);
                watcher.Log += msg => Console.Error.WriteLine($"koios: {msg}");
                try
                {
                    watcher.Start();
                    Console.Error.WriteLine($"koios: watching {engine.Root} for changes");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"koios: watch disabled ({ex.Message}); serving a fixed snapshot");
                }

                ready.TrySetResult();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        });
    }

    /// <summary>The warm engine, once loaded. A tool call during the cold load
    /// simply waits; a failed load surfaces as the tool call's error.</summary>
    public async Task<Engine> ReadyAsync(CancellationToken ct)
    {
        await ready.Task.WaitAsync(ct);
        Interlocked.Increment(ref requestCount);
        return engine;
    }

    /// <summary>Status must answer during (and after a failed) load, so it does not
    /// gate on readiness like the other verbs.</summary>
    public Envelope<StatusInfo> Status()
    {
        Interlocked.Increment(ref requestCount);
        if (ready.Task.IsFaulted)
        {
            var why = ready.Task.Exception?.GetBaseException().Message ?? "load failed";
            return new Envelope<StatusInfo>
            {
                Ok = false,
                State = "error",
                SnapshotId = "sln@0",
                Error = new ErrorInfo { Code = "load_failed", Message = why, Retryable = false },
            };
        }
        if (!ready.Task.IsCompleted)
        {
            return new Envelope<StatusInfo>
            {
                State = "loading",
                SnapshotId = "sln@0",
                Items = new[] { new StatusInfo { State = "loading", Solution = new SolutionInfo { Root = solution } } },
                Notes = new[] { "workspace is loading; other koios tools will answer once it is ready" },
            };
        }
        return engine.Status(new ResidentInfo
        {
            Pid = Environment.ProcessId,
            SocketPath = "(in-process, stdio)",
            UptimeSeconds = (long)(DateTime.UtcNow - startedAt).TotalSeconds,
            RequestsServed = Volatile.Read(ref requestCount),
        });
    }

    public void Dispose()
    {
        cts.Cancel();
        watcher?.Dispose();
        engine.Dispose();
        cts.Dispose();
    }
}
