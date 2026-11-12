# 0005. Logs stay on the console; no database log provider and no metrics endpoint

**Status:** Accepted, 2026-11-11 (ROADMAP 6.4). In force; nothing to build.

## Context

The question was whether observability needs a logger that writes to a database, a log file sink, or a metrics endpoint. Today the API uses default ASP.NET Core logging (console and debug), and the SQLite MCP child logs to stderr only, because stdout carries the protocol. All logging uses `ILogger<T>` message templates. There is no sink package in any project.

## Decision

- Logs stay on the console through `ILogger<T>`. Phase 6.4 adds structured fields (tool calls, validation failures, iterations, latency) and the run ID from ADR 0001, not a new destination.
- No database logging provider. Run history for the UI is the audit trail of ADR 0003, a separate concern with its own tables.
- No `/metrics` endpoint. For a single local process, logs are enough. If counters are wanted later, emit `System.Diagnostics.Metrics` instruments rather than adding an endpoint first.
- A rolling file sink (for example Serilog) is deferred. It would add a package, which CLAUDE.md says to ask about first.

## Alternatives considered

- **Database sink for logs.** It couples logs to storage and schema, and the volume is mostly noise. The part worth keeping (what each run did) is in the audit trail.
- **Rolling file via Serilog.** Useful if logs must survive restarts. Not needed yet; revisit if the console output is lost often enough to matter.
- **Prometheus-style metrics.** Designed for fleets and scrapers. Nothing here scrapes it.

## Consequences

- Logs do not survive a restart. Anything that must be kept goes in the audit trail.
- The call sites need no change when a sink is added later, since they already use message templates.
