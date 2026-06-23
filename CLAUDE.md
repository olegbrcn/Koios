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
  - `Protocol.cs` — wire model (`Request`/`RequestArgs`, newline-delimited compact JSON)
    + `Dispatcher.DispatchAsync` — the single verb→Engine mapping shared by every
    transport (the socket today, the MCP host later).
  - `RuntimeDir.cs` — per-solution socket path (`{runtimeDir}/koios-{sha256(absPath)}.sock`).
  - `Server.cs` — resident host: binds a Unix domain socket, handles requests
    concurrently (the loaded `Solution` is immutable), per-request 2-min timeout,
    idle self-shutdown, `shutdown` control verb.
  - `SocketClient.cs` — thin client: `IsRunning`, `QueryAsync<T>`, `ShutdownAsync`.
- `src/Koios.Cli/` — thin CLI surface (assembly name `koios`). Query verbs are a
  socket round-trip to the resident; `serve` is the only path that loads an `Engine`.
  Argument parsing uses the **CommandLineParser** package: one `[Verb]` options class
  per subcommand, shared flags on a `GlobalOptions` base, dispatched via `MapResult`.
  `--help`/`--version`/usage errors are auto-generated (and go to stderr, keeping
  stdout JSON-clean).

## Current state

Foundation & HOT-query tier, relational queries, and the resident server complete
(see the README roadmap). CLI commands: `serve`, `stop` (lifecycle); `status`,
`search`, `outline`, `def`, `hover` (hot); `refs`, `callers`,
`impls` (with `--of <TypeArg>` for closed-generic filtering), `injectors`,
`hierarchy`, `diagnostics` (relational, via `SymbolFinder` / compiler diagnostics).
No watcher, SQLite, or MCP server yet.

Resident model:
- `koios serve` cold-loads the workspace once and holds the warm `Engine`; every query
  verb is a thin client (`Route<T>`/`RouteTarget<T>` in Program.cs) that round-trips a
  `Request` to the per-solution Unix socket and prints the returned `Envelope<T>`.
- **No in-process fallback** — a query verb with no resident returns a `no_resident`
  error (actionable hint to run `serve`). The only cold load lives in `ServeAsync`.
- Server and client serialize with `Protocol.Wire` (compact, one message per line);
  `Server` serializes the dispatch result by its runtime type so the boxed
  `Envelope<T>` emits its real shape. `status` is answered by the server (it stamps
  `ResidentInfo`: pid/uptime/requests); all other verbs go through `Dispatcher`.
- Fixed snapshot: on-disk edits are not reflected until `serve` is restarted (watcher
  is a later step). Idle-timeout (`--idle-timeout`, default 15 min) self-shuts down.

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
- `injectors` resolves a type, runs `FindReferencesAsync`, and keeps references whose
  syntax context is a constructor parameter's *type* (`ConstructorParameterContext`
  walks up to a `ConstructorDeclarationSyntax`, ignoring default values/attributes).
  The constructor's containing class is the injector; source-only, deduped by doc id.
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
