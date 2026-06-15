# Koios

A resident, read-only **semantic C# navigation engine** for AI coding agents,
built on Roslyn. Koios keeps a warm `MSBuildWorkspace` so an agent can ask
semantic questions — *where is this symbol defined, what's its signature, what's
in this file* — and get token-frugal `file:line` answers in milliseconds, instead
of paying Roslyn's cold-load cost per query or relying on the semantically-blind
output of grep.

## Current state

Koios has completed its [foundation and HOT-query tier](#foundation--hot-navigation)
and its [relational queries](#relational-queries): a warm Roslyn workspace that
projects a symbol catalog for name/position lookups and runs `SymbolFinder` /
compiler diagnostics for cross-symbol queries, exposed through a CLI.

| Command | What it does | Tier |
| --- | --- | --- |
| `koios status` | Load a solution/project and report health (projects, documents, symbols, load errors). | hot |
| `koios search <query>` | Ranked fuzzy symbol search (exact → prefix → camel-hump → substring). | hot |
| `koios outline <file.cs>` | Structural outline (types → members) of a file. | hot |
| `koios def <file>:<line>:<col>` | Go to definition (positional, via the live semantic model), or `def --id <symbol_id>`. | hot_semantic |
| `koios hover <file>:<line>:<col>` | Signature, container, XML-doc summary, or `hover --id <symbol_id>`. | hot_semantic |
| `koios refs <target>` | All references (read/write/call-classified) to a symbol. | relational |
| `koios callers <target>` | Incoming call hierarchy (`--depth N`). | relational |
| `koios impls <target>` | Source implementations / overrides / derived types. | relational |
| `koios hierarchy <target>` | Base/interface and derived-type hierarchy (`--direction`). | relational |
| `koios diagnostics [path]` | Compiler diagnostics for a file/project/solution (`--scope`, `--min-severity`). | relational |

A `<target>` is `file:line:col` or `--id <symbol_id>`. Every command emits the
canonical response envelope (`--format json`) or a human-readable view
(`--format text`, default). Symbols are keyed by their Roslyn
`DocumentationCommentId` (surfaced as `symbol_id`), so an agent can pass an id back
to re-target a symbol without re-resolving by position.

Relational queries need the target's references resolved — i.e. a restored
solution. On an unrestored project, symbols still extract but `diagnostics` flags
the result `degraded` (it would otherwise report missing-reference errors as if
they were real).

## Roadmap

Each step is a vertical slice that leaves the tool usable end-to-end.

- <a id="foundation--hot-navigation"></a>**Foundation & HOT navigation** (done)
  Boot `MSBuildWorkspace` via `MSBuildLocator`, hold one immutable `Solution`
  snapshot, and project an in-memory symbol catalog. Exposes `status`, `search`,
  `goto-definition`, `hover`, and `outline` (with the `hot` / `hot_semantic` split)
  through a CLI with JSON/text output and repo-local SDK auto-detection.
- <a id="relational-queries"></a>**Relational queries** (done)
  `refs`, `callers` (incoming call hierarchy), `impls`, `hierarchy`, and
  `diagnostics` via Roslyn `SymbolFinder` / compiler diagnostics — the capability
  that most decisively beats grep. (Outgoing callees and result memoization land
  with the resident host, where they pay off.)
- <a id="live-edits"></a>**Live edits**
  `FileSystemWatcher` with a debounced, incremental re-projection so edits are
  reflected within ~250 ms without a restart.
- <a id="persistence--warm-start"></a>**Persistence & warm start**
  Write-through SQLite mirror of the catalog for instant warm-start and an offline
  CLI fallback.
- <a id="agent-surface--packaging"></a>**Agent surface & packaging**
  An MCP stdio server exposing `koios_*` tools, packaged as a Claude Code skill +
  plugin so an agent uses Koios instead of grepping.

## Usage

Requires a .NET 10 SDK; `dotnet build` produces the CLI at
`src/Koios.Cli/bin/Debug/net10.0/koios.dll`.

```bash
koios() { dotnet src/Koios.Cli/bin/Debug/net10.0/koios.dll "$@"; }

koios status   -s path/to/My.sln
koios search   OrderService -s path/to/My.sln --kinds class,interface --limit 20
koios outline  src/Orders/OrderService.cs -s path/to/My.sln
koios def      src/Orders/OrderService.cs:52:25 -s path/to/My.sln
koios hover    --id "M:MyApp.Orders.OrderService.Submit(MyApp.Orders.Order)" -s path/to/My.sln --format json
```

`-s`/`--solution` accepts a `.sln`, `.slnx`, `.csproj`, or a directory (a single
solution/project is auto-discovered). It also defaults to `$KOIOS_SOLUTION` or the
current directory.

## SDK resolution

A solution's `global.json` may pin an SDK that isn't installed system-wide:

- **Repo-local SDK** — if a `.dotnet/` directory with an installed SDK exists at or
  above the target (including a user-level `~/.dotnet`), Koios uses it automatically,
  so no manual env wiring is needed.
- **Explicit pin** — set `KOIOS_MSBUILD_PATH` to an installed SDK directory.
- **Unsatisfiable pin** — if the pinned SDK can't be resolved at all, the load
  fails gracefully: `koios status` reports `state: error` with an actionable message
  (pinned vs installed versions and how to fix), and other commands return a
  structured `solution_not_loaded` error instead of a raw MSBuild stack trace.
