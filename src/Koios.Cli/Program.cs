using System.Text.Json;
using CommandLine;
using Koios.Core;

return await Cli.RunAsync(args);

// ---------------------------------------------------------------------------
// Verb option classes. Shared flags live on GlobalOptions; CommandLineParser
// picks up inherited properties, so every verb gets --solution / --format.
// ---------------------------------------------------------------------------

abstract class GlobalOptions
{
    [Option('s', "solution", HelpText = ".sln/.slnx/.csproj or directory (default: $KOIOS_SOLUTION or cwd).")]
    public string? Solution { get; set; }

    [Option("format", Default = "text", HelpText = "Output format: text or json.")]
    public string Format { get; set; } = "text";
}

[Verb("status", HelpText = "Load the solution and report health.")]
sealed class StatusOptions : GlobalOptions { }

[Verb("serve", HelpText = "Load the solution once and stay resident, serving queries over a local socket (foreground; Ctrl+C to stop). Serves a fixed snapshot — restart to pick up on-disk edits.")]
sealed class ServeOptions : GlobalOptions
{
    [Option("idle-timeout", Default = 15, HelpText = "Minutes of inactivity before the resident self-shuts down.")]
    public int IdleTimeout { get; set; }
}

[Verb("stop", HelpText = "Stop the resident server for the solution.")]
sealed class StopOptions : GlobalOptions { }

[Verb("search", HelpText = "Ranked fuzzy symbol search.")]
sealed class SearchOptions : GlobalOptions
{
    [Value(0, Required = true, MetaName = "query", HelpText = "Name / substring / camel-hump to search for.")]
    public string Query { get; set; } = "";

    [Option("kinds", HelpText = "Comma-separated kind filter, e.g. class,interface,method.")]
    public string? Kinds { get; set; }

    [Option("limit", Default = 50, HelpText = "Max results.")]
    public int Limit { get; set; }
}

[Verb("outline", HelpText = "Structural outline (types → members) of a file.")]
sealed class OutlineOptions : GlobalOptions
{
    [Value(0, Required = true, MetaName = "file", HelpText = "Path to a .cs file.")]
    public string File { get; set; } = "";
}

[Verb("def", HelpText = "Go to definition.")]
sealed class DefOptions : GlobalOptions
{
    [Value(0, MetaName = "target", HelpText = "file:line:col, a symbol_id, or a symbol name. Omit if using --id.")]
    public string? Target { get; set; }

    [Option("id", HelpText = "Target a symbol by its symbol_id (DocumentationCommentId).")]
    public string? Id { get; set; }
}

[Verb("hover", HelpText = "Signature, container, and XML-doc summary.")]
sealed class HoverOptions : GlobalOptions
{
    [Value(0, MetaName = "target", HelpText = "file:line:col, a symbol_id, or a symbol name. Omit if using --id.")]
    public string? Target { get; set; }

    [Option("id", HelpText = "Target a symbol by its symbol_id (DocumentationCommentId).")]
    public string? Id { get; set; }
}

// Shared target selector for relational commands.
abstract class TargetOptions : GlobalOptions
{
    [Value(0, MetaName = "target", HelpText = "file:line:col, a symbol_id, or a symbol name. Omit if using --id.")]
    public string? Target { get; set; }

    [Option("id", HelpText = "Target a symbol by its symbol_id (DocumentationCommentId).")]
    public string? Id { get; set; }
}

[Verb("refs", HelpText = "Find all references (reads/writes/calls) to a symbol.")]
sealed class RefsOptions : TargetOptions
{
    [Option("include-declaration", HelpText = "Include the declaration itself.")]
    public bool IncludeDeclaration { get; set; }

    [Option("limit", Default = 100, HelpText = "Max references.")]
    public int Limit { get; set; }
}

[Verb("callers", HelpText = "Incoming call hierarchy: who calls the target method.")]
sealed class CallersOptions : TargetOptions
{
    [Option("depth", Default = 1, HelpText = "Levels of callers to expand.")]
    public int Depth { get; set; }

    [Option("limit", Default = 50, HelpText = "Max top-level callers.")]
    public int Limit { get; set; }
}

[Verb("impls", HelpText = "Implementations / overrides / derived types of the target.")]
sealed class ImplsOptions : TargetOptions
{
    [Option("of", HelpText = "Filter to the closed generic where the first type argument matches this name (e.g. --of VehicleRawDataReceivedEventMessage).")]
    public string? Of { get; set; }

