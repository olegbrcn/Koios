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
            .ParseArguments<StatusOptions, SearchOptions, OutlineOptions, DefOptions, HoverOptions,
                            RefsOptions, CallersOptions, ImplsOptions, HierarchyOptions, DiagnosticsOptions>(args)
            .MapResult(
                (StatusOptions o) => WithEngine(o, allowLoadFailure: true,
                    (engine, _) => Task.FromResult(Emit(engine.Status(), o.Format, StatusText))),
                (SearchOptions o) => WithEngine(o, allowLoadFailure: false, (engine, oo) =>
                {
                    var kinds = string.IsNullOrWhiteSpace(oo.Kinds)
                        ? null
                        : new HashSet<string>(oo.Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    return Task.FromResult(Emit(engine.Search(oo.Query, kinds, oo.Limit), oo.Format, SearchText));
                }),
                (OutlineOptions o) => WithEngine(o, allowLoadFailure: false,
                    (engine, oo) => Task.FromResult(Emit(engine.Outline(oo.File), oo.Format, OutlineText))),
                (DefOptions o) => WithEngine(o, allowLoadFailure: false, async (engine, oo) =>
                {
                    if (!TryTarget(oo.Target, oo.Id, out var t)) return 1;
                    var env = await engine.DefinitionAsync(t.path, t.line, t.col, t.id, default);
                    return Emit(env, oo.Format, SearchText);
                }),
                (HoverOptions o) => WithEngine(o, allowLoadFailure: false, async (engine, oo) =>
                {
                    if (!TryTarget(oo.Target, oo.Id, out var t)) return 1;
                    var env = await engine.HoverAsync(t.path, t.line, t.col, t.id, default);
                    return Emit(env, oo.Format, HoverText);
                }),
                (RefsOptions o) => WithEngine(o, allowLoadFailure: false, async (engine, oo) =>
                {
                    if (!TryTarget(oo.Target, oo.Id, out var t)) return 1;
                    var env = await engine.FindReferencesAsync(t.path, t.line, t.col, t.id, oo.IncludeDeclaration, oo.Limit, default);
                    return Emit(env, oo.Format, RefsText);
                }),
                (CallersOptions o) => WithEngine(o, allowLoadFailure: false, async (engine, oo) =>
                {
                    if (!TryTarget(oo.Target, oo.Id, out var t)) return 1;
                    var env = await engine.CallersAsync(t.path, t.line, t.col, t.id, oo.Depth, oo.Limit, default);
                    return Emit(env, oo.Format, CallersText);
                }),
                (ImplsOptions o) => WithEngine(o, allowLoadFailure: false, async (engine, oo) =>
                {
                    if (!TryTarget(oo.Target, oo.Id, out var t)) return 1;
                    var env = await engine.FindImplementationsAsync(t.path, t.line, t.col, t.id, oo.Of, oo.Limit, default);
                    return Emit(env, oo.Format, ImplsText);
                }),
                (HierarchyOptions o) => WithEngine(o, allowLoadFailure: false, async (engine, oo) =>
                {
                    if (!TryTarget(oo.Target, oo.Id, out var t)) return 1;
                    var env = await engine.TypeHierarchyAsync(t.path, t.line, t.col, t.id, oo.Direction, oo.Limit, default);
                    return Emit(env, oo.Format, HierarchyText);
                }),
                (DiagnosticsOptions o) => WithEngine(o, allowLoadFailure: false, async (engine, oo) =>
                {
                    var scope = oo.Scope ?? (oo.Path is not null ? "file" : "solution");
                    var env = await engine.DiagnosticsAsync(scope, oo.Path, oo.Project, oo.MinSeverity, oo.Limit, default);
                    return Emit(env, oo.Format, DiagnosticsText);
                }),
                // 0 for --help/--version, 1 for genuine parse/usage errors.
                errs => Task.FromResult(errs.All(IsHelpOrVersion) ? 0 : 1));

    static bool IsHelpOrVersion(Error e) =>
        e.Tag is ErrorType.HelpRequestedError or ErrorType.HelpVerbRequestedError or ErrorType.VersionRequestedError;

    /// <summary>
    /// Resolve the solution, bring MSBuild up in the right order, load the workspace,
    /// then run the command body. Keeps the bootstrap ordering in one place.
    /// </summary>
    static async Task<int> WithEngine<T>(T opts, bool allowLoadFailure, Func<Engine, T, Task<int>> body)
        where T : GlobalOptions
    {
        var solution = opts.Solution
            ?? Environment.GetEnvironmentVariable("KOIOS_SOLUTION")
            ?? Directory.GetCurrentDirectory();

        // Detect a repo-local .dotnet, then register MSBuild BEFORE any method that
        // touches MSBuildWorkspace is JIT-compiled.
        RepoSdk.Configure(solution);
        MSBuildBootstrap.Register();

        using var engine = new Engine();
        try
        {
            await engine.LoadAsync(solution);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: failed to load '{solution}': {ex.Message}");
            return 2;
        }

        // The solution couldn't be opened at all. `status` still reports the failure
        // (and its remedy); every other command needs a loaded solution.
        if (engine.LoadFailed && !allowLoadFailure)
        {
            if (opts.Format == "json")
                Console.WriteLine(JsonSerializer.Serialize(new Envelope<object>
                {
                    Ok = false,
                    State = "error",
                    Error = new ErrorInfo
                    {
                        Code = "solution_not_loaded",
                        Message = engine.FatalLoadError ?? "The solution failed to load.",
                        Hint = "Run `koios status` for details.",
                        Retryable = true,
                    },
                }, Json));
            else
                Console.Error.WriteLine($"error: solution not loaded: {engine.FatalLoadError ?? "unknown error"}");
            return 3;
        }

        try
        {
            return await body(engine, opts);
        }
        catch (Exception ex)
        {
            if (opts.Format == "json")
                Console.WriteLine(JsonSerializer.Serialize(new Envelope<object>
                {
                    Ok = false,
                    State = "error",
                    Error = new ErrorInfo { Code = "internal_error", Message = ex.Message, Retryable = false },
                }, Json));
            else
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
        var s = env.Items[0];
        Console.WriteLine($"state:      {s.State}{(env.Degraded ? " (degraded)" : "")}");
        Console.WriteLine($"solution:   {s.Solution.File}  [{s.Solution.Root}]");
        Console.WriteLine($"msbuild:    {s.MSBuild}");
        Console.WriteLine($"projects:   {s.Projects.Loaded}/{s.Projects.Total} loaded, {s.Projects.Failed} failed");
        Console.WriteLine($"documents:  {s.Documents}");
        Console.WriteLine($"symbols:    {s.Symbols}");
        Console.WriteLine($"load time:  {s.LoadMs} ms");
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
