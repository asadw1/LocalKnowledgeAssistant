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

`200 OK`, body `{"status":"ok"}`. Reports that the process is up only. It does not check Ollama or the MCP servers *(planned: status panel, ROADMAP 5.4)*.

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
| `ErrorEvent` | `Message: string` | The run stopped abnormally (timeout or iteration cap). A `DoneEvent` still follows |
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
| Ollama unreachable or another exception inside the run | Not caught by the runner or the endpoint; the exception propagates after the response has started. Inferred from the code, not exercised live | The stream ends without a `DoneEvent`; the browser reports a network error or a truncated stream |
| Bad request body | ASP.NET Core model binding | `400` before any streaming |
| Backend down | Connection refused | `fetch` rejects, or the Vite proxy returns `5xx`; hook shows `Request failed (status)` or the fetch message |

The hook treats a missing `DoneEvent` as a normal end of stream and does not currently flag it. *(planned: a catch at the endpoint that emits an `ErrorEvent` and `DoneEvent` on unexpected failure, and a client check that a `DoneEvent` arrived; ROADMAP 5.5.)*

## 6. Heartbeats and reconnection *(planned)*

There are no keep-alive frames and no reconnection: a dropped connection ends the run and is shown as an error. Because `parseSseFrames` already ignores comment lines, adding `: keep-alive` frames on the server needs no client change. Model first-token latency can be long on a local 7B model, so this matters if a proxy with an idle timeout is ever placed in front.

## 7. Compatibility rules

- Adding a new event type: older clients skip nothing silently, since unknown `type` values land in the events list. Check the UI renders or ignores them.
- Adding a field to an event is backward compatible. Renaming or removing a field is not; update `useAgentStream.ts`, its tests and this document together.
- Renaming an event class changes the wire name.
