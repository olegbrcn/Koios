using System.Diagnostics;

namespace Koios.Core;

/// <summary>
/// Watches the solution tree and keeps a resident Engine's snapshot current.
///
/// Events are coalesced into one pending batch and applied after a quiet period
/// (the debounce extends while events keep streaming, so a git checkout lands as
/// one batch). The apply loop is the engine's single writer: .cs-only batches go
/// through the incremental path (<see cref="Engine.ApplyCsBatchAsync"/>); anything
/// that changes the project graph — a .csproj/.props/.targets/.sln/global.json
/// edit, a watcher overflow, a deleted directory that held loaded documents, or a
/// batch too large to re-project — escalates the whole batch to a full background
/// reload, during which queries keep answering from the previous snapshot.
/// </summary>
public sealed class Watcher : IDisposable
{
    private const int DebounceMs = 250;
    private const int BulkReloadThreshold = 500; // .cs files per batch; beyond this a full reload is cheaper/predictable

    private static readonly string[] IgnoredDirs = { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };
    private static readonly HashSet<string> ReloadExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".csproj", ".props", ".targets", ".sln", ".slnx", ".slnf" };
    private static readonly HashSet<string> ReloadFileNames = new(StringComparer.OrdinalIgnoreCase)
        { "global.json", "nuget.config" };

    private readonly Engine engine;
    private readonly CancellationToken ct;
    private readonly object gate = new();
    private readonly Dictionary<string, WatcherChangeTypes> pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim signal = new(0, 1);
    private FileSystemWatcher? fsw;
    private bool overflowed;
    private long lastEventTicks;

    /// <summary>Human-readable progress lines (serve logs them to stderr).</summary>
    public event Action<string>? Log;

    public Watcher(Engine engine, CancellationToken shutdownToken)
    {
        this.engine = engine;
        ct = shutdownToken;
    }

    public void Start()
    {
        fsw = new FileSystemWatcher(engine.Root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        fsw.Created += OnChanged;
        fsw.Changed += OnChanged;
        fsw.Deleted += OnChanged;
        fsw.Renamed += OnRenamed;
        fsw.Error += OnError;
        fsw.EnableRaisingEvents = true;
        _ = Task.Run(LoopAsync, CancellationToken.None);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // Directory mtime ticks whenever a child changes — the child raises its own
        // event, so a Changed on a directory itself is pure noise.
        if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
            return;
        Enqueue(e.FullPath, e.ChangeType);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Enqueue(e.OldFullPath, WatcherChangeTypes.Deleted);
        // Created (not Renamed) so a directory moved into the tree gets scanned.
        Enqueue(e.FullPath, WatcherChangeTypes.Created);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Event buffer overflowed — we lost events, so the pending batch is
        // incomplete. Escalate to a full reload.
        lock (gate) overflowed = true;
        Kick();
    }

    private void Enqueue(string path, WatcherChangeTypes kind)
    {
        if (IsIgnored(path))
            return;
        lock (gate)
            pending[path] = pending.TryGetValue(path, out var prior) ? prior | kind : kind;
        Kick();
    }

    private void Kick()
    {
        Interlocked.Exchange(ref lastEventTicks, DateTime.UtcNow.Ticks);
        try { signal.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
    }

    private bool IsIgnored(string path)
    {
        var rel = Path.GetRelativePath(engine.Root, path);
        if (rel.StartsWith("..", StringComparison.Ordinal))
            return true;
        foreach (var segment in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (IgnoredDirs.Contains(segment, StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await signal.WaitAsync(ct);

                // Quiet-period debounce: keep extending while events stream in, so a
                // bulk operation (git checkout, mass format) coalesces into one batch.
                while (true)
                {
                    await Task.Delay(DebounceMs, ct);
                    var last = new DateTime(Interlocked.Read(ref lastEventTicks), DateTimeKind.Utc);
                    if ((DateTime.UtcNow - last).TotalMilliseconds >= DebounceMs)
                        break;
                }

                Dictionary<string, WatcherChangeTypes> batch;
                bool reload;
                lock (gate)
                {
                    batch = new Dictionary<string, WatcherChangeTypes>(pending, StringComparer.Ordinal);
                    pending.Clear();
                    reload = overflowed;
                    overflowed = false;
                }

                try
                {
                    await ApplyAsync(batch, reload);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"watch: apply failed ({ex.Message}); still serving {engine.SnapshotId}");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task ApplyAsync(Dictionary<string, WatcherChangeTypes> batch, bool reload)
    {
        string? trigger = reload ? "watcher overflow" : null;
        var cs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, kind) in batch)
        {
            foreach (var p in Expand(path, kind))
            {
                if (IsReloadTrigger(p))
                {
                    reload = true;
                    trigger ??= Rel(p);
                }
                else if (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    cs.Add(p);
                }
                else if (!File.Exists(p) && !Directory.Exists(p) && engine.HasDocumentsUnder(p))
                {
                    // A deleted/renamed-away directory that held loaded documents:
                    // inotify reported only the directory, so we cannot enumerate
                    // what went with it — reload.
                    reload = true;
                    trigger ??= $"{Rel(p)}/ removed";
                }
            }
        }

        if (!reload && cs.Count > BulkReloadThreshold)
        {
            reload = true;
            trigger = $"{cs.Count} .cs files changed";
        }

        if (reload)
        {
            Log?.Invoke($"watch: full reload ({trigger}) — serving {engine.SnapshotId} meanwhile…");
            var sw = Stopwatch.StartNew();
            var ok = await engine.ReloadAsync(ct);
            Log?.Invoke(ok
                ? $"watch: reloaded → {engine.SnapshotId} in {sw.ElapsedMilliseconds} ms"
                : $"watch: reload failed; still serving {engine.SnapshotId} (see status load_errors)");
        }
        else if (cs.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            await engine.ApplyCsBatchAsync(cs.ToList(), ct);
            Log?.Invoke($"watch: {cs.Count} file(s) → {engine.SnapshotId} in {sw.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>A path from the pending map, expanded: a directory that appeared
    /// (created or moved in) is scanned recursively, because its children never
    /// raised their own events; anything else passes through.</summary>
    private IEnumerable<string> Expand(string path, WatcherChangeTypes kind)
    {
        if (Directory.Exists(path))
        {
            if ((kind & WatcherChangeTypes.Created) == 0)
                yield break;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories); }
            catch (Exception) { yield break; }
            foreach (var f in files)
                if (!IsIgnored(f))
                    yield return f;
            yield break;
        }
        yield return path;
    }

    private static bool IsReloadTrigger(string path) =>
        ReloadExtensions.Contains(Path.GetExtension(path))
        || ReloadFileNames.Contains(Path.GetFileName(path));

    private string Rel(string path)
    {
        try { return Path.GetRelativePath(engine.Root, path).Replace('\\', '/'); }
        catch { return path; }
    }

    public void Dispose() => fsw?.Dispose();
}
