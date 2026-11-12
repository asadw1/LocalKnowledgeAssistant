# Architecture decision records

Short records of design decisions that are not obvious from the code: what was decided, what else was considered, and what it costs. One file per decision, numbered in the order they were made. A decision that is later reversed gets a new record that supersedes the old one; the old one stays and its status changes.

**Status values:** *Accepted* (built, or in force), *Proposed* (agreed direction, not built), *Superseded by NNNN*.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-run-id-event-and-explicit-log-argument.md) | A run ID is the first SSE event and an explicit log argument | Accepted |
| [0002](0002-runner-converts-failures-to-error-events.md) | The runner turns unexpected failures into `ErrorEvent` and `DoneEvent` | Accepted |
| [0003](0003-audit-trail-in-a-separate-database.md) | The audit trail lives in a separate SQLite file, recorded by a runner decorator | Proposed |
| [0004](0004-status-endpoint-separate-from-health.md) | `/api/status` is separate from the `/api/health` liveness probe | Proposed |
| [0005](0005-console-logs-only-no-metrics-endpoint.md) | Logs stay on the console; no database log provider and no metrics endpoint | Accepted |

## Format

```
# NNNN. Title
Status, date
## Context
## Decision
## Alternatives considered
## Consequences
```

Keep each record to what a future reader needs to avoid re-deriving the argument. Link to the specs for detail instead of copying them.
