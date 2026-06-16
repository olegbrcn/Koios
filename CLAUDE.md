# Koios — agent notes

Koios is a resident, read-only **semantic C# navigation engine** for AI agents,
built on Roslyn.

## Layout

- `src/Koios.Core/` — the engine library (transport-agnostic).
  - `MSBuildBootstrap.cs` — registers MSBuild via MSBuildLocator. MUST run before any
    Roslyn-workspace type is JIT-loaded. Degrades gracefully when global.json pins an
    uninstalled SDK.
  - `RepoSdk.cs` — auto-detects a repo-local `.dotnet/` (or user-level `~/.dotnet`)
    and points the SDK resolver at it. Called before bootstrap.
  - `Engine.cs` — loads the workspace, projects the catalog, answers queries.
  - `Catalog.cs` — in-memory symbol projection + indexes + ranked search.
  - `Display.cs` — custom SymbolDisplayFormats (FQN, signature, type signature).
  - `Models.cs` — the canonical response envelope + result DTOs (snake_case JSON).
- `src/Koios.Cli/` — thin CLI surface (assembly name `koios`). One-shot per invocation.
  Argument parsing uses the **CommandLineParser** package: one `[Verb]` options class
  per subcommand, shared flags on a `GlobalOptions` base, dispatched via `MapResult`.
  `--help`/`--version`/usage errors are auto-generated (and go to stderr, keeping
  stdout JSON-clean).

## Current state

Foundation & HOT-query tier and relational queries complete (see the README roadmap).
CLI commands: `status`, `search`, `outline`, `def`, `hover` (hot); `refs`, `callers`,
`impls` (with `--of <TypeArg>` for closed-generic filtering), `hierarchy`,
`diagnostics` (relational, via `SymbolFinder` / compiler diagnostics). No watcher,
SQLite, or MCP server yet.

Target resolution (`def`/`hover`/`refs`/`callers`/`impls`/`hierarchy`):
- A target is `file:line:col`, a `symbol_id` (doc-comment id), or a **bare name**.
  `NormalizeTargetId` (Engine) passes through known monikers and doc-id-shaped strings
  (`X:…`), else resolves the name via `Catalog.ByExactName` (case-sensitive preferred).
- Unique name → resolved; >1 match → `ambiguous_symbol` error via `Ambiguous<T>`,
  listing every candidate's `symbol_id` + loc so the caller re-runs with `--id`. Never
  silently pick a top-ranked match for relational verbs.
- `ResolveSymbolAsync` returns `Resolved(Symbol, Ambiguous)`; the CLI routes bare names
  into the `--id`/symbol_id slot, so no CLI parsing change was needed.

Relational notes:
- `LoadAsync` strips analyzer references after open — an `UnresolvedAnalyzerReference`
  crashes `SymbolFinder` operations that compute project checksums. We never run
  analyzers, and compiler diagnostics from `GetDiagnostics` are unaffected.
- `impls`/`hierarchy` derived results are filtered to source symbols (a metadata
  interface like `IDisposable` has thousands of BCL implementers); omitted counts
  are surfaced in `notes`.
- `impls --of <TypeArg>` post-filters to implementers of the closed generic
  `IFace<TypeArg>`. Matching is done via `OriginalDefinition` doc-comment id (stable
  across compilations) + first type argument `Name` (case-insensitive). A `notes`
  line reports the narrowing (e.g. `"filtered to ICloudEventHandler<VehicleRawDataReceivedEventMessage>: 79 → 32"`).
- `diagnostics` is only meaningful on a restored target; missing-reference errors
  (CS0006/0009/0012/0234/0246) flag the result `degraded` rather than presenting
  them as real.
- Each relational handler is wrapped (CLI `WithEngine`) so an unexpected Roslyn
  error returns `internal_error` instead of a raw stack trace.

## Conventions (keep these)

- Identity key is the Roslyn `DocumentationCommentId`, surfaced on the wire as `symbol_id`.
- Result array is always `items`; advisory array is always `notes`. Never `results`/`warnings`.
- Locations are `loc: { path (repo-relative), line (1-based), col (1-based), snippet }`.
- One Core, serialized identically by every surface (CLI now; MCP later).
- **Naming:** private instance fields are camelCase with **no underscore prefix**;
  const and static-readonly fields are PascalCase. Enforced by `.editorconfig`.

## Build & test

```bash
dotnet build
# Then point -s at a .sln/.slnx/.csproj. If the target's global.json pins an SDK
# that isn't installed, install it (or expose a repo-local/user .dotnet) — RepoSdk
# picks it up automatically.
```

## Known rough edges

- NuGet `NU1903` restore advisories are filtered out of load errors (they surface as
  workspace "failures" but don't block loading).
- `WorkspaceFailed` uses an obsolete API (CS0618 warning) — fine for now.
