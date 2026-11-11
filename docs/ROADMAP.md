# Roadmap

Delivery plan for the Local Knowledge & File Assistant. Each phase ends in something runnable; each stage is a small, reviewable unit of work. Design details live in [TAD.md](TAD.md).

**Status key:** Done, In progress, Not started

| Phase | Goal | Status |
|---|---|---|
| 0 | Scaffold and documentation | Done |
| 1 | Streaming chat, no tools | Done |
| 2 | MCP filesystem tools | Done |
| 3 | ReAct loop with validation | Done |
| 4 | SQLite metadata and indexing | Done |
| 5 | Dashboard UI | In progress |
| 6 | Hardening and evaluation | Not started |

---

## Phase 0: Scaffold and documentation (done)

**Goal:** A building solution and frontend, with the architecture written down.

- **0.1 Solution scaffold:** `Assistant.Core`, `Assistant.Api`, `Assistant.Tests` on `net10.0`; Semantic Kernel, Ollama connector and MCP SDK referenced.
- **0.2 Frontend scaffold:** Vite, React, TypeScript, Tailwind v4, `/api` dev proxy, `useAgentStream` hook.
- **0.3 Contracts:** `IAgentRunner`, `AgentEvent` types, `IToolRegistry`, `McpServerOptions`, `/api/chat` SSE endpoint shape.
- **0.4 Docs:** README, TAD, this roadmap.

**Exit criteria:** `dotnet build` and `npm run build` succeed.

---

## Phase 1: Streaming chat, no tools (done)

**Goal:** Type a prompt in the browser and watch tokens stream from the local model.

- **1.1 Local model setup:** Start Ollama, pull `qwen2.5:7b-instruct`, confirm with `ollama run`.
- **1.2 Configuration:** `Ollama` options section (base URL, model id) bound and validated at startup.
- **1.3 Agent runner:** `SemanticKernelAgentRunner` streaming chat completions as `TokenEvent`, finishing with `DoneEvent`; cancellation flows through.
- **1.4 DI composition:** Register kernel, chat service and runner in `Program.cs`; endpoint resolves only `IAgentRunner`.
- **1.5 Minimal chat UI:** Prompt box, streamed response, running/disabled state using `useAgentStream`.
- **1.6 Tests:** Runner against a fake chat completion service (token order, cancellation, error propagation); SSE frame test for the endpoint.

**Exit criteria:** End-to-end streaming works in the browser; stopping the request stops generation; tests pass.

---

## Phase 2: MCP filesystem tools (done)

**Goal:** The app can launch a local MCP server and expose its tools to the kernel.

- **2.1 MCP client service:** Hosted service that launches stdio servers from `McpServerOptions`, owns their lifetime, disposes on shutdown.
- **2.2 Tool discovery:** List tools from each server and capture name, description and JSON Schema.
- **2.3 Kernel adapter:** `McpToolRegistry : IToolRegistry` exposes MCP tools as Semantic Kernel functions.
- **2.4 Filesystem server:** Configure the filesystem MCP server restricted to an allow-listed root; add a sample documents folder for testing.
- **2.5 Failure handling:** Server fails to start, crashes mid-run, or times out; the app stays up and reports the tool as unavailable.
- **2.6 Tests:** Registry against a stub MCP server; allow-list enforcement; one opt-in integration test with the real server.

**Exit criteria:** Startup logs list discovered filesystem tools; calling one directly through the registry returns real file data.

---

## Phase 3: ReAct loop with validation (done)

**Goal:** The model reasons, calls tools, observes results and answers, safely.

- **3.1 Manual function-calling loop:** Disable auto-invoke; iterate completion, tool request, invocation, result, repeat.
- **3.2 Event streaming:** Emit `ToolCallEvent` and `ToolResultEvent` as they happen, interleaved with tokens.
- **3.3 Validation pipeline:** Unknown tool name, invalid JSON, JSON Schema violation, oversize output (see TAD section 6).
- **3.4 Error feedback:** Return validation failures to the model as `IsError` tool results so it can self-correct.
- **3.5 Loop guards:** Iteration cap, per-run timeout, repeated-identical-call detection.
- **3.6 System prompt:** Define the assistant's role, tool-use rules and answer format; keep it in a versioned file.
- **3.7 Tests:** Scripted fake model producing valid, malformed, looping and never-ending behaviour; assert events and termination.

**Exit criteria:** A question like "summarise the notes in my docs folder" produces tool calls and a correct answer; malformed calls never crash the run.

---

## Phase 4: SQLite metadata and indexing (done)

**Goal:** Answer questions about file metadata and find documents without reading everything.

- **4.1 Schema design (done):** Tables for files (path, size, modified, hash), tags and summaries; migration script under `data/`.
- **4.2 Indexer (done):** Background job that scans the allow-listed root and upserts metadata; incremental via hash and mtime.
- **4.3 SQLite MCP server (done):** Our own `Assistant.SqliteMcp`, because the npm servers surveyed did not guarantee read-only (model-controlled flag or database path, or a broken `npx` entry point). Two tools, `describe_schema` and `query`; database opened read-only with `query_only`, single `SELECT`/`WITH` only, row cap, 5 second timeout.
- **4.4 Tool selection (done):** System prompt v2 routed metadata questions to SQLite, content questions to the filesystem, and combined ones query-then-read; it summarises the `files` table and gives a date idiom that works with the indexer's timestamps. Overlapping filesystem tools were withheld, leaving seven tools (six after 4.5). Drift tests tie the prompt to the schema and tool config.
- **4.5 Combined scenarios (done):** Queries needing both, such as "which large files changed this week, and what do they contain?". Run against the real model: with the multi-file read exposed, 5 of 6 runs hit the iteration cap because the model passed `paths` as a string. `read_multiple_files` is now withheld and prompt v3 reads one file per call; the same prompts then all finished. Remaining model limits (empty first arguments, announcing a step without calling the tool) are in PROMPT-SPEC section 7.
- **4.6 Tests (done):** Indexer on a temp directory; read-only enforcement; `CombinedScenarioTests` drive a scripted model over a real index and the real SQLite server (query, then read the returned `path` unchanged; rejected query then correction; write attempts fail and the index is unchanged).

