# 0004. `/api/status` is separate from the `/api/health` liveness probe

**Status:** Proposed, 2026-11-11 (ROADMAP 5.5). Not built.

## Context

`GET /api/health` returns `{"status":"ok"}` and says only that the process is up. The status panel needs more: whether Ollama is reachable and has the model, which MCP servers are running or failed to start, and the state of the index. Failed MCP starts are only logged today.

## Decision

- Keep `/api/health` as a pure liveness probe. Add `GET /api/status` for dependency state ([API-CONTRACT.md](../API-CONTRACT.md) has the provisional body).
- `/api/status` returns `200` even when a dependency is unhealthy, and the body says which one. It includes a `ready` flag: Ollama reachable, model pulled, at least one tool server running.
- Checks are cheap and cached for a few seconds. Ollama is probed through its `/api/tags` with a short timeout, so the endpoint never hangs when Ollama is down.
- The response never includes the database path, the filesystem root, server command lines or tool arguments.
- `McpToolRegistry` will record failed starts so the panel can show them.

## Alternatives considered

- **Make `/api/health` check dependencies.** Liveness would then report the process as unhealthy whenever Ollama is stopped, which is a normal state for this app, and anything treating it as "the process is up" would misread it.
- **Return `503` when not ready.** Standard for orchestrators, but this is a local single-user app whose consumer is the dashboard, which needs the body in every case. A status code the client must special-case adds nothing.
- **A separate `/api/ready` plus a status endpoint.** Two endpoints for one set of checks. `ready` as a field covers it until a real consumer needs a separate path.

## Consequences

- It distinguishes "Ollama down" from "model not pulled", which the stream errors cannot (ADR 0002).
- The body shape is provisional until built; changing it later updates the contract and the dashboard together.
- The Ollama probe is one more outbound call per cache window.
