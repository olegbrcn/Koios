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
    Holds one volatile immutable `Snapshot(Solution, Catalog, Version)`; queries
    capture it once at entry. Single-writer mutation API for live edits
    (`ApplyCsBatchAsync`, `ReloadAsync`, `HasDocumentsUnder`).
  - `Catalog.cs` — in-memory symbol projection + indexes + ranked search.
  - `Watcher.cs` — `FileSystemWatcher` + debounce + batch classification; the
    engine's single writer (see live-edits notes below).
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
- `src/Koios.Mcp/` — MCP stdio server (assembly name `koios-mcp`, official
  `ModelContextProtocol` SDK). Owns the Engine **in-process** (it IS the resident
  an agent spawns; no socket round-trip). `EngineHost` cold-loads in the
  background so the JSON-RPC handshake answers immediately — tools await
  readiness, `koios_status` reports `loading`/`error` without blocking — then
  starts the `Watcher`. `KoiosTools` is one thin `[McpServerTool]` per verb over
  `Protocol.DispatchAsync`, serialized with `Protocol.Pretty` (identical to CLI
  `--format json`; golden-checked). stdout carries JSON-RPC only — ALL logging
  goes to stderr. Registered for this repo in `.mcp.json`; the skill in
  `.claude/skills/koios/SKILL.md` steers agents to `koios_*` over grep/Read.

## Current state

Foundation & HOT-query tier, relational queries, the resident server, live
edits, and the MCP agent surface complete (see the README roadmap). CLI
commands: `serve`, `stop` (lifecycle); `status`, `search`, `outline`, `def`,
`hover` (hot); `refs`, `callers`, `impls` (with `--of <TypeArg>` for
closed-generic filtering), `injectors`, `hierarchy`, `diagnostics` (relational,
via `SymbolFinder` / compiler diagnostics). Every verb is also a `koios_*` MCP
tool. No SQLite warm-start yet; target parsing and JSON shape are shared via
`Protocol.TargetArgs` / `Protocol.Pretty`.

Relational memoization:
- `Protocol.DispatchAsync` memoizes the relational verbs (refs/callers/callees/
  impls/injectors/deps/hierarchy/diagnostics) in `Engine.Cache` (`QueryCache`,
  LRU, 512 entries). Key = verb + wire-serialized args + `snapshot_id`, so a
  watcher swap invalidates without a flush; only `Ok` envelopes are cached
  (stored under the snapshot that actually answered). Hot verbs are ~ms already
  and are not cached. A cached envelope is returned as-is, `elapsed_ms`
  included — it reports the original compute time, not the hit.

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
- Idle-timeout (`--idle-timeout`, default 15 min) self-shuts down. `--no-watch`
  serves a fixed snapshot (also the fallback if the watcher can't start, e.g.
  inotify watch limits).

Live edits (watcher):
- `Watcher` (started by `serve`) coalesces fs events into one pending batch and
  applies it after a 250 ms quiet period (the debounce extends while events keep
  streaming, so a `git checkout` lands as ONE batch). `bin/`, `obj/`, `.git/`,
  `.vs/`, `.idea/`, `node_modules/` are ignored at the event source; a directory
  Changed event is dropped (child-mtime noise); a directory that *appears*
  (created/moved in) is scanned recursively because its children never raised
  events.
- Single-writer discipline: the watcher's apply loop is the only caller of the
  Engine mutation API. Queries capture the volatile `Snapshot` once per request,
  so in-flight queries stay consistent across a swap. Every swap bumps
  `snapshot_id` (`sln@N`).
- `.cs`-only batches go through `ApplyCsBatchAsync`: `WithDocumentText` /
  `AddDocument` (into the deepest project whose dir contains the file; multi-TFM
  → all of them) / `RemoveDocument`, then an incremental catalog rebuild — drop
  entries declared in touched files, re-project only the touched documents, and
  re-resolve partial types whose other parts live in unchanged files. One swap
  per batch (~tens of ms; ~1 s for a 300-file storm on a 57k-symbol solution).
- Full background reload instead (old snapshot keeps serving until the swap;
  on failure it KEEPS serving and the error lands in `status.load_errors`) when
  the batch contains: a project-graph file (`.csproj`/`.props`/`.targets`/
  `.sln`/`.slnx`/`.slnf`/`global.json`/`nuget.config` — `MSBuildWorkspace` has
  no clean single-project reload), a watcher overflow, a deleted directory that
  held loaded documents (inotify reports only the dir), or >500 changed `.cs`
  files.
- Blind spot (accepted): a bare `dotnet restore` with no project-file edit
  changes only `obj/` (ignored), so new package refs need a csproj touch or
  restart to be seen.

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
  line reports the narrowing (e.g. `"filtered to IHandler<OrderCreated>: 79 → 32"`).
- `injectors` resolves a type, runs `FindReferencesAsync`, and keeps references whose
  syntax context is a constructor parameter's *type* (`ConstructorInjectorType` walks up
  to the injecting `TypeDeclarationSyntax`, ignoring default values/attributes). Handles
  **both** classic constructors (`ConstructorDeclarationSyntax`) and **primary
  constructors** (parameter list on the type declaration). Source-only, deduped by doc id.
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
