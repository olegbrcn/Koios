using System.ComponentModel;
using System.Text.Json;
using Koios.Core;
using ModelContextProtocol.Server;

namespace Koios.Mcp;

/// <summary>
/// One MCP tool per Koios verb. Each tool is a thin wrapper: build a Request,
/// run it through the shared dispatcher, and serialize the envelope with the
/// same options as the CLI's --format json — one Core, identical output on
/// every surface. Results are token-frugal JSON with repo-relative
/// file:line locations and stable symbol_ids.
/// </summary>
[McpServerToolType]
public sealed class KoiosTools
{
    private const string TargetHelp =
        "Symbol to target: a bare name (e.g. 'OrderService'), a file:line:col position, " +
        "or a symbol_id from a previous result. An ambiguous name returns every candidate's " +
        "symbol_id so you can re-run precisely.";

    private readonly EngineHost host;

    public KoiosTools(EngineHost host) => this.host = host;

    private async Task<string> RunAsync(string verb, RequestArgs args, CancellationToken ct)
    {
        var engine = await host.ReadyAsync(ct);
        var result = await Protocol.DispatchAsync(engine, new Request { Verb = verb, Args = args }, ct);
        return JsonSerializer.Serialize(result, result.GetType(), Protocol.Pretty);
    }

    private Task<string> RunTargetAsync(string verb, string target, Func<RequestArgs, RequestArgs> extra, CancellationToken ct) =>
        RunAsync(verb, extra(Protocol.TargetArgs(target)), ct);

    [McpServerTool(Name = "koios_status")]
    [Description("Health of the Koios engine: loaded projects/documents/symbols, snapshot_id, load errors. Answers even while the workspace is still loading.")]
    public string Status() =>
        JsonSerializer.Serialize(host.Status(), Protocol.Pretty);

    [McpServerTool(Name = "koios_search")]
    [Description("Ranked fuzzy symbol search across the whole solution (exact > prefix > camel-hump > substring). Use this instead of grep to find a type/member by name; results carry symbol_id + file:line.")]
    public Task<string> Search(
        [Description("Name, substring, or camel-hump pattern (e.g. 'OS' matches 'OrderService').")] string query,
        [Description("Comma-separated kind filter, e.g. 'class,interface,method'.")] string? kinds = null,
        [Description("Max results.")] int limit = 50,
        CancellationToken ct = default) =>
        RunAsync("search", new RequestArgs
        {
            Query = query,
            Kinds = string.IsNullOrWhiteSpace(kinds)
                ? null
                : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Limit = limit,
        }, ct);

    [McpServerTool(Name = "koios_outline")]
    [Description("Structural outline of a .cs file: its types and members with signatures and line numbers. Use this instead of reading a whole file to understand its shape.")]
    public Task<string> Outline(
        [Description("Path to a .cs file (repo-relative or absolute).")] string file,
        CancellationToken ct = default) =>
        RunAsync("outline", new RequestArgs { File = file }, ct);

    [McpServerTool(Name = "koios_def")]
    [Description("Go to definition: resolve a symbol to its declaration site(s) with signature and file:line.")]
    public Task<string> Def(
        [Description(TargetHelp)] string target,
        CancellationToken ct = default) =>
        RunTargetAsync("def", target, a => a, ct);

    [McpServerTool(Name = "koios_hover")]
    [Description("Signature, containing type, return type, and XML-doc summary of a symbol.")]
    public Task<string> Hover(
        [Description(TargetHelp)] string target,
        CancellationToken ct = default) =>
        RunTargetAsync("hover", target, a => a, ct);

    [McpServerTool(Name = "koios_refs")]
    [Description("Find all references to a symbol, classified as read/write/call, with file:line and a snippet. Semantic — finds only real references, not textual matches.")]
    public Task<string> Refs(
        [Description(TargetHelp)] string target,
        [Description("Include the declaration itself.")] bool includeDeclaration = false,
        [Description("Max references.")] int limit = 100,
        CancellationToken ct = default) =>
        RunTargetAsync("refs", target, a => a with { IncludeDeclaration = includeDeclaration, Limit = limit }, ct);