    [Option("limit", Default = 100, HelpText = "Max results.")]
    public int Limit { get; set; }
}

[Verb("injectors", HelpText = "Classes that declare the target type as a constructor parameter (DI injection sites).")]
sealed class InjectorsOptions : TargetOptions
{
    [Option("limit", Default = 100, HelpText = "Max results.")]
    public int Limit { get; set; }
}

[Verb("hierarchy", HelpText = "Base/interface and derived-type hierarchy of a type.")]
sealed class HierarchyOptions : TargetOptions
{
    [Option("direction", Default = "both", HelpText = "both | base | derived.")]
    public string Direction { get; set; } = "both";

    [Option("limit", Default = 100, HelpText = "Max results.")]
    public int Limit { get; set; }
}

[Verb("diagnostics", HelpText = "Compiler diagnostics for a file, project, or the solution.")]
sealed class DiagnosticsOptions : GlobalOptions
{
    [Value(0, MetaName = "path", HelpText = "File to scope to (implies --scope file).")]
    public string? Path { get; set; }

    [Option("scope", HelpText = "file | project | solution (default: file if a path is given, else solution).")]
    public string? Scope { get; set; }

    [Option("project", HelpText = "Project name for --scope project.")]
    public string? Project { get; set; }

    [Option("min-severity", Default = "warning", HelpText = "hidden | info | warning | error.")]
    public string MinSeverity { get; set; } = "warning";

    [Option("limit", Default = 100, HelpText = "Max diagnostics.")]
    public int Limit { get; set; }
}

