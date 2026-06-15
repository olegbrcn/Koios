using System.Text.Json.Serialization;

namespace Koios.Core;

// Canonical response envelope, trimmed to what the first iteration needs.
public sealed class Envelope<T>
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("ok")] public bool Ok { get; init; } = true;
    [JsonPropertyName("tier")] public string Tier { get; init; } = "hot";
    [JsonPropertyName("source")] public string Source { get; init; } = "semantic";
    [JsonPropertyName("snapshot_id")] public string SnapshotId { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "ready";
    [JsonPropertyName("stale")] public bool Stale { get; init; }
    [JsonPropertyName("degraded")] public bool Degraded { get; init; }
    [JsonPropertyName("elapsed_ms")] public long ElapsedMs { get; init; }
    [JsonPropertyName("items")] public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    [JsonPropertyName("error")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErrorInfo? Error { get; init; }
    [JsonPropertyName("notes")] public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class ErrorInfo
{
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("hint")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }
}

public sealed class Loc
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("line")] public int Line { get; init; }
    [JsonPropertyName("col")] public int Col { get; init; }
    [JsonPropertyName("snippet")] public string Snippet { get; init; } = "";
}

public sealed class SymbolItem
{
    [JsonPropertyName("symbol_id")] public string? SymbolId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("container")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Container { get; init; }
    [JsonPropertyName("accessibility")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accessibility { get; init; }
    [JsonPropertyName("signature")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; init; }
    [JsonPropertyName("loc")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Loc? Loc { get; init; }
    [JsonPropertyName("score")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Score { get; init; }
    [JsonPropertyName("relation")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Relation { get; init; }
    [JsonPropertyName("is_metadata")] public bool IsMetadata { get; init; }
    [JsonPropertyName("generated")] public bool Generated { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "semantic";
}

// A single reference (read/write/call) to a target symbol.
public sealed class ReferenceItem
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "reference";
    [JsonPropertyName("loc")] public Loc Loc { get; init; } = new();
    [JsonPropertyName("source")] public string Source { get; init; } = "semantic";
}

// A node in the call hierarchy (a caller of, or callee of, the target).
public sealed class CallNode
{
    [JsonPropertyName("symbol_id")] public string? SymbolId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("container")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Container { get; init; }
    [JsonPropertyName("call_sites")] public IReadOnlyList<Loc> CallSites { get; init; } = Array.Empty<Loc>();
    [JsonPropertyName("depth")] public int Depth { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "semantic";
    [JsonPropertyName("children")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CallNode>? Children { get; init; }
}

// A node in a type hierarchy (a base, interface, or derived type of the target).
public sealed class HierarchyItem
{
    [JsonPropertyName("symbol_id")] public string? SymbolId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("relation")] public string Relation { get; init; } = "";
    [JsonPropertyName("depth")] public int Depth { get; init; }
    [JsonPropertyName("loc")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Loc? Loc { get; init; }
    [JsonPropertyName("is_metadata")] public bool IsMetadata { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "semantic";
}

public sealed class DiagnosticItem
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("severity")] public string Severity { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("loc")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Loc? Loc { get; init; }
    [JsonPropertyName("end")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EndPos? End { get; init; }
    [JsonPropertyName("category")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "semantic";
}

public sealed class EndPos
{
    [JsonPropertyName("line")] public int Line { get; init; }
    [JsonPropertyName("col")] public int Col { get; init; }
}

public sealed class HoverItem
{
    [JsonPropertyName("symbol_id")] public string? SymbolId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("signature")] public string Signature { get; init; } = "";
    [JsonPropertyName("return_type")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnType { get; init; }
    [JsonPropertyName("accessibility")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accessibility { get; init; }
    [JsonPropertyName("container")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Container { get; init; }
    [JsonPropertyName("doc_summary")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocSummary { get; init; }
    [JsonPropertyName("loc")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Loc? Loc { get; init; }
    [JsonPropertyName("is_metadata")] public bool IsMetadata { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "semantic";
}

public sealed class OutlineNode
{
    [JsonPropertyName("symbol_id")] public string? SymbolId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("signature")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; init; }
    [JsonPropertyName("accessibility")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accessibility { get; init; }
    [JsonPropertyName("loc")] public Loc? Loc { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "semantic";
    [JsonPropertyName("children")] public List<OutlineNode> Children { get; init; } = new();
}

public sealed class StatusInfo
{
    [JsonPropertyName("state")] public string State { get; init; } = "ready";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("roslyn")] public string Roslyn { get; init; } = "";
    [JsonPropertyName("msbuild")] public string MSBuild { get; init; } = "";
    [JsonPropertyName("solution")] public SolutionInfo Solution { get; init; } = new();
    [JsonPropertyName("snapshot_id")] public string SnapshotId { get; init; } = "";
    [JsonPropertyName("projects")] public ProjectCounts Projects { get; init; } = new();
    [JsonPropertyName("documents")] public int Documents { get; init; }
    [JsonPropertyName("symbols")] public int Symbols { get; init; }
    [JsonPropertyName("load_errors")] public IReadOnlyList<string> LoadErrors { get; init; } = Array.Empty<string>();
    [JsonPropertyName("load_ms")] public long LoadMs { get; init; }
}

public sealed class SolutionInfo
{
    [JsonPropertyName("root")] public string Root { get; init; } = "";
    [JsonPropertyName("file")] public string File { get; init; } = "";
}

public sealed class ProjectCounts
{
    [JsonPropertyName("total")] public int Total { get; init; }
    [JsonPropertyName("loaded")] public int Loaded { get; init; }
    [JsonPropertyName("failed")] public int Failed { get; init; }
}
