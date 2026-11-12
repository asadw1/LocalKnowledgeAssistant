# 0001. A run ID is the first SSE event and an explicit log argument

**Status:** Accepted, 2026-11-11 (ROADMAP 5.1). Built.

## Context

When a run fails, the dashboard shows an error and the details are only in the server log. Nothing tied the two together. The audit trail (ADR 0003) and structured logging (ROADMAP 6.4) also need a stable identifier for a run.

## Decision

- The runner generates the ID and emits `RunStartedEvent(RunId)` as the first event of every run. The ID is the first 12 characters of a random GUID in lowercase hex, which is short enough to read out in a bug report and ample for a single-user app.
- Every log line the runner writes for that run includes it as a message-template argument (`Run {RunId}: ...`). The dashboard shows it under the answer.
- Log lines written by other components (the MCP servers, the indexer) do not carry it.

## Alternatives considered

- **Response header (`X-Run-Id`) set by the endpoint.** Needs no new event type, but the runner would still need the ID for its logs, and the audit decorator (ADR 0003) works on the event stream, not on HTTP headers. The hook also does not read headers today.
- **A logger scope (`BeginScope`) instead of an argument.** This is the usual way to tag a request's log lines, but the runner is an async iterator. Context set inside an iterator is not reliably visible between its `yield`s, so a scope could silently drop the ID from some lines. An explicit argument cannot be lost, and a test can assert it with the plain capturing logger, which ignores scopes.
- **The ID on every event.** More bytes on every frame for no extra information.
- **A full GUID.** Harder to read aloud or compare by eye.

## Consequences

- A new event type: every test that asserted an exact event sequence gained a leading `RunStartedEvent`. The compatibility rules in [API-CONTRACT.md](../API-CONTRACT.md) section 7 apply to future clients.
- Correlation is partial by design. To follow one run through MCP or indexer logs, the ID would have to be passed into those components; this is deferred until 6.4 needs it.
- The ID is not a secret and not unique across restarts in any guaranteed way; it is a correlation aid, not an identity.