    [McpServerTool(Name = "koios_callers")]
    [Description("Incoming call hierarchy: who calls the target method (optionally transitive via depth). The semantic answer to 'where is this invoked from'.")]
    public Task<string> Callers(
        [Description(TargetHelp)] string target,
        [Description("Levels of callers to expand.")] int depth = 1,
        [Description("Max top-level callers.")] int limit = 50,
        CancellationToken ct = default) =>
        RunTargetAsync("callers", target, a => a with { Depth = depth, Limit = limit }, ct);

    [McpServerTool(Name = "koios_callees")]
    [Description("Outgoing first-party calls made by a method (or by every method of a type): invocations, constructor calls, property/indexer accesses. Inverse of koios_callers.")]
    public Task<string> Callees(
        [Description(TargetHelp)] string target,
        [Description("Max results.")] int limit = 100,
        CancellationToken ct = default) =>
        RunTargetAsync("callees", target, a => a with { Limit = limit }, ct);

    [McpServerTool(Name = "koios_impls")]
    [Description("Implementations of an interface/abstract member, overrides, or derived types. Optionally narrow a generic interface to one closed type argument.")]
    public Task<string> Impls(
        [Description(TargetHelp)] string target,
        [Description("Filter to implementers of the closed generic whose first type argument has this name (e.g. target 'IHandler' + of 'OrderCreated' → implementers of IHandler<OrderCreated>).")] string? of = null,
        [Description("Max results.")] int limit = 100,
        CancellationToken ct = default) =>
        RunTargetAsync("impls", target, a => a with { Of = of, Limit = limit }, ct);

    [McpServerTool(Name = "koios_injectors")]
    [Description("Classes that take the target type as a constructor parameter (classic or primary constructor) — its DI injection sites.")]
    public Task<string> Injectors(
        [Description(TargetHelp)] string target,
        [Description("Max results.")] int limit = 100,
        CancellationToken ct = default) =>
        RunTargetAsync("injectors", target, a => a with { Limit = limit }, ct);

    [McpServerTool(Name = "koios_deps")]
    [Description("A type's constructor dependencies — what it injects. Inverse of koios_injectors.")]
    public Task<string> Deps(
        [Description(TargetHelp)] string target,
        [Description("Max results.")] int limit = 100,
        CancellationToken ct = default) =>
        RunTargetAsync("deps", target, a => a with { Limit = limit }, ct);

    [McpServerTool(Name = "koios_hierarchy")]
    [Description("Type hierarchy of a class/interface: base types and interfaces upward, derived/implementing types downward.")]
    public Task<string> Hierarchy(
        [Description(TargetHelp)] string target,
        [Description("both | base | derived.")] string direction = "both",
        [Description("Max results.")] int limit = 100,
        CancellationToken ct = default) =>
        RunTargetAsync("hierarchy", target, a => a with { Direction = direction, Limit = limit }, ct);

    [McpServerTool(Name = "koios_diagnostics")]
    [Description("Compiler diagnostics (errors/warnings) for a file, a project, or the whole solution — without running a build.")]
    public Task<string> Diagnostics(
        [Description("File to scope to (implies scope 'file').")] string? path = null,
        [Description("file | project | solution (default: file if a path is given, else solution).")] string? scope = null,
        [Description("Project name for scope 'project'.")] string? project = null,
        [Description("hidden | info | warning | error.")] string minSeverity = "warning",
        [Description("Max diagnostics.")] int limit = 100,
        CancellationToken ct = default) =>
        RunAsync("diagnostics", new RequestArgs
        {
            Scope = scope ?? (path is not null ? "file" : "solution"),
            Path = path,
            Project = project,
            MinSeverity = minSeverity,
            Limit = limit,
        }, ct);
}
