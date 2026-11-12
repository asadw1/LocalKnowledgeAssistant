# 0003. The audit trail lives in a separate SQLite file, recorded by a runner decorator

**Status:** Proposed, 2026-11-11 (ROADMAP 5.6). Not built.

## Context

The dashboard should offer a grid of past runs: when, what was asked, how it ended, which tools were called. That needs run history to be stored. Today conversations are not persisted. The metadata index database has strict rules: the indexer is its only writer, and the SQLite MCP server opens it read-only so the model can query it.

## Decision

- Run history is stored in its own SQLite file (for example `data/audit.db`), git-ignored, written only by the API.
- Recording is done by a decorator around `IAgentRunner` that passes events through and writes them. The runner itself stays free of I/O, as the SRP rule in CLAUDE.md requires.
- No MCP server is ever pointed at this file, so the model cannot query its own audit trail, and the read-only guarantees on the index database are untouched.
- One row per run (ID from ADR 0001, times, status, prompt, tool-call count, final answer) and one row per event (type, tool, `IsError`, duration, truncated payload). Token streams are not stored; the final answer is.
- A row or age cap and a clear-history action bound the size. A failed audit write is logged and never fails the chat run.
- Read access is `GET /api/runs` and `GET /api/runs/{id}` ([API-CONTRACT.md](../API-CONTRACT.md)).

## Alternatives considered

- **Reuse the index database.** It would break the single-writer rule, and the SQLite MCP server could expose the audit data to the model.
- **A database logging provider.** It ties logs to the audit view, floods the table with Debug and Information lines, and still leaves the need for structured run and event rows. See ADR 0005.
- **A JSON-lines file.** Simple to write, but paging, filtering and per-run lookup would be re-implemented by hand.
- **No persistence.** The dashboard grid and the evaluation work (ROADMAP 6.1, 6.2) would have nothing durable to read from.

## Consequences

- Prompts, SQL and tool results are stored in plain text and may contain personal data. [SECURITY.md](../SECURITY.md) section 5 notes this and must be updated when the feature is built, since its statement that conversations are not persisted stops being true.
- New tests are needed for concurrent runs, write failure, retention, and temp databases (`SqliteConnection.ClearAllPools()`).
- It depends on the run ID (ADR 0001) and on the event stream staying the single source of truth for what a run did.
