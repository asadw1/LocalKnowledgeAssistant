# API and Real-Time Contract

**Status:** Describes the HTTP surface as built. Heartbeats, reconnection and structured error codes are *(planned)*.

Boundary between `Assistant.Api` and the React dashboard (`web`). This is hand-written Markdown, not generated OpenAPI or AsyncAPI; with two endpoints, a generated spec would be larger than the thing it describes. Revisit if the surface grows. Source of truth is `src/Assistant.Api/Program.cs`, `src/Assistant.Core/Agent/IAgentRunner.cs` and `web/src/hooks/useAgentStream.ts`.

## 1. Transport and hosting

| Item | Value |
|---|---|
| Base URL | `http://localhost:5080` (`Urls` in `appsettings.json`) |
| Dev proxy | Vite proxies `/api` to `localhost:5080`, so the browser sees one origin |
| CORS | Only `http://localhost:5173` (the Vite dev server), any header, any method |
| Auth | None. Single user, loopback only; see [SECURITY.md](SECURITY.md) |

## 2. Endpoints

### `GET /api/health`

`200 OK`, body `{"status":"ok"}`. Reports that the process is up only. It does not check Ollama or the MCP servers, and stays that way: it is the liveness probe.

### `GET /api/status` *(planned, ROADMAP 5.5)*

Not built. Intended to feed the dashboard status panel and to act as a readiness check. Checks are cheap and cached for a few seconds, and Ollama is probed with a short timeout so the endpoint never hangs when Ollama is down. It returns `200` even when a dependency is unhealthy; the body says which one.

Planned body (field names are provisional until built):

```json
{
  "ready": true,
  "ollama": { "reachable": true, "model": "qwen2.5:7b-instruct", "modelPulled": true },
  "mcpServers": [
    { "name": "filesystem", "state": "running", "tools": ["read_text_file", "list_directory"] },
    { "name": "sqlite", "state": "failed", "tools": [] }
  ],
  "index": { "fileCount": 42, "lastScanUtc": "2026-11-11T09:00:00Z", "scanning": false }
}
```

`ready` is true when Ollama is reachable, the model is pulled and at least one tool server is running. The response must not include the database path, the filesystem root, server command lines or tool arguments (see [SECURITY.md](SECURITY.md)). Failed MCP starts need `McpToolRegistry` to record them; today it only logs.

### `GET /api/runs` and `GET /api/runs/{id}` *(planned, ROADMAP 5.6)*

Not built. Read-only access to the audit trail behind the dashboard's run history panel. Runs are recorded by the API into its own audit database, separate from the metadata index and never exposed to the model.

- `GET /api/runs?limit=&before=` returns runs newest first, one summary per run: run ID, start and end time, status (`completed`, `error`, `cancelled`), prompt, tool-call count. Paged with a cursor.
- `GET /api/runs/{id}` returns that summary plus its recorded events in order (type, tool, `IsError`, duration, truncated payload) and the final answer. Unknown ID returns `404`.
- A `DELETE` or clear action is planned for retention; its shape is undecided.

Payloads are truncated, and prompts, SQL and file contents are stored as plain text, so the response may contain personal data. Field names are provisional until built.

### `POST /api/chat`

Starts one agent run and streams it.

**Request:** `Content-Type: application/json`

```json
{ "prompt": "What is the largest file?" }
```

Each request is a single turn with fresh history; nothing carries over between requests.

**Response:** `200`, `Content-Type: text/event-stream`, `Cache-Control: no-cache`. The headers are sent before the run starts, so a failure after that point cannot change the status code (section 5).

## 3. Event stream

Each frame is two lines and a blank line:

```
event: <EventTypeName>
data: <json>

```

`<EventTypeName>` is the .NET type name. `data` is that event serialised with `System.Text.Json` defaults, so **field names are PascalCase**. Frames are flushed one by one as events occur.

| `event:` | `data` fields | Meaning |
|---|---|---|
| `TokenEvent` | `Text: string` | Incremental model output. Concatenate in order to form the answer |
| `ToolCallEvent` | `Tool: string`, `ArgumentsJson: string` | The model requested a tool. `Tool` is `plugin-function` (for example `sqlite-query`). `ArgumentsJson` is a JSON-encoded string of the arguments, not a nested object |
| `ToolResultEvent` | `Tool: string`, `Result: string`, `IsError: bool` | Outcome of that call. `IsError` is true for validation failures, repeat-limit errors, tool errors and exceptions. `Result` is already truncated to `Agent:MaxToolResultChars` |
| `ErrorEvent` | `Message: string` | The run stopped abnormally (timeout, iteration cap or an unexpected failure such as Ollama being unreachable). A `DoneEvent` still follows. `Message` is written for the user and never quotes the exception text |
| *(planned)* run ID | `RunId: string` | A run identifier sent in the first event and used in the server log scope, so a UI error can be matched to the log (ROADMAP 5.1, 6.4). The carrying event and field name are not decided; adding a field is backward compatible (section 7) |
| `DoneEvent` | none (`{}`) | Terminal frame of every completed run |

