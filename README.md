# Koios

A resident, read-only **semantic C# navigation engine** for AI coding agents,
built on Roslyn. Koios keeps a warm `MSBuildWorkspace` so an agent can ask
semantic questions — *where is this symbol defined, what's its signature, what's
in this file* — and get token-frugal `file:line` answers in milliseconds, instead
of paying Roslyn's cold-load cost per query or relying on the semantically-blind
output of grep.

## Current state

Koios has completed its [foundation and HOT-query tier](#foundation--hot-navigation),
its [relational queries](#relational-queries), and its [resident server](#resident-server):
a warm Roslyn workspace that projects a symbol catalog for name/position lookups and runs
`SymbolFinder` / compiler diagnostics for cross-symbol queries — held resident by
`koios serve` and answered over a local socket, so each CLI query is a millisecond
round-trip instead of a cold workspace load.

| Command | What it does | Tier |
| --- | --- | --- |
| `koios serve` | Load the solution once and stay **resident**, serving queries over a local socket (foreground; Ctrl+C or `--idle-timeout` to stop). | — |
| `koios stop` | Stop the resident server for the solution. | — |
| `koios status` | Report health of the resident (projects, documents, symbols, uptime/requests). | hot |
| `koios search <query>` | Ranked fuzzy symbol search (exact → prefix → camel-hump → substring). | hot |
| `koios outline <file.cs>` | Structural outline (types → members) of a file. | hot |
| `koios def <target>` | Go to definition (by name, `file:line:col`, or `--id <symbol_id>`). | hot_semantic |
| `koios hover <target>` | Signature, container, XML-doc summary (by name, `file:line:col`, or `--id`). | hot_semantic |
| `koios refs <target>` | All references (read/write/call-classified) to a symbol. | relational |
| `koios callers <target>` | Incoming call hierarchy (`--depth N`). | relational |
| `koios impls <target> [--of <TypeArg>]` | Source implementations / overrides / derived types. `--of` filters to a closed generic (e.g. `impls IHandler --of OrderCreated` → implementers of `IHandler<OrderCreated>`). | relational |
| `koios injectors <target>` | Classes that declare the target type as a constructor parameter (DI injection sites). | relational |
| `koios deps <target>` | A type's constructor dependencies — what it injects (classic + primary ctors). Inverse of `injectors`. | relational |
| `koios callees <target>` | Outgoing first-party calls made by a method, or by every method of a type. Inverse of `callers`. | relational |
| `koios hierarchy <target>` | Base/interface and derived-type hierarchy (`--direction`). | relational |
| `koios diagnostics [path]` | Compiler diagnostics for a file/project/solution (`--scope`, `--min-severity`). | relational |

A `<target>` is `file:line:col`, a bare **symbol name**, or `--id <symbol_id>`.
A bare name is resolved against the catalog: a unique match is used directly; a name
that matches more than one declaration returns an `ambiguous_symbol` error listing
every candidate with its `symbol_id` and location, so you can re-run precisely via
`--id` or `file:line:col`. Every command emits the canonical response envelope
(`--format json`) or a human-readable view (`--format text`, default). Symbols are
keyed by their Roslyn `DocumentationCommentId` (surfaced as `symbol_id`), so an agent
can pass an id back to re-target a symbol without re-resolving by position.

Relational queries need the target's references resolved — i.e. a restored
solution. On an unrestored project, symbols still extract but `diagnostics` flags
the result `degraded` (it would otherwise report missing-reference errors as if
they were real).

### Resident server

Koios is meant to run **resident**. `koios serve` loads the workspace once (~tens of
seconds for a large solution) and holds the warm `Engine`; every other `koios <verb>`
is a thin client that connects to it over a per-solution Unix domain socket and
answers in milliseconds. There is **no in-process fallback** — a query verb with no
resident running fails fast with a `no_resident` error pointing you to `koios serve`.
The socket is keyed by the absolute solution path (one resident per solution); the
server self-shuts down after `--idle-timeout` minutes of inactivity, and `koios stop`
ends it immediately.

The resident serves a **fixed snapshot** — edits on disk are not picked up until you
restart it (incremental re-projection via a file watcher is a later step).

For an agent that fires many `koios` calls per session, start the resident once at the
start of the session (e.g. a Claude Code `SessionStart` hook or a devcontainer
`postStart`: `koios serve -s <solution> &`), and every subsequent query is warm.

## Roadmap

Each step is a vertical slice that leaves the tool usable end-to-end.

- <a id="foundation--hot-navigation"></a>**Foundation & HOT navigation** (done)
  Boot `MSBuildWorkspace` via `MSBuildLocator`, hold one immutable `Solution`
  snapshot, and project an in-memory symbol catalog. Exposes `status`, `search`,
  `goto-definition`, `hover`, and `outline` (with the `hot` / `hot_semantic` split)
  through a CLI with JSON/text output and repo-local SDK auto-detection.
- <a id="relational-queries"></a>**Relational queries** (done)
  `refs`, `callers`/`callees`, `impls` (with `--of`), `injectors`/`deps`,
  `hierarchy`, and `diagnostics` via Roslyn `SymbolFinder` / compiler
  diagnostics — the capability that most decisively beats grep. (Result
  memoization is a later increment now that the resident host exists.)
- <a id="resident-server"></a>**Resident server** (done)
  `koios serve` holds the warm `Engine` and answers over a per-solution Unix domain
  socket; every verb is a thin client (`koios stop` / idle-timeout end it). A single
  shared dispatcher serves the socket today and the MCP host later. Auto-spawn of the
  resident on first query is the next increment.
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

koios serve    -s path/to/My.sln &       # start the resident once (loads the workspace)
# … then every query below is a warm, millisecond round-trip to it:
koios status   -s path/to/My.sln
koios search   OrderService -s path/to/My.sln --kinds class,interface --limit 20
koios outline  src/Orders/OrderService.cs -s path/to/My.sln
koios def      src/Orders/OrderService.cs:52:25 -s path/to/My.sln
koios hover    --id "M:MyApp.Orders.OrderService.Submit(MyApp.Orders.Order)" -s path/to/My.sln --format json
koios impls    IOrderHandler -s path/to/My.sln          # by bare name (unique match)
koios impls    IHandler --of OrderCreated -s path/to/My.sln   # implementers of IHandler<OrderCreated>
koios callers  Submit -s path/to/My.sln                 # ambiguous → lists candidate symbol_ids
koios stop     -s path/to/My.sln                        # shut the resident down
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
- **Unsatisfiable pin** — if the pinned SDK can't be resolved at all, `koios serve`
  fails gracefully with an actionable message (pinned vs installed versions and how
  to fix) instead of a raw MSBuild stack trace; query verbs report `no_resident`
  until a resident is successfully started.
