---
name: koios
description: Semantic C# code navigation via the koios_* MCP tools — finding symbols, definitions, references, callers/callees, implementations, DI wiring, type hierarchies, and compiler diagnostics. Use whenever exploring or answering questions about C# code structure, instead of grep/Read scanning.
---

# Koios — semantic C# navigation

This project exposes `koios_*` MCP tools backed by a warm Roslyn workspace that
tracks on-disk edits live. They answer *semantic* questions in milliseconds with
token-frugal `file:line` results — always prefer them over grep/find or reading
whole files when the question is about C# code.

## Tool selection

| Question | Tool (not this) |
| --- | --- |
| Where is symbol X? | `koios_search` (not grep) |
| What's in this file? | `koios_outline` (not Read) |
| Where is X defined / what is its signature? | `koios_def` / `koios_hover` |
| Who uses X? | `koios_refs` — real references only, classified read/write/call (grep finds strings, comments, false names) |
| Who calls this method? | `koios_callers` (`depth` for transitive) |
| What does this method call? | `koios_callees` |
| Who implements/derives this? | `koios_impls` (`of` narrows a generic: target `IHandler`, of `OrderCreated` → implementers of `IHandler<OrderCreated>`) |
| Where is this type injected (DI)? | `koios_injectors`; its own dependencies: `koios_deps` |
| Base/derived types? | `koios_hierarchy` |
| Does the code compile? | `koios_diagnostics` (no build needed) |

## Targets

A `target` is a bare name (`OrderService`), a position (`src/Foo.cs:52:17`), or
a `symbol_id` from any previous result. If a bare name is ambiguous, the error
lists every candidate's `symbol_id` — re-run with the one you meant. Reuse
`symbol_id`s across calls; they are stable.

## Notes

- Results are JSON envelopes: `items` is the data, `notes` carries advisories
  (truncation, filtered counts), `snapshot_id` identifies the workspace state.
- The engine tracks file edits within ~300 ms; no restart needed after editing.
- `koios_status` shows engine health; while it reports `loading`, other tools
  wait for the cold load to finish (tens of seconds on large solutions).
- Falling back to grep/Read is fine for non-C# files, comments/strings, or when
  a koios tool returns an error you can't resolve.
