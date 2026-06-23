using System.Text.Json;
using System.Text.Json.Serialization;

namespace Koios.Core;

// Wire protocol for the resident server. One request per line, one response per
// line (newline-delimited compact JSON), so a StreamReader buffers a whole message
// without any length-prefix framing.

public sealed class Request
{
    [JsonPropertyName("verb")] public string Verb { get; init; } = "";
    [JsonPropertyName("args")] public RequestArgs Args { get; init; } = new();
}

// Superset of every verb's parameters. Each verb reads only the fields it needs.
// A record so the CLI can build a base (target) and layer verb-specific fields via `with`.
public sealed record RequestArgs
{
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("line")] public int? Line { get; init; }
    [JsonPropertyName("col")] public int? Col { get; init; }
    [JsonPropertyName("symbol_id")] public string? SymbolId { get; init; }
    [JsonPropertyName("query")] public string? Query { get; init; }
    [JsonPropertyName("kinds")] public string[]? Kinds { get; init; }
    [JsonPropertyName("limit")] public int Limit { get; init; } = 50;
    [JsonPropertyName("include_declaration")] public bool IncludeDeclaration { get; init; }
    [JsonPropertyName("depth")] public int Depth { get; init; } = 1;
    [JsonPropertyName("of")] public string? Of { get; init; }
    [JsonPropertyName("direction")] public string Direction { get; init; } = "both";
    [JsonPropertyName("scope")] public string? Scope { get; init; }
    [JsonPropertyName("project")] public string? Project { get; init; }
    [JsonPropertyName("min_severity")] public string MinSeverity { get; init; } = "warning";
    [JsonPropertyName("file")] public string? File { get; init; }
}

public static class Protocol
{
    public const string ShutdownVerb = "shutdown";

    // Compact (single-line) — required for newline framing on the wire.
    public static readonly JsonSerializerOptions Wire = new() { WriteIndented = false };

    // Single mapping from verb to Engine call, shared by every transport. Returns the
    // boxed Envelope<T> (serialize via its runtime type). `status` is handled by the
    // server (it injects ResidentInfo) and so is not reached here in practice.
    public static async Task<object> DispatchAsync(Engine engine, Request req, CancellationToken ct)
    {
        var a = req.Args;
        return req.Verb switch
        {
            "status" => engine.Status(),
            "search" => engine.Search(a.Query ?? "", a.Kinds is { } k ? new HashSet<string>(k) : null, a.Limit),
            "outline" => engine.Outline(a.File ?? a.Path ?? ""),
            "def" => await engine.DefinitionAsync(a.Path, a.Line, a.Col, a.SymbolId, ct),
            "hover" => await engine.HoverAsync(a.Path, a.Line, a.Col, a.SymbolId, ct),
            "refs" => await engine.FindReferencesAsync(a.Path, a.Line, a.Col, a.SymbolId, a.IncludeDeclaration, a.Limit, ct),
            "callers" => await engine.CallersAsync(a.Path, a.Line, a.Col, a.SymbolId, a.Depth, a.Limit, ct),
            "impls" => await engine.FindImplementationsAsync(a.Path, a.Line, a.Col, a.SymbolId, a.Of, a.Limit, ct),
            "injectors" => await engine.FindInjectorsAsync(a.Path, a.Line, a.Col, a.SymbolId, a.Limit, ct),
            "hierarchy" => await engine.TypeHierarchyAsync(a.Path, a.Line, a.Col, a.SymbolId, a.Direction, a.Limit, ct),
            "diagnostics" => await engine.DiagnosticsAsync(a.Scope ?? "solution", a.Path, a.Project, a.MinSeverity, a.Limit, ct),
            _ => new Envelope<object>
            {
                Ok = false,
                Error = new ErrorInfo { Code = "unknown_verb", Message = $"Unknown verb: {req.Verb}", Retryable = false },
            },
        };
    }
}
