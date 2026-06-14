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
    [Value(0, MetaName = "target", HelpText = "file:line:col, or a symbol_id. Omit if using --id.")]
    public string? Target { get; set; }

    [Option("id", HelpText = "Target a symbol by its symbol_id (DocumentationCommentId).")]
    public string? Id { get; set; }
}

[Verb("hover", HelpText = "Signature, container, and XML-doc summary.")]
sealed class HoverOptions : GlobalOptions
{
    [Value(0, MetaName = "target", HelpText = "file:line:col, or a symbol_id. Omit if using --id.")]
    public string? Target { get; set; }

    [Option("id", HelpText = "Target a symbol by its symbol_id (DocumentationCommentId).")]
    public string? Id { get; set; }
}

static class Cli
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Task<int> RunAsync(string[] args) =>
        Parser.Default
            .ParseArguments<StatusOptions, SearchOptions, OutlineOptions, DefOptions, HoverOptions>(args)
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

        return await body(engine, opts);
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
        Console.Error.WriteLine("error: provide a target as file:line:col or --id <symbol_id>.");
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

    static void Error<T>(Envelope<T> env)
    {
        Console.WriteLine($"error: {env.Error?.Code}: {env.Error?.Message}");
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