Note the naming split: events carry `plugin-function`, while the model itself sees `plugin_function`.

### Ordering guarantees

- A `ToolCallEvent` is always followed by its `ToolResultEvent` before the next `ToolCallEvent`.
- Calls requested together in one model turn are executed and reported one after another, in request order.
- `TokenEvent`s can appear before a tool call in the same turn (the model's "thinking aloud") and after the last result (the answer).
- Every run that ends normally, by timeout or by iteration cap, ends with `DoneEvent`; `ErrorEvent`, when present, comes immediately before it.

### Example

```
event: ToolCallEvent
data: {"Tool":"sqlite-query","ArgumentsJson":"{\"sql\":\"SELECT path FROM files ORDER BY size_bytes DESC LIMIT 1\"}"}

event: ToolResultEvent
data: {"Tool":"sqlite-query","Result":"{\"columns\":[\"path\"],\"rows\":[[\"C:\\\\...\\\\welcome.md\"]],\"rowCount\":1,\"truncated\":false}","IsError":false}

event: TokenEvent
data: {"Text":"The largest file is welcome.md."}

event: DoneEvent
data: {}

```

## 4. Client behaviour (`useAgentStream`)

- Uses `fetch` with `POST`, not `EventSource`, because `EventSource` cannot send a body.
- Reads the body as a text stream and passes `buffer + chunk` to `parseSseFrames`, which returns complete frames and the unfinished tail, kept for the next chunk. A frame is complete only after its blank line.
- A frame with no `event:` line or no `data:` line is skipped (this also makes `: comment` keep-alive lines harmless). Malformed JSON in `data:` throws and surfaces as an error.
- Each parsed event is stored as `{ type, ...data }`.
- `stop()` aborts the request. An abort is not shown as an error. On the server the aborted request cancels the run through the request's `CancellationToken`, which reaches the model and any tool call.

## 5. Errors

There are no structured error codes yet. What a client can see today:

| Situation | What happens | Client sees |
|---|---|---|
| Run timeout (`Agent:RunTimeoutSeconds`, 120) | Run stops | `ErrorEvent` then `DoneEvent` |
| Iteration cap (`Agent:MaxIterations`, 8) | Run stops | `ErrorEvent` then `DoneEvent` |
| Bad tool call, tool error, repeated call | Fed back to the model | `ToolResultEvent` with `IsError: true`; the run continues |
| Client disconnects or calls `stop()` | Run cancelled | Nothing further |
| Ollama unreachable (`HttpRequestException`, or an `HttpClient` timeout) | Caught in the runner and logged at Error | `ErrorEvent` ("Could not reach the model server...") then `DoneEvent`. Covered by unit tests with a throwing fake model, not yet exercised against a stopped Ollama |
| Any other exception inside the run (including tool registration) | Caught in the runner and logged at Error | `ErrorEvent` ("The run failed unexpectedly. See the server log...") then `DoneEvent`; any tokens already sent are kept |
| Bad request body | ASP.NET Core model binding | `400` before any streaming |
| Backend down | Connection refused | `fetch` rejects, or the Vite proxy returns `5xx`; hook shows `Request failed (status)` or the fetch message |

The hook treats a missing `DoneEvent` as a normal end of stream and does not currently flag it. *(planned: a client check that a `DoneEvent` arrived, and rendering `ErrorEvent`; ROADMAP 5.1.)* A failure that happens before the runner starts (for example a dropped connection) can still end the stream without a `DoneEvent`.

## 6. Heartbeats and reconnection *(planned)*

There are no keep-alive frames and no reconnection: a dropped connection ends the run and is shown as an error. Because `parseSseFrames` already ignores comment lines, adding `: keep-alive` frames on the server needs no client change. Model first-token latency can be long on a local 7B model, so this matters if a proxy with an idle timeout is ever placed in front.

## 7. Compatibility rules

- Adding a new event type: older clients skip nothing silently, since unknown `type` values land in the events list. Check the UI renders or ignores them.
- Adding a field to an event is backward compatible. Renaming or removing a field is not; update `useAgentStream.ts`, its tests and this document together.
- Renaming an event class changes the wire name.