static class Cli
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Task<int> RunAsync(string[] args) =>
        Parser.Default
            .ParseArguments<StatusOptions, ServeOptions, StopOptions, SearchOptions, OutlineOptions, DefOptions, HoverOptions,
                            RefsOptions, CallersOptions, ImplsOptions, InjectorsOptions, HierarchyOptions, DiagnosticsOptions>(args)
            .MapResult(
                (StatusOptions o) => Route<StatusInfo>(o, new Request { Verb = "status" }, StatusText),
                (ServeOptions o) => ServeAsync(o),
                (StopOptions o) => StopAsync(o),
                (SearchOptions o) => Route<SymbolItem>(o, new Request
                {
                    Verb = "search",
                    Args = new RequestArgs
                    {
                        Query = o.Query,
                        Kinds = string.IsNullOrWhiteSpace(o.Kinds)
                            ? null
                            : o.Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        Limit = o.Limit,
                    },
                }, SearchText),
                (OutlineOptions o) => Route<OutlineNode>(o, new Request { Verb = "outline", Args = new RequestArgs { File = o.File } }, OutlineText),
                (DefOptions o) => RouteTarget<SymbolItem>(o, o.Target, o.Id, "def", t => t, SearchText),
                (HoverOptions o) => RouteTarget<HoverItem>(o, o.Target, o.Id, "hover", t => t, HoverText),
                (RefsOptions o) => RouteTarget<ReferenceItem>(o, o.Target, o.Id, "refs",
                    t => t with { IncludeDeclaration = o.IncludeDeclaration, Limit = o.Limit }, RefsText),
                (CallersOptions o) => RouteTarget<CallNode>(o, o.Target, o.Id, "callers",
                    t => t with { Depth = o.Depth, Limit = o.Limit }, CallersText),
                (ImplsOptions o) => RouteTarget<SymbolItem>(o, o.Target, o.Id, "impls",
                    t => t with { Of = o.Of, Limit = o.Limit }, ImplsText),
                (InjectorsOptions o) => RouteTarget<SymbolItem>(o, o.Target, o.Id, "injectors",
                    t => t with { Limit = o.Limit }, InjectorsText),
                (HierarchyOptions o) => RouteTarget<HierarchyItem>(o, o.Target, o.Id, "hierarchy",
                    t => t with { Direction = o.Direction, Limit = o.Limit }, HierarchyText),
                (DiagnosticsOptions o) => Route<DiagnosticItem>(o, new Request
                {
                    Verb = "diagnostics",
                    Args = new RequestArgs
                    {
                        Scope = o.Scope ?? (o.Path is not null ? "file" : "solution"),
                        Path = o.Path,
                        Project = o.Project,
                        MinSeverity = o.MinSeverity,
                        Limit = o.Limit,
                    },
                }, DiagnosticsText),
                // 0 for --help/--version, 1 for genuine parse/usage errors.
                errs => Task.FromResult(errs.All(IsHelpOrVersion) ? 0 : 1));

    static bool IsHelpOrVersion(Error e) =>
        e.Tag is ErrorType.HelpRequestedError or ErrorType.HelpVerbRequestedError or ErrorType.VersionRequestedError;

    /// <summary>
    /// Resolve the solution, bring MSBuild up in the right order, load the workspace,
    /// then run the command body. Keeps the bootstrap ordering in one place.
    /// </summary>
    static string ResolveSolution(GlobalOptions o) =>
        o.Solution ?? Environment.GetEnvironmentVariable("KOIOS_SOLUTION") ?? Directory.GetCurrentDirectory();

    // Every query verb routes to the resident; without one it fails with an actionable
    // error (queries never cold-load — the only cold load is `serve`).
    static async Task<int> Route<T>(GlobalOptions opts, Request request, Action<Envelope<T>> renderer)
    {
        var solution = ResolveSolution(opts);
        var sock = RuntimeDir.SocketPathFor(solution);
        if (!SocketClient.IsRunning(sock))
        {
            var env = new Envelope<T>
            {
                Ok = false,
                State = "error",
                Error = new ErrorInfo
                {
                    Code = "no_resident",
                    Message = $"no resident server for '{solution}'",
                    Hint = $"start one with: koios serve -s '{solution}'",
                    Retryable = false,
                },
            };
            return Emit(env, opts.Format, renderer);
        }
        try
        {
            var env = await SocketClient.QueryAsync<T>(sock, request, default);
            return Emit(env, opts.Format, renderer);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 4;
        }
    }

    // Target verbs: parse file:line:col / --id / bare name into the request, then route.
    static Task<int> RouteTarget<T>(GlobalOptions opts, string? target, string? id, string verb,
        Func<RequestArgs, RequestArgs> extra, Action<Envelope<T>> renderer)
    {
        if (!TryTarget(target, id, out var t))
            return Task.FromResult(1);
        var args = new RequestArgs { Path = t.path, Line = t.line, Col = t.col, SymbolId = t.id };
        return Route(opts, new Request { Verb = verb, Args = extra(args) }, renderer);
    }

    static async Task<int> ServeAsync(ServeOptions o)
    {
        var solution = ResolveSolution(o);
        var sock = RuntimeDir.SocketPathFor(solution);
        if (SocketClient.IsRunning(sock))
        {
            Console.Error.WriteLine($"error: a resident server is already running for '{solution}' (socket: {sock}).");
            Console.Error.WriteLine($"  stop it with: koios stop -s '{solution}'");
            return 1;
        }

        RepoSdk.Configure(solution);
        MSBuildBootstrap.Register();

        var engine = new Engine();
        try
        {
            await engine.LoadAsync(solution);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: failed to load '{solution}': {ex.Message}");
            engine.Dispose();
            return 2;
        }
        if (engine.LoadFailed)
        {
            Console.Error.WriteLine($"error: solution not loaded: {engine.FatalLoadError ?? "unknown error"}");
            engine.Dispose();
            return 3;
        }

        var s = engine.Status().Items[0];
        Console.Error.WriteLine($"loaded {s.Projects.Loaded}/{s.Projects.Total} projects, {s.Documents} documents, {s.Symbols} symbols in {s.LoadMs} ms");
        Console.Error.WriteLine($"serving {sock} (idle timeout {o.IdleTimeout} min) — Ctrl+C to stop");

        using var server = new Server(engine, sock, TimeSpan.FromMinutes(o.IdleTimeout));
        server.Start();

        var done = new TaskCompletionSource();
        using var reg = server.ShutdownToken.Register(() => done.TrySetResult());
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
        await done.Task;
        Console.Error.WriteLine("stopped.");
        return 0;
    }

    static async Task<int> StopAsync(StopOptions o)
    {
        var solution = ResolveSolution(o);
        var sock = RuntimeDir.SocketPathFor(solution);
        if (!SocketClient.IsRunning(sock))
        {
            Console.Error.WriteLine($"no resident server running for '{solution}'.");
            if (File.Exists(sock)) { try { File.Delete(sock); } catch { /* best-effort stale cleanup */ } }
            return 1;
        }
        try
        {
            await SocketClient.ShutdownAsync(sock, default);
            Console.Error.WriteLine($"stopped resident server for '{solution}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 4;
        }
    }

    // A target is either `file:line:col` or a symbol_id (via positional or --id).
    static bool TryTarget(string? target, string? id, out (string? path, int? line, int? col, string? id) t)
    {
        if (!string.IsNullOrEmpty(id))
        {
            t = (null, null, null, id);
            return true;
        }
        if (!string.IsNullOrEmpty(target))
        {
            var parts = target.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[1], out var line) && int.TryParse(parts[2], out var col))
                t = (parts[0], line, col, null);
            else
                t = (null, null, null, target); // a symbol_id (monikers carry a single ':')
            return true;
        }
        Console.Error.WriteLine("error: provide a target as file:line:col, a symbol name, or --id <symbol_id>.");
        t = default;
        return false;
    }

    static int Emit<T>(Envelope<T> env, string format, Action<Envelope<T>> text)
    {
        if (format == "json")
            Console.WriteLine(JsonSerializer.Serialize(env, Json));
        else
            text(env);
        return env.Ok ? 0 : 3;
    }

    // ---- text renderers ----

    static void StatusText(Envelope<StatusInfo> env)
    {
        if (!env.Ok) { Error(env); return; }
        var s = env.Items[0];
        Console.WriteLine($"state:      {s.State}{(env.Degraded ? " (degraded)" : "")}");
        Console.WriteLine($"solution:   {s.Solution.File}  [{s.Solution.Root}]");
        Console.WriteLine($"msbuild:    {s.MSBuild}");
        Console.WriteLine($"projects:   {s.Projects.Loaded}/{s.Projects.Total} loaded, {s.Projects.Failed} failed");
        Console.WriteLine($"documents:  {s.Documents}");
        Console.WriteLine($"symbols:    {s.Symbols}");
        Console.WriteLine($"load time:  {s.LoadMs} ms");
        if (s.Resident is { } r)
            Console.WriteLine($"resident:   pid {r.Pid}, uptime {r.UptimeSeconds}s, {r.RequestsServed} request(s)  [{r.SocketPath}]");
        if (s.LoadErrors.Count > 0)
        {
            Console.WriteLine($"load errors ({s.LoadErrors.Count}):");
            foreach (var e in s.LoadErrors) Console.WriteLine($"  - {Truncate(e, 200)}");
        }
    }

    static void SearchText(Envelope<SymbolItem> env)
    {
        if (!env.Ok) { Error(env); return; }
        if (env.Items.Count == 0) { Console.WriteLine("(no matches)"); return; }
        foreach (var it in env.Items)
        {
            var loc = it.Loc is null ? (it.IsMetadata ? "[metadata]" : "") : $"{it.Loc.Path}:{it.Loc.Line}:{it.Loc.Col}";
            var score = it.Score is { } sc ? $"  ({sc:0.00})" : "";
            Console.WriteLine($"{it.Kind,-12} {it.Name,-32} {loc}{score}");
            if (it.Signature is { Length: > 0 } sig) Console.WriteLine($"             {Truncate(sig, 160)}");
        }
        Console.WriteLine($"\n{env.Items.Count} result(s) · {env.ElapsedMs} ms · {env.Tier}");
    }

    static void OutlineText(Envelope<OutlineNode> env)
    {
        if (!env.Ok) { Error(env); return; }
        foreach (var node in env.Items) PrintNode(node, 0);
        Console.WriteLine($"\n{env.ElapsedMs} ms · {env.Tier}");
    }

    static void PrintNode(OutlineNode n, int depth)
    {
        var indent = new string(' ', depth * 2);
        var line = n.Loc is null ? "" : $":{n.Loc.Line}";
        Console.WriteLine($"{indent}{n.Kind} {n.Name}{line}");
        foreach (var c in n.Children) PrintNode(c, depth + 1);
    }

    static void HoverText(Envelope<HoverItem> env)
    {
        if (!env.Ok) { Error(env); return; }
        var h = env.Items[0];
        Console.WriteLine($"{h.Signature}");
        Console.WriteLine($"  kind:      {h.Kind}");
        if (h.Container is { Length: > 0 }) Console.WriteLine($"  container: {h.Container}");
        if (h.ReturnType is { Length: > 0 }) Console.WriteLine($"  returns:   {h.ReturnType}");
        if (h.Loc is not null) Console.WriteLine($"  at:        {h.Loc.Path}:{h.Loc.Line}:{h.Loc.Col}");
        if (h.IsMetadata) Console.WriteLine($"  (metadata symbol)");
        if (h.DocSummary is { Length: > 0 }) Console.WriteLine($"  summary:   {h.DocSummary}");
    }

    static void RefsText(Envelope<ReferenceItem> env)
    {
        if (!env.Ok) { Error(env); return; }
        if (env.Items.Count == 0) { Console.WriteLine("(no references)"); }
        foreach (var byFile in env.Items.GroupBy(i => i.Loc.Path))
        {
            Console.WriteLine($"{byFile.Key}");
            foreach (var r in byFile)
                Console.WriteLine($"  {r.Loc.Line,5}:{r.Loc.Col,-3} {r.Kind,-11} {Truncate(r.Loc.Snippet, 120)}");
        }
        PrintFooter(env, env.Items.Count, "reference(s)");
    }

    static void CallersText(Envelope<CallNode> env)
    {
        if (!env.Ok) { Error(env); return; }
        if (env.Items.Count == 0) Console.WriteLine("(no callers)");
        foreach (var n in env.Items) PrintCall(n, 0);
        PrintFooter(env, env.Items.Count, "caller(s)");
    }

    static void PrintCall(CallNode n, int depth)
    {
        var indent = new string(' ', depth * 2);
        var where = n.CallSites.Count > 0 ? $"  ({n.CallSites[0].Path}:{n.CallSites[0].Line})" : "";
        var container = n.Container is { Length: > 0 } ? $"{n.Container}." : "";
        Console.WriteLine($"{indent}{container}{n.Name}{where}");
        if (n.Children is not null)
            foreach (var c in n.Children) PrintCall(c, depth + 1);
    }

    static void ImplsText(Envelope<SymbolItem> env)
    {
        if (!env.Ok) { Error(env); return; }
        if (env.Items.Count == 0) { Console.WriteLine("(none)"); }
        foreach (var it in env.Items)
        {
            var loc = it.Loc is null ? (it.IsMetadata ? "[metadata]" : "") : $"{it.Loc.Path}:{it.Loc.Line}";
            Console.WriteLine($"{it.Relation,-11} {it.Kind,-10} {it.Name,-32} {loc}");
        }
        PrintFooter(env, env.Items.Count, "result(s)");
    }

    static void InjectorsText(Envelope<SymbolItem> env)
    {
        if (!env.Ok) { Error(env); return; }
        if (env.Items.Count == 0) Console.WriteLine("(none)");
        foreach (var it in env.Items)
        {
            var loc = it.Loc is null ? "" : $"{it.Loc.Path}:{it.Loc.Line}";
            Console.WriteLine($"{it.Relation,-11} {it.Kind,-10} {it.Name,-32} {loc}");
        }
        PrintFooter(env, env.Items.Count, "injector(s)");
    }

    static void HierarchyText(Envelope<HierarchyItem> env)
    {
        if (!env.Ok) { Error(env); return; }
        if (env.Items.Count == 0) { Console.WriteLine("(none)"); }
        foreach (var it in env.Items)
        {
            var loc = it.Loc is null ? (it.IsMetadata ? "[metadata]" : "") : $"{it.Loc.Path}:{it.Loc.Line}";
            Console.WriteLine($"{it.Relation,-10} d{it.Depth} {it.Kind,-10} {it.Name,-32} {loc}");
        }
        PrintFooter(env, env.Items.Count, "type(s)");
    }

    static void DiagnosticsText(Envelope<DiagnosticItem> env)
    {
        if (!env.Ok) { Error(env); return; }
        foreach (var d in env.Items)
        {
            var at = d.Loc is null ? "" : $"{d.Loc.Path}:{d.Loc.Line}:{d.Loc.Col} ";
            Console.WriteLine($"{d.Severity,-7} {d.Id,-8} {at}{Truncate(d.Message, 120)}");
        }
        foreach (var note in env.Notes) Console.WriteLine($"\n{note}");
        Console.WriteLine($"{env.ElapsedMs} ms · {env.Tier}");
    }

    static void PrintFooter<T>(Envelope<T> env, int count, string noun)
    {
        foreach (var note in env.Notes) Console.WriteLine($"  ({note})");
        Console.WriteLine($"\n{count} {noun} · {env.ElapsedMs} ms · {env.Tier}");
    }

    static void Error<T>(Envelope<T> env)
    {
        Console.WriteLine($"error: {env.Error?.Code}: {env.Error?.Message}");
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
