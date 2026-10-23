# Roadmap

Delivery plan for the Local Knowledge & File Assistant. Each phase ends in something runnable; each stage is a small, reviewable unit of work. Design details live in [TAD.md](TAD.md).

**Status key:** ✅ done · 🔜 next · ⬜ not started

| Phase | Goal | Status |
|---|---|---|
| 0 | Scaffold and documentation | ✅ |
| 1 | Streaming chat, no tools | ✅ |
| 2 | MCP filesystem tools | ✅ |
| 3 | ReAct loop with validation | ✅ |
| 4 | SQLite metadata and indexing | 🔜 (4.1–4.4 done) |
| 5 | Dashboard UI | ⬜ |
| 6 | Hardening and evaluation | ⬜ |

---

## Phase 0: Scaffold and documentation ✅

**Goal:** A building solution and frontend, with the architecture written down.

- **0.1 Solution scaffold:** `Assistant.Core`, `Assistant.Api`, `Assistant.Tests` on `net10.0`; Semantic Kernel, Ollama connector and MCP SDK referenced.
- **0.2 Frontend scaffold:** Vite, React, TypeScript, Tailwind v4, `/api` dev proxy, `useAgentStream` hook.
- **0.3 Contracts:** `IAgentRunner`, `AgentEvent` types, `IToolRegistry`, `McpServerOptions`, `/api/chat` SSE endpoint shape.
- **0.4 Docs:** README, TAD, this roadmap.

**Exit criteria:** `dotnet build` and `npm run build` succeed.

---

## Phase 1: Streaming chat, no tools ✅

**Goal:** Type a prompt in the browser and watch tokens stream from the local model.

- **1.1 Local model setup:** Start Ollama, pull `qwen2.5:7b-instruct`, confirm with `ollama run`.
- **1.2 Configuration:** `Ollama` options section (base URL, model id) bound and validated at startup.
- **1.3 Agent runner:** `SemanticKernelAgentRunner` streaming chat completions as `TokenEvent`, finishing with `DoneEvent`; cancellation flows through.
- **1.4 DI composition:** Register kernel, chat service and runner in `Program.cs`; endpoint resolves only `IAgentRunner`.
- **1.5 Minimal chat UI:** Prompt box, streamed response, running/disabled state using `useAgentStream`.
- **1.6 Tests:** Runner against a fake chat completion service (token order, cancellation, error propagation); SSE frame test for the endpoint.

**Exit criteria:** End-to-end streaming works in the browser; stopping the request stops generation; tests pass.

---

## Phase 2: MCP filesystem tools ✅

**Goal:** The app can launch a local MCP server and expose its tools to the kernel.

- **2.1 MCP client service:** Hosted service that launches stdio servers from `McpServerOptions`, owns their lifetime, disposes on shutdown.
- **2.2 Tool discovery:** List tools from each server and capture name, description and JSON Schema.
- **2.3 Kernel adapter:** `McpToolRegistry : IToolRegistry` exposes MCP tools as Semantic Kernel functions.
- **2.4 Filesystem server:** Configure the filesystem MCP server restricted to an allow-listed root; add a sample documents folder for testing.
- **2.5 Failure handling:** Server fails to start, crashes mid-run, or times out; the app stays up and reports the tool as unavailable.
- **2.6 Tests:** Registry against a stub MCP server; allow-list enforcement; one opt-in integration test with the real server.

**Exit criteria:** Startup logs list discovered filesystem tools; calling one directly through the registry returns real file data.

---

## Phase 3: ReAct loop with validation ✅

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

## Phase 4: SQLite metadata and indexing 🔜

**Goal:** Answer questions about file metadata and find documents without reading everything.

- **4.1 Schema design ✅:** Tables for files (path, size, modified, hash), tags and summaries; migration script under `data/`.
- **4.2 Indexer ✅:** Background job that scans the allow-listed root and upserts metadata; incremental via hash and mtime.
- **4.3 SQLite MCP server ✅:** Our own `Assistant.SqliteMcp`, because the npm servers surveyed did not guarantee read-only (model-controlled flag or database path, or a broken `npx` entry point). Two tools, `describe_schema` and `query`; database opened read-only with `query_only`, single `SELECT`/`WITH` only, row cap, 5 second timeout.
- **4.4 Tool selection ✅:** System prompt v2 routes metadata questions to SQLite, content questions to the filesystem, and combined ones query-then-read; it summarises the `files` table and gives a date idiom that works with the indexer's timestamps. Overlapping filesystem tools are withheld, leaving seven tools. Drift tests tie the prompt to the schema and tool config.
- **4.5 Combined scenarios:** Queries needing both, such as "which large files changed this week, and what do they contain?"
- **4.6 Tests:** Indexer on a temp directory; read-only enforcement; scenario tests with the scripted model.

**Exit criteria:** Metadata questions are answered from SQLite; content questions use the filesystem; the database cannot be modified through the agent.

---

## Phase 5: Dashboard UI

**Goal:** A usable interface that makes the agent's reasoning visible.

- **5.1 Conversation view:** Message list with markdown rendering and auto-scroll.
- **5.2 Tool activity cards:** Collapsible cards for each call (arguments, result, error state, duration).
- **5.3 Run controls:** Cancel in-flight run, retry, clear conversation.
- **5.4 Status panel:** Ollama reachability, model name, connected MCP servers and their tools.
- **5.5 Error and empty states:** Backend down, model missing, stream interrupted.
- **5.6 Frontend tests:** SSE parser edge cases (split frames, multiple events per chunk), component tests for tool cards.

**Exit criteria:** A full session is understandable from the UI alone, including failures.

---

## Phase 6: Hardening and evaluation

**Goal:** Know how well it works and keep it safe.

- **6.1 Evaluation set:** A fixed list of prompts with expected tool use and answers over a sample corpus.
- **6.2 Model comparison:** Run the set on `qwen2.5:7b-instruct` and `llama3.2:3b`; record tool-call validity rate and answer quality.
- **6.3 Prompt-injection checks:** Documents containing hostile instructions; verify tools stay read-only and the allow-list holds.
- **6.4 Observability:** Structured logs for tool calls, validation failures, iterations and latency.
- **6.5 Configuration and packaging:** Documented `appsettings`, one-command dev startup script.
- **6.6 Retrospective:** Update TAD with what changed; decide on SSE vs SignalR and `Microsoft.Extensions.AI` migration.

**Exit criteria:** Evaluation results are recorded in `docs/`; known limits are documented.