**Exit criteria:** Metadata questions are answered from SQLite; content questions use the filesystem; the database cannot be modified through the agent.

---

## Phase 5: Dashboard UI

**Goal:** A usable interface that makes the agent's reasoning visible.

Stages are listed in build order. Backend gaps that the UI relies on (5.1) come before the stages that build on them.

**Already in place (from Phases 0-1):** `useAgentStream` (fetch POST stream, typed `AgentEvent`s, abort), `parseSseFrames`, and a minimal `App` (prompt box, streamed answer text, Send/Stop, empty hint, HTTP/network error alert). Vitest coverage is at 100%.

- **5.1 Error handling, empty states and run ID (done):** Backend down, model missing, stream interrupted. The runner turns an unexpected exception (Ollama down, model not pulled, HTTP timeout) into `ErrorEvent` then `DoneEvent`, and the first event of every run is `RunStartedEvent(RunId)`, repeated as `Run <id>:` in the runner's log lines (checked live against a stopped Ollama and a missing model). The dashboard shows each `ErrorEvent` as an alert, shows the run id under the answer, and reports a stream that ends without `DoneEvent`. Limits: only the runner's own log lines carry the id (not MCP servers or the indexer), and a dropped connection outside the runner still ends the stream without `DoneEvent`. No structured error codes yet, so the UI cannot tell "Ollama down" from "model missing"; the status panel (5.5) is where that distinction will live.
- **5.2 Tool activity cards (not started):** Collapsible cards for each call (arguments, result, error state, duration). `ToolCallEvent` and `ToolResultEvent` are collected by the hook but not rendered.
- **5.3 Conversation view (partial):** Message list with markdown rendering and auto-scroll. Today `App` shows one plain-text answer built from `TokenEvent`s; there is no message list, markdown or auto-scroll.
- **5.4 Run controls (partial):** Cancel in-flight run (Stop is done), retry, clear conversation (not started).
- **5.5 Status panel (not started):** Ollama reachability, model name, connected MCP servers and their tools. Needs a backend `GET /api/status` first (see [API-CONTRACT.md](API-CONTRACT.md) section 2): Ollama reachable and model pulled (via Ollama `/api/tags`, short timeout), MCP servers with their tools and any that failed to start, and index file count, last scan time and whether a scan is running. `McpToolRegistry` only logs failed starts today, so it must record them. The response never includes the database path, the filesystem root, command lines or tool arguments. Readiness (Ollama reachable and at least one tool server up) can be derived from the same cached checks, with `/api/health` staying as liveness.
- **5.6 Run history and audit grid (not started):** A slide-over panel with a grid of past runs (time, prompt, status, tool-call count, duration); expanding a row shows that run's tool calls, reusing the 5.2 cards. Depends on the run ID from 5.1. Design: an `IAgentRunner` decorator records events (the runner itself stays free of I/O) into a **separate** SQLite file (for example `data/audit.db`) written only by the API; no MCP server may be pointed at it, so the model can never query it and the read-only guarantees on the index database are untouched. Store one row per run and one per event (type, tool, `IsError`, duration, truncated payload), the final answer rather than the token stream, with a row or age cap and a clear-history action. A failed audit write is logged and never fails the chat run. Endpoints `GET /api/runs` and `GET /api/runs/{id}` (see [API-CONTRACT.md](API-CONTRACT.md)). Update [SECURITY.md](SECURITY.md) section 5, since conversations become persisted. Tests: concurrent runs, write failure, retention, `SqliteConnection.ClearAllPools()` on temp databases. Logs stay on the console (6.4); there is no database logging provider.
- **5.7 Frontend tests (partial):** SSE parser edge cases (split frames, multiple frames per chunk) are covered; component tests for tool cards wait on 5.2.

**Exit criteria:** A full session is understandable from the UI alone, including failures.

---

## Phase 6: Hardening and evaluation

**Goal:** Know how well it works and keep it safe.

- **6.1 Evaluation set:** A fixed list of prompts with expected tool use and answers over a sample corpus.
- **6.2 Model comparison:** Run the set on `qwen2.5:7b-instruct` and `llama3.2:3b`; record tool-call validity rate and answer quality.
- **6.3 Prompt-injection checks:** Documents containing hostile instructions; verify tools stay read-only and the allow-list holds.
- **6.4 Observability:** Structured logs for tool calls, validation failures, iterations and latency, each carrying the run ID from 5.1. No `/metrics` endpoint: for a single local process, logs are enough (revisit with `System.Diagnostics.Metrics` counters if needed).
- **6.5 Configuration and packaging:** Documented `appsettings`, one-command dev startup script.
- **6.6 Retrospective:** Update TAD with what changed; decide on SSE vs SignalR and `Microsoft.Extensions.AI` migration.

**Exit criteria:** Evaluation results are recorded in `docs/`; known limits are documented.
