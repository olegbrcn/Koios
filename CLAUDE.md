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

Foundation & HOT-query tier complete (see the README roadmap). CLI commands: `status`,
`search`, `outline`, `def`, `hover`. No watcher, relational queries, SQLite, or MCP
server yet — those are the remaining roadmap steps (live edits, relational queries,
persistence, agent surface).

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
