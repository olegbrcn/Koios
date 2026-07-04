# Koios

A resident, read-only **semantic C# navigation engine** for AI coding agents,
built on Roslyn. Koios keeps a warm `MSBuildWorkspace` so an agent can ask
semantic questions — *where is this symbol defined, what's its signature, what's
in this file* — and get token-frugal `file:line` answers in milliseconds, instead
of paying Roslyn's cold-load cost per query or relying on the semantically-blind
output of grep.

## Current state

Koios has completed its [foundation and HOT-query tier](#foundation--hot-navigation),
its [relational queries](#relational-queries), its [resident server](#resident-server),
[live edits](#live-edits), and its [agent surface](#agent-surface--packaging): a warm
Roslyn workspace that projects a symbol catalog for name/position lookups and runs
`SymbolFinder` / compiler diagnostics for cross-symbol queries — held resident, kept
current by a file watcher as you edit, and answered in milliseconds over two
surfaces: a per-solution local socket for the CLI (`koios serve`), and an MCP stdio
server (`koios-mcp`) exposing every verb as a `koios_*` tool for AI agents.

| Command | What it does | Tier |
| --- | --- | --- |
| `koios serve` | Load the solution once and stay **resident**, serving queries over a local socket and tracking on-disk edits live (foreground; Ctrl+C or `--idle-timeout` to stop; `--no-watch` for a fixed snapshot). | — |
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

The resident stays **current**: a file watcher applies on-disk edits to the warm
snapshot within ~300 ms (see [live edits](#live-edits)), so there is no need to
restart it as you work. `--no-watch` disables the watcher and serves a fixed
snapshot.

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
  diagnostics — the capability that most decisively beats grep. Results are
  memoized per `(verb, args, snapshot_id)` in a bounded LRU, so repeated agent
  queries answer in ~ms; the watcher's snapshot bump invalidates naturally.
- <a id="resident-server"></a>**Resident server** (done)
  `koios serve` holds the warm `Engine` and answers over a per-solution Unix domain
  socket; every verb is a thin client (`koios stop` / idle-timeout end it). A single
  shared dispatcher serves the socket today and the MCP host later. Auto-spawn of the
  resident on first query is the next increment.
- <a id="live-edits"></a>**Live edits** (done)
  A `FileSystemWatcher` in the resident keeps the snapshot current: debounced,
  coalesced batches of `.cs` edits are applied incrementally (new text / added /
  removed documents, only the touched files re-projected) as one atomic snapshot
  swap — queries always read a consistent snapshot, and every response carries
  its `snapshot_id` (`sln@N`). Changes to the project graph (`.csproj`, `.props`,
  `.targets`, `.sln`/`.slnx`, `global.json`, `nuget.config`), watcher overflow,
  or bulk storms (500+ files, deleted source directories) escalate to a full
  background reload that serves the previous snapshot until the swap — and keeps
  serving it (with the error in `status`) if the reload fails.
- <a id="agent-surface--packaging"></a>**Agent surface** (done)
  `src/Koios.Mcp` — an MCP stdio server (official `ModelContextProtocol` SDK)
  exposing one `koios_*` tool per verb over the same shared dispatcher, so MCP
  and CLI output are identical. The MCP host owns the Engine **in-process**: it
  is the long-lived resident an agent spawns — the workspace cold-loads in the
  background (the handshake answers immediately; `koios_status` reports
  `loading` meanwhile) and the file watcher keeps it current. Registered for
  this repo via `.mcp.json` + a skill (`.claude/skills/koios/SKILL.md`) that
  steers agents to `koios_*` over grep/Read. Marketplace-style plugin packaging
  ships with distribution.

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

### Agent usage (MCP)

Register the MCP server in any C# repo's `.mcp.json` (Claude Code picks it up and
spawns it with the repo as its working directory, so the solution is auto-discovered):

```json
{
  "mcpServers": {
    "koios": {
      "command": "dotnet",
      "args": ["/path/to/koios-mcp.dll"]
    }
  }
}
```

Pass `-s <solution>` in `args` (or set `KOIOS_SOLUTION` in `env`) when the repo
holds more than one solution. The server owns the warm engine in-process — the
workspace loads in the background after spawn (`koios_status` reports `loading`
until it is ready) and the file watcher keeps it current for the whole session.
A skill (`.claude/skills/koios/SKILL.md` in this repo) tells the agent when to
prefer `koios_*` tools over grep/Read.

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
